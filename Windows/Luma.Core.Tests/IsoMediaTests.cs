using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Luma.Core.Containers;

namespace Luma.Core.Tests;

/// <summary>Independent box/table checks. Synthetic NAL/AAC payloads test mux framing, not compressed decoding.</summary>
public static class IsoMediaTests
{
    private static readonly byte[] Avcc = [1,66,0,30,255,225,0,5,0x67,0x42,0,0x1e,0xda,1,0,4,0x68,0xce,6,0xe2];
    public static void Run(string fixturesRoot)
    {
        _ = fixturesRoot; // Native playback of real fixtures belongs to the Windows app smoke test.
        const long frame=40_000_000;
        Mp4Sample[] videoSamples = [new(Nal(0x65,1),0,0,frame,true),new(Nal(0x41,2),frame,frame*2,frame,false),new(Nal(0x41,3),frame*2,frame,frame,false)];
        Mp4Sample[] audioSamples = [new([1,2,3],20_000_000,20_000_000,23_219_954,true),new([4,5],43_219_954,43_219_954,23_219_955,true)];
        var video=new Mp4Track(3,"V_MPEG4/ISO/AVC",Avcc,64,48,0,0,videoSamples);
        var audio=new Mp4Track(7,"A_AAC",[0x12,0x10],0,0,44100,2,audioSamples);
        byte[] file=Mux(video,audio);
        Box[] top=Boxes(file,0,file.Length);Check(top.Select(b=>b.Type).SequenceEqual(new[]{"ftyp","mdat","moov"}),"MP4 top-level layout");
        Box moov=top.Single(b=>b.Type=="moov"),mdat=top.Single(b=>b.Type=="mdat");
        Box mvhd=Child(file,moov,"mvhd");Check(file[mvhd.Body]==1 && U32(file,mvhd.Body+20)==IsoMediaMuxer.TimeScale && U64(file,mvhd.Body+24)==120_000_000,"64-bit movie header timing");
        var trackBoxes=Boxes(file,moov.Body,moov.End).Where(b=>b.Type=="trak").ToArray();Check(trackBoxes.Length==2,"No input track dropped");
        VerifyTrack(file,trackBoxes[0],video,mdat,0);
        VerifyTrack(file,trackBoxes[1],audio,mdat,0);
        Box vstbl=SampleTable(file,trackBoxes[0]);Box ctts=Child(file,vstbl,"ctts");
        Check(file[ctts.Body]==1 && Expand(file,ctts,true).SequenceEqual(new long[]{0,frame,-frame}),"Signed version1 composition offsets preserve coded B-frame order");
        Box stss=Child(file,vstbl,"stss");Check(U32(file,stss.Body+4)==1 && U32(file,stss.Body+8)==1,"Sync sample indices are one-based");
        Check(Entry(file,vstbl).Type=="avc1","AVC out-of-band sample entry");
        Check(Child(file,Entry(file,vstbl),"avcC",78).Payload(file).SequenceEqual(Avcc),"avcC retained byte for byte");
        VerifyAsc(file,SampleTable(file,trackBoxes[1]),audio.CodecPrivate);

        // Negative timestamps share one origin; media-time edits retain a
        // positive decode/composition delay without rewriting signed CTS offsets.
        var delayed = video with { Samples=videoSamples.Select(s=>s with {DtsNanoseconds=s.DtsNanoseconds-20_000_000,PtsNanoseconds=s.PtsNanoseconds+60_000_000}).ToArray() };
        var early = audio with {Samples=audioSamples.Select(s=>s with {DtsNanoseconds=s.DtsNanoseconds-60_000_000,PtsNanoseconds=s.PtsNanoseconds-60_000_000}).ToArray()};
        byte[] offsetFile=Mux(delayed,early);Box offsetMoov=Boxes(offsetFile,0,offsetFile.Length).Single(b=>b.Type=="moov");
        Box[] offsetTracks=Boxes(offsetFile,offsetMoov.Body,offsetMoov.End).Where(b=>b.Type=="trak").ToArray();
        VerifyTrack(offsetFile,offsetTracks[0],delayed,Boxes(offsetFile,0,offsetFile.Length).Single(b=>b.Type=="mdat"),-40_000_000);
        VerifyTrack(offsetFile,offsetTracks[1],early,Boxes(offsetFile,0,offsetFile.Length).Single(b=>b.Type=="mdat"),-40_000_000);
        var far=video with{Samples=[new(Nal(0x65,9),10_000_000_000,10_000_000_000,frame,true)]};
        byte[] farFile=Mux(far,audio with{Samples=[new([1],0,0,23_219_954,true)]});
        Box farMvhd=Child(farFile,Boxes(farFile,0,farFile.Length).Single(b=>b.Type=="moov"),"mvhd");
        Check(U64(farFile,farMvhd.Body+24)==10_040_000_000,"Movie duration is not truncated to32-bit nanoseconds");

        byte[] hvcc=HevcConfig(true);var hevc=video with{Id=9,CodecId="V_MPEGH/ISO/HEVC",CodecPrivate=hvcc,Samples=[new(Nal(0x26,1,4),0,0,frame,true)]};
        byte[] hevcFile=Mux(hevc);Box hevcEntry=FirstEntry(hevcFile);Check(hevcEntry.Type=="hvc1","Complete HEVC config chooses hvc1");
        Check(Child(hevcFile,hevcEntry,"hvcC",78).Payload(hevcFile).SequenceEqual(hvcc),"hvcC retained byte for byte");
        Check(FirstEntry(Mux(hevc with{CodecPrivate=HevcConfig(false)})).Type=="hev1","Incomplete HEVC arrays require hev1");
        byte[] inline = Nal(0x67,9).Concat(Nal(0x65,7)).ToArray();
        Check(FirstEntry(Mux(video with{Samples=[new(inline,0,0,frame,true)]})).Type=="avc3","In-band parameter sets retained with avc3 signaling");
        foreach(int width in new[]{1,2,4})
        {
            byte[] config=(byte[])Avcc.Clone();config[4]=(byte)(0xfc|width-1);
            byte[] withPrefix=NalWithPrefix(width,[0x65,0x88]);byte[] prefixed=Mux(video with{CodecPrivate=config,Samples=[new(withPrefix,0,0,frame,true)]});
            Check(prefixed.AsSpan(FindPayload(prefixed),withPrefix.Length).SequenceEqual(withPrefix),"Original NAL length-prefix width retained");
        }
        MalformedTests(video,audio,hevc);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();using var empty=new MemoryStream();
        try{IsoMediaMuxer.Write(empty,[video],cancelled.Token);throw new Exception("Ignored mux cancellation");}catch(OperationCanceledException){}
        Check(empty.Length==0,"Cancellation before output preserves empty stream");
        Console.WriteLine("PASS: original ISO BMFF packet-byte preservation, avcC/hvcC/esds ASC, stts/signedctts B-frames, elst common-origin offsets, stsc/stsz/co64/stss and64-bit movie timing");
        Console.WriteLine("PASS: mux malformed/unsupported config, NAL framing, AACHE/PS limits, noncontiguous DTS, resource/timestamp/output bounds and cancellation; compressed decoding is not claimed");
    }

