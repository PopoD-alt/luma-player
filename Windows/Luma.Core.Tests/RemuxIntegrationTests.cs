using System.Buffers.Binary;
using System.Text;
using Luma.Core.Containers;
using Luma.Core.Audio;

namespace Luma.Core.Tests;

internal static class RemuxIntegrationTests
{
    public static void Run(string fixtures)
    {
        var model = MatroskaDemuxer.Demux(File.ReadAllBytes(Path.Combine(fixtures,"Luma-AVC-AAC.mkv")));
        var video = model.Tracks.Single(t=>t.TrackType==1);
        var audio = model.Tracks.Single(t=>t.TrackType==2);
        foreach (bool includeAudio in new[]{false,true})
        {
            using var output = new MemoryStream();
            var result = MatroskaRemuxer.Remux(model,output,includeAudio:includeAudio);
            byte[] bytes = output.ToArray();
            var tracks = Children(bytes,0,bytes.Length).Single(b=>b.Type=="moov");
            var written = Children(bytes,tracks.Start,tracks.End).Where(b=>b.Type=="trak").ToArray();
            Check(written.Length==(includeAudio?2:1),"Explicit audio selection changed track count");
            VerifyPackets(bytes,written[0],video.Packets);
            if(includeAudio) VerifyPackets(bytes,written[1],audio.Packets);
            Check(result.OriginNanoseconds==250_000_000 && result.SelectedAudioTrackId==(includeAudio?audio.Id:null),"Movie origin or selected audio metadata");
            Check(result.AudioTracks.Count==1&&result.AudioTracks[0].Packets.Count==0,"Audio menu metadata unnecessarily retains packet payloads");
            var export=Environment.GetEnvironmentVariable("LUMA_WINDOWS_MEDIA_EXPORT");
            if(!string.IsNullOrWhiteSpace(export))
            {
                Directory.CreateDirectory(export);
                File.WriteAllBytes(Path.Combine(export,includeAudio?"Luma-remux-av.mp4":"Luma-remux-video.mp4"),bytes);
                ExportAudio(export);
            }
        }
        var alternate=audio with { Id=audio.Id+100,Name="Alternate test audio",IsDefault=false };
        var alternatives=model with { Tracks=[..model.Tracks,alternate] };
        using(var output=new MemoryStream())
        {
            var selected=MatroskaRemuxer.Remux(alternatives,output,alternate.Id);
            Check(selected.SelectedAudioTrackId==alternate.Id&&selected.AudioTracks.Count==2,"Alternate AAC track not exposed or selected");
        }
        Reject(()=>{ using var output=new MemoryStream();MatroskaRemuxer.Remux(model,output,9999); });
        try { using var output=new MemoryStream();MatroskaRemuxer.Remux(model,output,cancellationToken:new CancellationToken(true));throw new Exception("Cancelled remux accepted"); } catch(OperationCanceledException) { }
        Console.WriteLine("PASS: original MKV→MP4 AVC/AAC pipeline exact compressed packets via independent co64/stsz traversal, A/V origin, explicit audio-off/default/alternate track selection and cancellation");
    }
    static void ExportAudio(string path)
    {
        const int rate=44100, count=rate*3;
        byte[] pcm=new byte[count*2];
        for(int i=0;i<count;i++) BinaryPrimitives.WriteInt16BigEndian(pcm.AsSpan(i*2),(short)(4000*Math.Sin(i*440*2*Math.PI/rate)));
        byte[] aiff=new byte[54+pcm.Length];
        "FORM"u8.CopyTo(aiff);BinaryPrimitives.WriteUInt32BigEndian(aiff.AsSpan(4),(uint)(aiff.Length-8));"AIFFCOMM"u8.CopyTo(aiff.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(aiff.AsSpan(16),18);BinaryPrimitives.WriteUInt16BigEndian(aiff.AsSpan(20),1);BinaryPrimitives.WriteUInt32BigEndian(aiff.AsSpan(22),count);BinaryPrimitives.WriteUInt16BigEndian(aiff.AsSpan(26),16);
        new byte[]{0x40,0x0e,0xac,0x44,0,0,0,0,0,0}.CopyTo(aiff,28);"SSND"u8.CopyTo(aiff.AsSpan(38));BinaryPrimitives.WriteUInt32BigEndian(aiff.AsSpan(42),(uint)(pcm.Length+8));pcm.CopyTo(aiff,54);
        byte[] au=new byte[24+pcm.Length];".snd"u8.CopyTo(au);BinaryPrimitives.WriteUInt32BigEndian(au.AsSpan(4),24);BinaryPrimitives.WriteUInt32BigEndian(au.AsSpan(8),(uint)pcm.Length);BinaryPrimitives.WriteUInt32BigEndian(au.AsSpan(12),3);BinaryPrimitives.WriteUInt32BigEndian(au.AsSpan(16),rate);BinaryPrimitives.WriteUInt32BigEndian(au.AsSpan(20),1);pcm.CopyTo(au,24);
        var first=PcmContainerDecoder.Decode(aiff);var second=PcmContainerDecoder.Decode(au);
        Check(first.Samples.SequenceEqual(second.Samples)&&first.Duration==3&&second.Duration==3,"Exported AIFF/AU review fixtures");
        File.WriteAllBytes(Path.Combine(path,"Luma-Windows-Tone.aiff"),aiff);File.WriteAllBytes(Path.Combine(path,"Luma-Windows-Tone.au"),au);
    }
    sealed record Box(string Type,int Start,int End);
    static List<Box> Children(byte[] data,int start,int end)
    {
        var result=new List<Box>();
        while(start<end)
        {
            Check(start+8<=end,"Truncated MP4 box header");uint length=U32(data,start);int header=8;
            long size=length;
            if(length==1) { Check(start+16<=end,"Truncated extended MP4 box");size=checked((long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(start+8)));header=16; }
            Check(size>=header&&size<=end-start,"Invalid MP4 box extent");
            result.Add(new(Encoding.ASCII.GetString(data,start+4,4),start+header,checked(start+(int)size)));start=checked(start+(int)size);
        }
        return result;
    }
    static Box Child(byte[] data,Box parent,string type)=>Children(data,parent.Start,parent.End).Single(b=>b.Type==type);
    static uint U32(byte[] data,int offset)=>BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
    static void VerifyPackets(byte[] bytes,Box track,IReadOnlyList<MediaPacket> packets)
    {
        var stbl=Child(bytes,Child(bytes,Child(bytes,track,"mdia"),"minf"),"stbl");
        var sizes=Child(bytes,stbl,"stsz");var offsets=Child(bytes,stbl,"co64");
        Check(U32(bytes,sizes.Start+4)==0&&U32(bytes,sizes.Start+8)==packets.Count&&U32(bytes,offsets.Start+4)==packets.Count,"Sample count/size table");
        var mdat=Children(bytes,0,bytes.Length).Single(b=>b.Type=="mdat");
        for(int i=0;i<packets.Count;i++)
        {
            int length=checked((int)U32(bytes,sizes.Start+12+i*4));
            long offset=checked((long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offsets.Start+8+i*8)));
            Check(offset>=mdat.Start&&offset+length<=mdat.End,"Sample offset lies outside mdat");
            Check(bytes.AsSpan((int)offset,length).SequenceEqual(packets[i].Data),"Compressed packet bytes or coding order changed");
        }
    }
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch(Exception e) when(e is InvalidDataException or NotSupportedException) { return; } throw new Exception("Invalid remux accepted"); }
}
