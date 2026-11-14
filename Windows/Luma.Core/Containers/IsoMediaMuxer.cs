using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Luma.Core.Containers;

public sealed record Mp4Sample(byte[] Data, long DtsNanoseconds, long PtsNanoseconds, long DurationNanoseconds, bool Keyframe);
public sealed record Mp4Track(uint Id, string CodecId, byte[] CodecPrivate, int Width, int Height,
    int SampleRate, int Channels, IReadOnlyList<Mp4Sample> Samples);

/// <summary>
/// Original non-fragmented ISO BMFF muxing. Compressed sample/configuration bytes
/// are retained; this is not an AVC, HEVC or AAC decoder. Native playback depends
/// on the installed Windows facilities. Caller owns partial-output cleanup after
/// I/O failure and must keep input arrays/lists stable until Write returns.
///
/// Syntax references: ISO/IEC 14496-12, clauses8.3–8.7; 14496-15 avcC/hvcC;
/// 14496-1 ES_Descriptor and 14496-3 AudioSpecificConfig. Official registries:
/// https://www.iso.org/obp/ui/#iso:std:iso-iec:14496:-12:ed-8:v1:en
/// https://mp4ra.org/registered-types/boxes
/// https://mp4ra.org/registered-types/codecs
/// </summary>
public static class IsoMediaMuxer
{
    public const uint TimeScale = 1_000_000_000;
    public const int TrackLimit = 16, SampleLimit = 1_000_000, SampleBytesLimit = 16_777_216;
    public const long PayloadLimit = 536_870_912;
    private const long TimestampLimit = 86_400_000_000_000; // bounded24-hour timeline