    private static void VerifyTrack(byte[] file,Box box,Mp4Track track,Box mdat,long origin)
    {
        Box tkhd=Child(file,box,"tkhd");Check(U32(file,tkhd.Body+20)==track.Id,"Track ID retained");
        Box mdhd=Child(file,Child(file,box,"mdia"),"mdhd");Check(U32(file,mdhd.Body+20)==IsoMediaMuxer.TimeScale,"Media nanosecond timescale");
        long sum=track.Samples.Sum(s=>s.DurationNanoseconds);Check(U64(file,mdhd.Body+24)==(ulong)sum,"Decode duration retained");
        Box table=SampleTable(file,box),sizes=Child(file,table,"stsz"),offsets=Child(file,table,"co64"),stsc=Child(file,table,"stsc");
        Check(U32(file,sizes.Body+4)==0 && U32(file,sizes.Body+8)==track.Samples.Count && U32(file,offsets.Body+4)==track.Samples.Count,"Sample/chunk counts");
        Check(U32(file,stsc.Body+4)==1 && U32(file,stsc.Body+8)==1 && U32(file,stsc.Body+12)==1 && U32(file,stsc.Body+16)==1,"One exact packet per chunk");
        for(int i=0;i<track.Samples.Count;i++)
        {
            int size=(int)U32(file,sizes.Body+12+i*4);ulong offset=U64(file,offsets.Body+8+i*8);
            Check(size==track.Samples[i].Data.Length && offset>=(ulong)mdat.Body && offset+(ulong)size<=(ulong)mdat.End,"Chunk offset/size inside mdat");
            Check(file.AsSpan((int)offset,size).SequenceEqual(track.Samples[i].Data),"Compressed packet bytes retained");
        }
        long[] durations=Expand(file,Child(file,table,"stts"),false);Check(durations.SequenceEqual(track.Samples.Select(s=>s.DurationNanoseconds)),"stts exact durations");
        Box elst=Child(file,Child(file,box,"edts"),"elst");Check(file[elst.Body]==1,"64-bit edit list version");
        long minPts=track.Samples.Min(s=>s.PtsNanoseconds),lead=minPts-origin,mediaStart=minPts-track.Samples[0].DtsNanoseconds;
        int entries=(int)U32(file,elst.Body+4),edit=elst.Body+8;
        Check(entries==(lead>0?2:1),"Edit entry count");
        if(lead>0){Check(U64(file,edit)==(ulong)lead && I64(file,edit+8)==-1,"Empty edit retains track lead");edit+=20;}
        long presentation=track.Samples.Max(s=>s.PtsNanoseconds+s.DurationNanoseconds)-minPts;
        Check(U64(file,edit)==(ulong)presentation && I64(file,edit+8)==mediaStart,"Media edit retains composition origin/end");
        Box? composition=Boxes(file,table.Body,table.End).Cast<Box?>().FirstOrDefault(b=>b!.Value.Type=="ctts");
        long[] delta=composition.HasValue?Expand(file,composition.Value,true):new long[track.Samples.Count];
        long decode=0;
        for(int i=0;i<track.Samples.Count;i++)
        {
            Check(lead+decode+delta[i]-mediaStart==track.Samples[i].PtsNanoseconds-origin,"Movie presentation timestamp retained through edits");
            Check(lead+decode-mediaStart==track.Samples[i].DtsNanoseconds-origin,"Movie decode timestamp retained through edits");decode+=durations[i];
        }
    }
    private static void VerifyAsc(byte[] file,Box table,byte[] expected)
    {
        Box entry=Entry(file,table);Check(entry.Type=="mp4a","AAC sample entry");Box esds=Child(file,entry,"esds",28);
        (int tag,int start,int end)=Descriptor(file,esds.Body+4);Check(tag==3,"ES descriptor");
        (int decoder,int config,int next)=Descriptor(file,start+3);Check(decoder==4 && file[config]==0x40 && file[config+1]==0x15,"MPEG4 audio decoder descriptor");
        (int dsi,int asc,int ascEnd)=Descriptor(file,config+13);Check(dsi==5 && file.AsSpan(asc,ascEnd-asc).SequenceEqual(expected),"Exact ASC in esds DecoderSpecificInfo");
        (int sl,int predefined,int slEnd)=Descriptor(file,next);Check(sl==6 && file[predefined]==2 && slEnd==end,"MP4 SL descriptor");
    }
    private static (int Tag,int Body,int End) Descriptor(byte[] file,int offset)
    {int tag=file[offset++],length=0,b;do{b=file[offset++];length=(length<<7)|(b&127);}while((b&128)!=0);return(tag,offset,offset+length);}
    private static void MalformedTests(Mp4Track video,Mp4Track audio,Mp4Track hevc)
    {
        void Reject(params Mp4Track[] tracks)
        {
            using var output=new MemoryStream();try{IsoMediaMuxer.Write(output,tracks);}catch(Exception e)when(e is InvalidDataException or NotSupportedException){Check(output.Length==0,"Invalid input produced partial output");return;}
            throw new Exception("Accepted malformed/unsupported MP4 input");
        }
        Reject();Reject(video,video);Reject(video with{Id=0});Reject(video with{Samples=[]});Reject(video with{CodecId="V_VP9"});
        Reject(video with{Samples=[video.Samples[0] with{Keyframe=false}]});
        Reject(video with{Samples=[video.Samples[0] with{DurationNanoseconds=0}]});
        Reject(video with{Samples=[video.Samples[0],video.Samples[1] with{DtsNanoseconds=1}]});
        Reject(video with{Samples=[video.Samples[0] with{PtsNanoseconds=-1}]});
        Reject(video with{Samples=[video.Samples[0] with{PtsNanoseconds=(long)int.MaxValue+1}]});
        Reject(video with{Samples=[video.Samples[0] with{Data=[0,0,0,9,0x65]}]});
        Reject(video with{Samples=[video.Samples[0] with{Data=[0,0,0,0]}]});
        byte[] invalid=(byte[])Avcc.Clone();invalid[4]=0xfe;Reject(video with{CodecPrivate=invalid});
        invalid=(byte[])Avcc.Clone();invalid[5]=0xe0;Reject(video with{CodecPrivate=invalid});
        Reject(video with{CodecPrivate=Avcc[..^1]});Reject(hevc with{CodecPrivate=HevcConfig(true)[..^1]});
        Reject(audio with{SampleRate=48000});Reject(audio with{CodecPrivate=[0x2b,0x92,8]});
        Reject(audio with{SampleRate=24000,CodecPrivate=[0x13,0x10,0x56,0xe5,0x98]});
        Reject(audio with{CodecPrivate=[0x12,0]});Reject(audio with{CodecPrivate=[0xff,0xff]});
        Reject(video with{Samples=[null!]});
        using var occupied=new MemoryStream();occupied.WriteByte(1);occupied.Position=0;
        try{IsoMediaMuxer.Write(occupied,[video]);throw new Exception("Accepted occupied MP4 output");}catch(NotSupportedException){}
        Check(occupied.ToArray().SequenceEqual(new byte[]{1}),"Existing output bytes preserved");
    }
    private static byte[] HevcConfig(bool complete)
    {
        var bytes=new List<byte>(new byte[23]);bytes[0]=1;bytes[1]=1;bytes[12]=120;bytes[13]=0xf0;bytes[15]=0xfc;bytes[16]=0xfd;bytes[17]=0xf8;bytes[18]=0xf8;bytes[21]=3;bytes[22]=3;
        foreach(int type in new[]{32,33,34})bytes.AddRange(new byte[]{(byte)(type|(complete?128:0)),0,1,0,2,(byte)(type<<1),1});return bytes.ToArray();
    }
    private static byte[] Nal(params byte[] payload)=>NalWithPrefix(4,payload);
    private static byte[] NalWithPrefix(int prefix,byte[] payload){byte[] result=new byte[prefix+payload.Length];for(int i=0;i<prefix;i++)result[prefix-i-1]=(byte)(payload.Length>>(i*8));payload.CopyTo(result,prefix);return result;}
    private static byte[] Mux(params Mp4Track[] tracks){using var output=new MemoryStream();IsoMediaMuxer.Write(output,tracks);return output.ToArray();}
    private static int FindPayload(byte[] file)=>Boxes(file,0,file.Length).Single(b=>b.Type=="mdat").Body;
    private static Box FirstEntry(byte[] file){Box moov=Boxes(file,0,file.Length).Single(b=>b.Type=="moov"),track=Boxes(file,moov.Body,moov.End).Single(b=>b.Type=="trak");return Entry(file,SampleTable(file,track));}
    private static Box Entry(byte[] file,Box table){Box stsd=Child(file,table,"stsd");return Boxes(file,stsd.Body+8,stsd.End).Single();}
    private static Box SampleTable(byte[] file,Box track)=>Child(file,Child(file,Child(file,track,"mdia"),"minf"),"stbl");
    private static long[] Expand(byte[] file,Box table,bool signed)
    {
        var result=new List<long>();int count=(int)U32(file,table.Body+4);
        for(int i=0;i<count;i++){int at=table.Body+8+i*8,n=(int)U32(file,at);long value=signed?BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at+4)):U32(file,at+4);for(int j=0;j<n;j++)result.Add(value);}return result.ToArray();
    }
    private readonly record struct Box(string Type,int Start,int Size){public int Body=>Start+8;public int End=>Start+Size;public byte[] Payload(byte[] file)=>file[Body..End];}
    private static Box[] Boxes(byte[] file,int start,int end)
    {
        var boxes=new List<Box>();while(start<end){Check(end-start>=8,"Truncated mux box");uint size=U32(file,start);Check(size>=8 && size<=end-start,"Mux box length");boxes.Add(new(Encoding.ASCII.GetString(file,start+4,4),start,(int)size));start+=(int)size;}Check(start==end,"Mux box parent bound");return boxes.ToArray();
    }
    private static Box Child(byte[] file,Box parent,string type,int skip=0)=>Boxes(file,parent.Body+skip,parent.End).Single(b=>b.Type==type);
    private static uint U32(byte[] file,int offset)=>BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset));
    private static ulong U64(byte[] file,int offset)=>BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(offset));
    private static long I64(byte[] file,int offset)=>BinaryPrimitives.ReadInt64BigEndian(file.AsSpan(offset));
    private static void Check(bool condition,string label){if(!condition)throw new Exception("ISO media test failed: "+label);}
}