    /// <summary>
    /// Write to an empty, writable seekable stream. DTS must be contiguous in
    /// coded order; CTS offsets fit signed32-bit nanoseconds. The earliest PTS
    /// becomes movie time0. Per-track edit lists retain relative starts and map
    /// composition media time to that common origin. minPTS must be >= firstDTS.
    /// Currently accepts AVC/HEVC plus mono/stereo/5.1 AAC-LC at7350–64000Hz; HE/SBR,
    /// PS, PCE/core-coder extensions and encrypted samples are not implemented.
    /// Every input is validated before the first output byte is written.
    /// </summary>
    public static void Write(Stream output, IReadOnlyList<Mp4Track> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(tracks);
        cancellationToken.ThrowIfCancellationRequested();
        if (!output.CanWrite || !output.CanSeek || output.Position != 0 || output.Length != 0)
            throw new NotSupportedException("MP4 output must be an empty writable seekable stream.");
        if (tracks.Count is < 1 or > TrackLimit) throw new InvalidDataException("MP4 track count exceeds the1–16 bound.");
        var prepared = new List<PreparedTrack>(); var identifiers = new HashSet<uint>();
        long payloadBytes = 0, origin = long.MaxValue; int totalSamples = 0;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (track is null || track.CodecPrivate is null || track.Samples is null || track.Id is 0 or > 65535 || !identifiers.Add(track.Id))
                throw new InvalidDataException("MP4 track IDs must be distinct nonzero16-bit values.");
            if (track.Samples.Count == 0 || track.Samples.Count > SampleLimit - totalSamples)
                throw new InvalidDataException("MP4 samples exceed the one-million bound or a track is empty.");
            if (track.Samples[0] is null) throw new InvalidDataException("MP4 has a null first sample.");
            totalSamples += track.Samples.Count;
            PreparedTrack ready = Prepare(track, cancellationToken);
            foreach (var sample in track.Samples)
            {
                payloadBytes += sample.Data.Length;
                if (payloadBytes > PayloadLimit) throw new InvalidDataException("MP4 payload exceeds512MiB.");
            }
            origin = Math.Min(origin, ready.MinPts); prepared.Add(ready);
        }
        byte[] ftyp = BoxBytes(w => w.Box("ftyp", b => { b.Text("isom"); b.U32(0x200); b.Text("isomiso6mp41"); }));
        ulong nextOffset = (ulong)ftyp.Length + 8;
        long movieDuration = 0;
        foreach (var track in prepared)
        {
            track.Lead = track.MinPts - origin;
            track.MovieDuration = checked(track.Lead + track.PresentationDuration);
            if (track.MovieDuration > TimestampLimit) throw new NotSupportedException("MP4 movie timeline exceeds24hours.");
            movieDuration = Math.Max(movieDuration, track.MovieDuration);
            for (int i = 0; i < track.Track.Samples.Count; i++)
            {
                track.Offsets[i] = nextOffset;
                nextOffset += (ulong)track.Track.Samples[i].Data.Length;
            }
        }
        byte[] moov = BoxBytes(w => w.Box("moov", b =>
        {
            MovieHeader(b, movieDuration, tracks.Max(t => t.Id) + 1);
            foreach (var track in prepared) { cancellationToken.ThrowIfCancellationRequested(); TrackBox(b, track, cancellationToken); }
        }));
        if (moov.Length > 67_108_864) throw new InvalidDataException("MP4 sample tables exceed64MiB.");
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(ftyp);
        Span<byte> mdat = stackalloc byte[8]; BinaryPrimitives.WriteUInt32BigEndian(mdat,(uint)(payloadBytes+8)); "mdat"u8.CopyTo(mdat[4..]); output.Write(mdat);
        foreach (var track in prepared)
            foreach (var sample in track.Track.Samples)
                for (int offset = 0; offset < sample.Data.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(65_536,sample.Data.Length-offset); output.Write(sample.Data.AsSpan(offset,count)); offset += count;
                }
        cancellationToken.ThrowIfCancellationRequested(); output.Write(moov);
    }

    private sealed class PreparedTrack(Mp4Track track, bool video, string entry, int lengthBytes)
    {
        public readonly Mp4Track Track = track;
        public readonly bool Video = video;
        public string Entry = entry;
        public readonly int LengthBytes = lengthBytes;
        public long MinPts, DecodeDuration, PresentationDuration, MediaStart, Lead, MovieDuration;
        public readonly ulong[] Offsets = new ulong[track.Samples.Count];
        public readonly List<(uint Count,uint Duration)> DecodeRuns = [];
        public readonly List<(uint Count,int Offset)> CompositionRuns = [];
    }

    private static PreparedTrack Prepare(Mp4Track track, CancellationToken cancellationToken)
    {
        bool video = track.CodecId is "V_MPEG4/ISO/AVC" or "V_MPEGH/ISO/HEVC";
        int nalBytes = 0; string entry;
        if (video)
        {
            if (track.Width is < 1 or > 8192 || track.Height is < 1 or > 8192 || (long)track.Width * track.Height > 16_777_216)
                throw new InvalidDataException("MP4 video dimensions exceed the supported bounds.");
            if (track.CodecId == "V_MPEG4/ISO/AVC") { nalBytes = ValidateAvc(track.CodecPrivate); entry = "avc1"; }
            else { (int length, bool complete) = ValidateHevc(track.CodecPrivate); nalBytes = length; entry = complete ? "hvc1" : "hev1"; }
            if (!track.Samples[0].Keyframe) throw new NotSupportedException("MP4 video must begin at a declared sync sample.");
        }
        else if (track.CodecId == "A_AAC") { ValidateAac(track); entry = "mp4a"; }
        else throw new NotSupportedException("MP4 muxing does not implement codec " + track.CodecId + ".");
        var ready = new PreparedTrack(track,video,entry,nalBytes);
        long firstDts = track.Samples[0].DtsNanoseconds, expectedDts = firstDts, minPts = long.MaxValue, maxEnd = long.MinValue;
        foreach (var sample in track.Samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sample is null || sample.Data is null || sample.Data.Length is < 1 or > SampleBytesLimit)
                throw new InvalidDataException("MP4 sample size is outside1–16MiB.");
            if (sample.DtsNanoseconds is < -TimestampLimit or > TimestampLimit || sample.PtsNanoseconds is < -TimestampLimit or > TimestampLimit ||
                sample.DurationNanoseconds is <= 0 or > uint.MaxValue)
                throw new NotSupportedException("MP4 sample timestamp/duration exceeds bounded nanosecond timing.");
            if (sample.DtsNanoseconds != expectedDts) throw new NotSupportedException("MP4 requires a complete contiguous coded-order DTS timeline.");
            expectedDts = checked(expectedDts + sample.DurationNanoseconds);
            long cts = sample.PtsNanoseconds - sample.DtsNanoseconds;
            if (cts is < int.MinValue or > int.MaxValue) throw new NotSupportedException("MP4 composition offset exceeds signed32-bit nanoseconds.");
            AddDecodeRun(ready.DecodeRuns,(uint)sample.DurationNanoseconds);
            AddCompositionRun(ready.CompositionRuns,(int)cts);
            minPts = Math.Min(minPts,sample.PtsNanoseconds); maxEnd = Math.Max(maxEnd,checked(sample.PtsNanoseconds+sample.DurationNanoseconds));
            if (video && ValidatePacket(sample.Data,nalBytes,track.CodecId == "V_MPEGH/ISO/HEVC",cancellationToken))
                ready.Entry = track.CodecId == "V_MPEG4/ISO/AVC" ? "avc3" : "hev1";
        }
        if (minPts < firstDts) throw new NotSupportedException("MP4 minimum PTS precedes the supplied first DTS; reconstruct the decode timeline first.");
        ready.MinPts = minPts; ready.MediaStart = minPts-firstDts; ready.DecodeDuration = expectedDts-firstDts;
        ready.PresentationDuration = maxEnd-minPts;
        if (ready.DecodeDuration > TimestampLimit || ready.PresentationDuration > TimestampLimit) throw new NotSupportedException("MP4 track exceeds24hours.");
        return ready;
    }

    private static void AddDecodeRun(List<(uint Count,uint Duration)> runs,uint value)
    { if (runs.Count>0 && runs[^1].Duration==value) runs[^1]=(runs[^1].Count+1,value); else runs.Add((1,value)); }
    private static void AddCompositionRun(List<(uint Count,int Offset)> runs,int value)
    { if (runs.Count>0 && runs[^1].Offset==value) runs[^1]=(runs[^1].Count+1,value); else runs.Add((1,value)); }

    private static void MovieHeader(Writer w,long duration,uint nextId) => w.Full("mvhd",1,0,b =>
    {
        b.U64(0);b.U64(0);b.U32(TimeScale);b.U64((ulong)duration);b.U32(0x10000);b.U16(0x100);b.Zero(10);b.Matrix();b.Zero(24);b.U32(nextId);
    });
    private static void TrackBox(Writer w,PreparedTrack t,CancellationToken token) => w.Box("trak",b =>
    {
        b.Full("tkhd",1,7,k =>
        {
            k.U64(0);k.U64(0);k.U32(t.Track.Id);k.U32(0);k.U64((ulong)t.MovieDuration);k.Zero(8);
            k.U16(0);k.U16(t.Video?0:1);k.U16(t.Video?0:0x100);k.U16(0);k.Matrix();k.U32((uint)(t.Video?t.Track.Width:0)<<16);k.U32((uint)(t.Video?t.Track.Height:0)<<16);
        });
        b.Box("edts",e => e.Full("elst",1,0,l =>
        {
            l.U32(t.Lead>0?2u:1u);
            if(t.Lead>0) { l.U64((ulong)t.Lead);l.I64(-1);l.U16(1);l.U16(0); }
            l.U64((ulong)t.PresentationDuration);l.I64(t.MediaStart);l.U16(1);l.U16(0);
        }));
        b.Box("mdia",m =>
        {
            m.Full("mdhd",1,0,h => { h.U64(0);h.U64(0);h.U32(TimeScale);h.U64((ulong)t.DecodeDuration);h.U16(0x55c4);h.U16(0); });
            m.Full("hdlr",0,0,h => { h.U32(0);h.Text(t.Video?"vide":"soun");h.Zero(12);h.Text(t.Video?"Luma Video\0":"Luma Audio\0"); });
            m.Box("minf",n =>
            {
                if(t.Video)n.Full("vmhd",0,1,h=>h.Zero(8));else n.Full("smhd",0,0,h=>h.Zero(4));
                n.Box("dinf",d=>d.Full("dref",0,0,r=>{r.U32(1);r.Full("url ",0,1,_=>{});}));
                n.Box("stbl",s=>SampleTable(s,t,token));
            });
        });
    });
    private static void SampleTable(Writer w,PreparedTrack t,CancellationToken token)
    {
        w.Full("stsd",0,0,b=>{b.U32(1);SampleEntry(b,t);});
        w.Full("stts",0,0,b=>{b.U32((uint)t.DecodeRuns.Count);foreach(var run in t.DecodeRuns){b.U32(run.Count);b.U32(run.Duration);}});
        if(t.CompositionRuns.Any(r=>r.Offset!=0))
            w.Full("ctts",1,0,b=>{b.U32((uint)t.CompositionRuns.Count);foreach(var run in t.CompositionRuns){b.U32(run.Count);b.U32(unchecked((uint)run.Offset));}});
        w.Full("stsc",0,0,b=>{b.U32(1);b.U32(1);b.U32(1);b.U32(1);});
        w.Full("stsz",0,0,b=>{b.U32(0);b.U32((uint)t.Track.Samples.Count);foreach(var sample in t.Track.Samples){token.ThrowIfCancellationRequested();b.U32((uint)sample.Data.Length);}});
        w.Full("co64",0,0,b=>{b.U32((uint)t.Offsets.Length);foreach(ulong offset in t.Offsets){token.ThrowIfCancellationRequested();b.U64(offset);}});
        if(t.Video)w.Full("stss",0,0,b=>
        {
            b.U32((uint)t.Track.Samples.Count(s=>s.Keyframe));
            for(int i=0;i<t.Track.Samples.Count;i++)if(t.Track.Samples[i].Keyframe)b.U32((uint)i+1);
        });
    }
    private static void SampleEntry(Writer w,PreparedTrack t) => w.Box(t.Entry,b=>
    {
        b.Zero(6);b.U16(1);
        if(t.Video)
        {
            b.Zero(16);b.U16(t.Track.Width);b.U16(t.Track.Height);b.U32(0x480000);b.U32(0x480000);b.U32(0);b.U16(1);b.Zero(32);b.U16(0x18);b.U16(0xffff);
            b.Box(t.Track.CodecId=="V_MPEG4/ISO/AVC"?"avcC":"hvcC",c=>c.Bytes(t.Track.CodecPrivate));
        }
        else
        {
            b.Zero(8);b.U16(t.Track.Channels);b.U16(16);b.Zero(4);b.U32((uint)t.Track.SampleRate<<16);
            b.Full("esds",0,0,e=>e.Bytes(Esds(t.Track)));
        }
    });
    private static byte[] Esds(Mp4Track t)
    {
        byte[] dsi=Descriptor(5,t.CodecPrivate);
        byte[] decoder=BoxBytes(w=>{w.U8(0x40);w.U8(0x15);w.Zero(3);w.U32(0);w.U32(0);w.Bytes(dsi);});
        byte[] es=BoxBytes(w=>{w.U16((int)t.Id);w.U8(0);w.Bytes(Descriptor(4,decoder));w.Bytes(Descriptor(6,[2]));});
        return Descriptor(3,es);
    }
    private static byte[] Descriptor(byte tag,byte[] payload)
    {
        var length=new List<byte>();int remaining=payload.Length;length.Add((byte)(remaining&127));
        while((remaining>>=7)!=0)length.Insert(0,(byte)((remaining&127)|128));
        return BoxBytes(w=>{w.U8(tag);foreach(byte part in length)w.U8(part);w.Bytes(payload);});
    }

    private static int ValidateAvc(byte[] config)
    {
        if(config.Length is < 7 or > 1_048_576 || config[0]!=1 || (config[4]&0xfc)!=0xfc || (config[5]&0xe0)!=0xe0)
            throw new InvalidDataException("Invalid avcC record/reserved bits.");
        int length=(config[4]&3)+1;if(length==3)throw new NotSupportedException("Three-byte AVC NAL lengths are not implemented.");
        int cursor=6,sps=config[5]&31;if(sps==0)throw new InvalidDataException("avcC has no SPS.");
        for(int i=0;i<sps;i++)ConfigNal(config,ref cursor,7,false);
        if(cursor>=config.Length)throw new InvalidDataException("Truncated avcC PPS count.");
        int pps=config[cursor++];if(pps==0)throw new InvalidDataException("avcC has no PPS.");
        for(int i=0;i<pps;i++)ConfigNal(config,ref cursor,8,false);
        if(cursor<config.Length)
        {
            if(!new[]{100,110,122,144,44,83,86,118,128,138,139,134,135}.Contains(config[1]) || config.Length-cursor<4 ||
                (config[cursor]&0xfc)!=0xfc || (config[cursor+1]&0xf8)!=0xf8 || (config[cursor+2]&0xf8)!=0xf8)
                throw new NotSupportedException("Unrecognized avcC extension.");
            cursor+=3;int count=config[cursor++];for(int i=0;i<count;i++)ConfigNal(config,ref cursor,13,false);
        }
        if(cursor!=config.Length)throw new InvalidDataException("Trailing avcC bytes.");return length;
    }
    private static (int Length,bool Complete) ValidateHevc(byte[] config)
    {
        if(config.Length is < 23 or > 1_048_576 || config[0]!=1 || (config[13]&0xf0)!=0xf0 || (config[15]&0xfc)!=0xfc ||
            (config[16]&0xfc)!=0xfc || (config[17]&0xf8)!=0xf8 || (config[18]&0xf8)!=0xf8)
            throw new InvalidDataException("Invalid hvcC record/reserved bits.");
        int length=(config[21]&3)+1;if(length==3)throw new NotSupportedException("Three-byte HEVC NAL lengths are not implemented.");
        int cursor=23,total=0;bool complete=true;var required=new HashSet<int>();
        for(int i=0;i<config[22];i++)
        {
            if(config.Length-cursor<3)throw new InvalidDataException("Truncated hvcC array.");byte descriptor=config[cursor++];
            if((descriptor&0x40)!=0)throw new InvalidDataException("Invalid hvcC array reserved bit.");
            int type=descriptor&63,count=BinaryPrimitives.ReadUInt16BigEndian(config.AsSpan(cursor));cursor+=2;
            total+=count;if(total>1024)throw new NotSupportedException("hvcC exceeds1024 parameter NALs.");
            if(type is 32 or 33 or 34){complete&=(descriptor&128)!=0;if(count>0)required.Add(type);}
            for(int n=0;n<count;n++)ConfigNal(config,ref cursor,type,true);
        }
        if(cursor!=config.Length || required.Count!=3)throw new InvalidDataException("hvcC requires complete VPS/SPS/PPS framing.");
        return(length,complete);
    }
    private static void ConfigNal(byte[] config,ref int cursor,int type,bool hevc)
    {
        if(config.Length-cursor<2)throw new InvalidDataException("Truncated codec parameter length.");
        int count=BinaryPrimitives.ReadUInt16BigEndian(config.AsSpan(cursor));cursor+=2;
        if(count<(hevc?2:1) || count>config.Length-cursor || (config[cursor]&128)!=0 ||
            (hevc?((config[cursor]>>1)&63):(config[cursor]&31))!=type || (hevc&&(config[cursor+1]&7)==0))
            throw new InvalidDataException("Invalid codec parameter NAL framing/type.");
        cursor+=count;
    }
    private static bool ValidatePacket(byte[] packet,int prefix,bool hevc,CancellationToken token)
    {
        int cursor=0;bool vcl=false,parameters=false;
        while(cursor<packet.Length)
        {
            token.ThrowIfCancellationRequested();
            if(packet.Length-cursor<prefix)throw new InvalidDataException("Truncated length-prefixed video sample.");
            uint length=0;for(int i=0;i<prefix;i++)length=(length<<8)|packet[cursor++];
            if(length<(hevc?2:1) || length>packet.Length-cursor || (packet[cursor]&128)!=0 || (hevc&&(packet[cursor+1]&7)==0))
                throw new InvalidDataException("Invalid length-prefixed video NAL.");
            int type=hevc?(packet[cursor]>>1)&63:packet[cursor]&31;
            if(!hevc&&type==0)throw new InvalidDataException("Reserved AVC NAL type0.");
            vcl|=hevc?type<=31:type is >=1 and <=5;parameters|=hevc?type is 32 or 33 or 34:type is 7 or 8 or 13;
            cursor+=(int)length;
        }
        if(!vcl)throw new NotSupportedException("A video sample without a coded picture is not implemented.");return parameters;
    }
    private static void ValidateAac(Mp4Track track)
    {
        if(track.CodecPrivate.Length is <2 or >64)throw new InvalidDataException("Invalid AAC AudioSpecificConfig length.");
        var bits=new ConfigBits(track.CodecPrivate);int aot=bits.Read(5);if(aot==31)aot=32+bits.Read(6);
        if(aot!=2)throw new NotSupportedException("MP4 AAC passthrough currently supports AAC-LC only; HE-AAC/SBR/PS are not implemented.");
        int index=bits.Read(4);int[] rates=[96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350];
        int rate=index==15?bits.Read(24):index<rates.Length?rates[index]:throw new InvalidDataException("Reserved AAC frequency index.");
        int channels=bits.Read(4);if(bits.Read(1)!=0)throw new NotSupportedException("AAC960-sample frames are not implemented.");
        if(bits.Read(1)!=0 || bits.Read(1)!=0)throw new NotSupportedException("AAC core-coder/GA extensions are not implemented.");
        while(bits.Remaining>0)if(bits.Read(1)!=0)throw new NotSupportedException("AAC sync SBR/PS or unknown extension is not implemented.");
        if(channels is not(1 or 2 or 6) || rate is <7350 or >64000)throw new NotSupportedException("AAC passthrough requires mono/stereo/5.1 at7350–64000Hz.");
        if(track.Channels!=channels || track.SampleRate!=rate)throw new InvalidDataException("AAC track metadata differs from AudioSpecificConfig.");
    }
    private ref struct ConfigBits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data=data;private int position;
        public int Remaining=>data.Length*8-position;
        public int Read(int count){if(count>Remaining)throw new InvalidDataException("Truncated AudioSpecificConfig.");int value=0;for(int i=0;i<count;i++)value=(value<<1)|((data[position/8]>>(7-position++%8))&1);return value;}
    }
    private static byte[] BoxBytes(Action<Writer> write){var writer=new Writer();write(writer);return writer.ToArray();}
    private sealed class Writer
    {
        private readonly MemoryStream stream=new();
        public byte[] ToArray()=>stream.ToArray();
        public void U8(byte value)=>stream.WriteByte(value);
        public void U16(int value){Span<byte>b=stackalloc byte[2];BinaryPrimitives.WriteUInt16BigEndian(b,checked((ushort)value));stream.Write(b);}
        public void U32(uint value){Span<byte>b=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(b,value);stream.Write(b);}
        public void U64(ulong value){Span<byte>b=stackalloc byte[8];BinaryPrimitives.WriteUInt64BigEndian(b,value);stream.Write(b);}
        public void I64(long value)=>U64(unchecked((ulong)value));
        public void Bytes(byte[] bytes)=>stream.Write(bytes);
        public void Text(string value)=>stream.Write(Encoding.ASCII.GetBytes(value));
        public void Zero(int count){for(int i=0;i<count;i++)stream.WriteByte(0);}
        public void Matrix(){U32(0x10000);U32(0);U32(0);U32(0);U32(0x10000);U32(0);U32(0);U32(0);U32(0x40000000);}
        public void Box(string type,Action<Writer> body)
        {
            long start=stream.Position;U32(0);Text(type);body(this);long end=stream.Position;
            if(end-start>uint.MaxValue)throw new InvalidDataException("MP4 metadata box exceeds32-bit size.");
            stream.Position=start;U32((uint)(end-start));stream.Position=end;
        }
        public void Full(string type,byte version,int flags,Action<Writer> body)=>Box(type,b=>{b.U32((uint)version<<24|(uint)flags);body(b);});
    }
}
