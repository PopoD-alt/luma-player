using System.Buffers.Binary;
using System.Text;
using Luma.Core.Containers;

namespace Luma.Core.Tests;
public static class MatroskaTests
{
    public static void Run(string fixturesRoot)
    {
        byte[] fixture=File.ReadAllBytes(Path.Combine(fixturesRoot,"Luma-AVC-AAC.mkv"));
        var media=MatroskaDemuxer.Demux(fixture);
        Check(media.TimestampScaleNanoseconds==1_000_000 && media.Tracks.Count==2,"Original fixture tracks/timestamp scale");
        var video=media.Tracks.Single(t=>t.CodecId=="V_MPEG4/ISO/AVC"); var audio=media.Tracks.Single(t=>t.CodecId=="A_AAC");
        Check(video.TrackType==1 && video.Width==960 && video.Height==540 && video.Packets.Count==360 && video.Packets[0].Keyframe,"Original AVC fixture dimensions/count/keyframe");
        Check(audio.TrackType==2 && audio.SampleRate==48000 && audio.Channels is 1 or 2 && audio.Packets.Count>100,"Original AAC fixture metadata/count");
        Check(video.Packets[0].TimestampNanoseconds==250_000_000 && audio.Packets[0].TimestampNanoseconds==500_000_000,"Original A/V offset must be retained without normalization");
        Check(video.DefaultDurationNanoseconds==33_333_333 && video.Packets[0].DurationNanoseconds==33_000_000,"DefaultDuration and rounded BlockDuration remain distinct");
        Check(audio.Packets.All(p=>p.DurationNanoseconds is >21_000_000 and <22_000_000) && video.CodecPrivate[0]==1 && audio.CodecPrivate.Length>=2,"CodecPrivate/AAC timing");
        byte[] originalPacket=(byte[])video.Packets[0].Data.Clone(); Array.Fill(fixture,(byte)0);
        Check(video.Packets[0].Data.SequenceEqual(originalPacket),"Returned packets must own their bytes");
        var bitmap=MatroskaDemuxer.Demux(File.ReadAllBytes(Path.Combine(fixturesRoot,"Luma-PGS.mkv")));
        var pgs=bitmap.Tracks.Single(t=>t.CodecId=="S_HDMV/PGS");
        Check(bitmap.Tracks.Count==3 && pgs.TrackType==17 && pgs.Id==4 && !pgs.IsDefault && pgs.Packets.Count==10,"Original PGS fixture packet carriage/default metadata");
        Check(pgs.Packets[0].Data[0]==0x16 && pgs.Packets[0].TimestampNanoseconds==1_250_000_000 && pgs.Packets[^1].TimestampNanoseconds==4_250_000_000,"PGS segment data/timeline");
        Console.WriteLine($"PASS: original AVC/AAC Matroska fixture {video.Packets.Count} video/{audio.Packets.Count} audio packets, exact A/V offset/default duration/configuration, owned bytes; PGS raw track metadata and10timed segments");

        byte[][] unequal=[[1,2],[3,4,5],[6,7,8,9]];
        foreach(int mode in Enumerable.Range(0,4))
        {
            byte[][] packets=mode==2?new byte[][]{[1,2],[3,4],[5,6]}:unequal;
            var blocks=mode==0?packets.Select((p,i)=>Simple(1,-20+i*10,[p])).ToArray():[Simple(1,-20,packets,mode)];
            var result=MatroskaDemuxer.Demux(MakeFile([Video()],blocks,cluster:100)).Tracks.Single();
            Check(result.Packets.Count==3,"Lace packet count");
            for(int index=0;index<3;index++)
            {
                Check(result.Packets[index].Data.SequenceEqual(packets[index]),"Exact lace boundaries");
                Check(result.Packets[index].TimestampNanoseconds==(80+index*10)*1_000_000L && result.Packets[index].DurationNanoseconds==10_000_000,"Signed relative/lace DefaultDuration");
            }
        }
        byte[][] longXiph=[new byte[300],new byte[255],[7]];
        var xiph=MatroskaDemuxer.Demux(MakeFile([Video()],[Simple(1,0,longXiph,1)])).Tracks.Single();
        Check(xiph.Packets.Select(p=>p.Data.Length).SequenceEqual([300,255,1]),"Xiph255 continuation and exact multiple terminator");
        foreach(byte[][] packets in new[]{new byte[][]{new byte[127],[1],[2]},new byte[][]{[1],new byte[65],[2]}})
        {
            var result=MatroskaDemuxer.Demux(MakeFile([Video(id:127)],[Simple(127,0,packets,3)])).Tracks.Single();
            Check(result.Id==127 && result.Packets.Select(p=>p.Data.Length).SequenceEqual(packets.Select(p=>p.Length)),"All-ones raw VINT TrackNumber/first lace/signed+64 delta are values, not unknown sizes");
        }
        var scaled=MatroskaDemuxer.Demux(MakeFile([Video(scale:1.5)],[Group(SimplePayload(1,-20,unequal,3),duration:30)],cluster:100)).Tracks.Single();
        Check(scaled.Packets.Select(p=>p.TimestampNanoseconds).SequenceEqual([70_000_000L,85_000_000L,100_000_000L]) && scaled.Packets.All(p=>p.DurationNanoseconds==15_000_000),"TrackTimestampScale applies only relative timestamp and TrackTick duration");
        var keyframes=MatroskaDemuxer.Demux(MakeFile([Video()],[Group(SimplePayload(1,0,[[1]]),duration:10),Group(SimplePayload(1,10,[[2]]),reference:true),Simple(1,20,[[3]],keyframe:false)])).Tracks.Single();
        Check(keyframes.Packets.Select(p=>p.Keyframe).SequenceEqual([true,false,false]),"BlockGroup ReferenceBlock versus SimpleBlock keyframe semantics");
        var reordered=MatroskaDemuxer.Demux(MakeFile([Video()],[Simple(1,0,[[1]]),Simple(1,20,[[2]],keyframe:false),Simple(1,10,[[3]],keyframe:false)])).Tracks.Single();
        Check(reordered.Packets.Select(p=>p.Data[0]).SequenceEqual(new byte[]{1,2,3}) && reordered.Packets.Select(p=>p.TimestampNanoseconds).SequenceEqual([0L,20_000_000L,10_000_000L]),"PTS reordering must not sort coding order");
        var unknownReordered=MatroskaDemuxer.Demux(MakeFile([Video(defaultDuration:0)],[Simple(1,0,[[1]]),Simple(1,20,[[2]]),Simple(1,10,[[3]])])).Tracks.Single();
        Check(unknownReordered.Packets.All(p=>p.DurationNanoseconds is null),"Do not invent adjacent-PTS durations for reordered video without DefaultDuration");
        var negative=MatroskaDemuxer.Demux(MakeFile([Video()],[Simple(1,-100,[[1]])],cluster:0)).Tracks.Single();
        Check(negative.Packets[0].TimestampNanoseconds==-100_000_000,"Negative absolute PTS must not be silently clamped");
        var rational=MatroskaDemuxer.Demux(MakeFile([Audio(rate:44100,asc:[0x12,0x10])],[Simple(2,0,[[1],[2],[3]],1)])).Tracks.Single();
        Check(rational.Packets[2].TimestampNanoseconds==46_439_909 && rational.Packets.Sum(p=>p.DurationNanoseconds!.Value)==69_659_864,"AAC lace timestamp rational rounding");
        var hevc=MatroskaDemuxer.Demux(MakeFile([Video(codec:"V_MPEGH/ISO/HEVC",configuration:Join([1],new byte[22]))],[Simple(1,0,[[1]])])).Tracks.Single();
        Check(hevc.CodecId=="V_MPEGH/ISO/HEVC" && hevc.CodecPrivate.Length==23,"HEVC raw configuration carriage");
        var text=MatroskaDemuxer.Demux(MakeFile([TextTrack()],[Group(SimplePayload(3,100,[Encoding.UTF8.GetBytes("Original caption")]),duration:25)])).Tracks.Single();
        Check(text.Language=="en" && text.Name=="Own text" && text.IsForced && !text.IsDefault && Encoding.UTF8.GetString(text.Packets[0].Data)=="Original caption" && text.Packets[0].DurationNanoseconds==25_000_000,"Text subtitle packet metadata/duration carriage");
        Console.WriteLine("PASS: SimpleBlock/BlockGroup, all4lacing modes, Xiph255 runs, full raw-VINT values, signed relative/absolute PTS, track scaling, reference/keyframe flags, reordered coding order, rationalAAC andHEVC/text metadata");

        byte[] disabled=Track(7,2,"A_NOT_SUPPORTED",Join(E(0x6d80,[]),U(0x56aa,1),U(0xb9,0)));
        var enabled=MatroskaDemuxer.Demux(MakeFile([Video(),disabled],[Simple(1,0,[[1]]),E(0xa3,RawVint(7))]));
        Check(enabled.Tracks.Count==1 && enabled.Tracks[0].Id==1,"Late disabled unknown track must not reject/play packets");
        byte[] normal=MakeFile([Video()],[Simple(1,0,[[1,2]])],unknownSegment:false);
        for(int length=0;length<normal.Length;length++) Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(normal[..length]));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux([0,0,0,0]));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(Join(Header(),[0x18,0x53,0x80,0x67,0x88,0])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(),Video()],[Simple(1,0,[[1]])])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(width:0)],[Simple(1,0,[[1]])])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(width:8193)],[Simple(1,0,[[1]])])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(configuration:[1,2])],[Simple(1,0,[[1]])])));
        Reject<NotSupportedException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(extra:E(0x6d80,[]))],[Simple(1,0,[[1]])])));
        Reject<NotSupportedException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(videoExtra:E(0x55b0,[]))],[Simple(1,0,[[1]])])));
        Reject<NotSupportedException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(codec:"V_VP9")],[Simple(1,0,[[1]])])));
        Reject<NotSupportedException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(extra:U(0x56aa,1))],[Simple(1,0,[[1]])])));
        Reject<NotSupportedException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(defaultDuration:0)],[Simple(1,0,[[1],[2]],1)])));
        Reject<NotSupportedException>(()=>MatroskaDemuxer.Demux(MakeFile([Video()],[Group(SimplePayload(1,0,[[1]]),extra:E(0xa4,[]))])));
        Reject<NotSupportedException>(()=>MatroskaDemuxer.Demux(MakeFile([Video()],[Simple(1,0,[[1]],flags:8)])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video()],[Simple(2,0,[[1]])])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video(extra:U(0x9c,0))],[Simple(1,0,[[1],[2]],1)])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video()],[E(0xa3,Join(RawVint(1),[0,0,4,2,1,2,3,4]))])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video()],[E(0xa3,Join(RawVint(1),[0,0,2,0,1]))])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video()],[E(0xa3,Join(RawVint(1),[0,0,6,2,0xff,0]))])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Video()],[Simple(1,0,[[1]])],cluster:ulong.MaxValue)));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Audio(asc:[0])],[Simple(2,0,[[1]])])));
        Reject<InvalidDataException>(()=>MatroskaDemuxer.Demux(MakeFile([Audio(rate:44100)],[Simple(2,0,[[1]])])));
        Reject<NotSupportedException>(()=>MatroskaDemuxer.Demux(MakeFile([Video()],[Simple(1,0,[[1]])],clusterUnknown:true)));
        Reject<NotSupportedException>(()=>MatroskaDemuxer.Demux(Join(Header(),E(0x18538067,[]),E(0x18538067,[]))));
        Reject<OperationCanceledException>(()=>MatroskaDemuxer.Demux(normal,new CancellationToken(true)));
        Console.WriteLine("PASS: disabled unknown tracks, every strict prefix of a complete bounded fixture, malformed EBML/lacing/configuration/geometry/timestamp, unsupported encoding/delay/codec/unknownCluster and cancellation");
    }
    static void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);}
    static void Reject<T>(Action action) where T:Exception{try{action();}catch(T){return;}throw new InvalidOperationException("Expected "+typeof(T).Name+" from malformed Matroska.");}
    static byte[] Join(params byte[][] arrays)=>arrays.SelectMany(a=>a).ToArray();
    static byte[] Integer(ulong value)
    {
        var bytes=new List<byte>();bool started=false;for(int shift=56;shift>=0;shift-=8){byte next=(byte)(value>>shift);if(next!=0||started||shift==0){bytes.Add(next);started=true;}}return bytes.ToArray();
    }
    static byte[] RawVint(ulong value)
    {
        for(int width=1;width<=8;width++)if(value<=(1UL<<(width*7))-1){ulong encoded=value|(1UL<<(width*7));return Enumerable.Range(0,width).Reverse().Select(i=>(byte)(encoded>>(8*i))).ToArray();}throw new InvalidOperationException();
    }
    static byte[] Size(int value)
    {
        for(int width=1;width<=8;width++)if((ulong)value<(1UL<<(width*7))-1){ulong encoded=(ulong)(uint)value|(1UL<<(width*7));return Enumerable.Range(0,width).Reverse().Select(i=>(byte)(encoded>>(8*i))).ToArray();}throw new InvalidOperationException();
    }
    static byte[] E(ulong id,byte[] body)=>Join(Integer(id),Size(body.Length),body);
    static byte[] U(ulong id,ulong value)=>E(id,Integer(value));
    static byte[] Text(ulong id,string value)=>E(id,Encoding.UTF8.GetBytes(value));
    static byte[] Float(ulong id,double value){var data=new byte[8];BinaryPrimitives.WriteInt64BigEndian(data,BitConverter.DoubleToInt64Bits(value));return E(id,data);}
    static byte[] Header()=>E(0x1a45dfa3,Join(U(0x42f7,1),Text(0x4282,"matroska"),U(0x4285,4)));
    static byte[] Track(ulong id,int type,string codec,byte[] extra)=>E(0xae,Join(U(0xd7,id),U(0x83,(ulong)type),Text(0x86,codec),extra));
    static byte[] Video(ulong id=1,string codec="V_MPEG4/ISO/AVC",ulong width=8,long defaultDuration=10_000_000,double scale=1,byte[]? configuration=null,byte[]? extra=null,byte[]? videoExtra=null)
        =>Track(id,1,codec,Join(E(0x63a2,configuration??[1,100,0,31,255,225,0]),E(0xe0,Join(U(0xb0,width),U(0xba,4),videoExtra??[])),defaultDuration>0?U(0x23e383,(ulong)defaultDuration):[],Float(0x23314f,scale),extra??[]));
    static byte[] Audio(double rate=48000,byte[]? asc=null)=>Track(2,2,"A_AAC",Join(E(0x63a2,asc??[0x11,0x90]),E(0xe1,Join(Float(0xb5,rate),U(0x9f,2)))));
    static byte[] TextTrack()=>Track(3,17,"S_TEXT/UTF8",Join(Text(0x536e,"Own text"),Text(0x22b59d,"en"),U(0x88,0),U(0x55aa,1)));
    static byte[] MakeFile(byte[][] tracks,byte[][] blocks,ulong cluster=0,bool unknownSegment=true,bool clusterUnknown=false)
    {
        byte[] clusterData=Join(U(0xe7,cluster),Join(blocks));
        byte[] body=Join(E(0x1549a966,U(0x2ad7b1,1_000_000)),E(0x1654ae6b,Join(tracks)),clusterUnknown?Join(Integer(0x1f43b675),[255],clusterData):E(0x1f43b675,clusterData));
        return Join(Header(),unknownSegment?Join(Integer(0x18538067),[1,255,255,255,255,255,255,255],body):E(0x18538067,body));
    }
    static byte[] Simple(ulong track,int time,byte[][] frames,int mode=0,bool keyframe=true,byte flags=0)=>E(0xa3,SimplePayload(track,time,frames,mode,(byte)((keyframe?128:0)|flags)));
    static byte[] SimplePayload(ulong track,int time,byte[][] frames,int mode=0,byte flags=0)
    {
        ushort relative=unchecked((ushort)(short)time);var result=new List<byte>(Join(RawVint(track),[(byte)(relative>>8),(byte)relative,(byte)(flags|(mode<<1))]));
        if(mode!=0)
        {
            result.Add((byte)(frames.Length-1));
            if(mode==1)foreach(var frame in frames[..^1]){int size=frame.Length;while(size>=255){result.Add(255);size-=255;}result.Add((byte)size);}
            if(mode==3)
            {
                result.AddRange(RawVint((ulong)frames[0].Length));
                for(int index=1;index<frames.Length-1;index++)
                {
                    long delta=frames[index].Length-frames[index-1].Length;
                    for(int width=1;width<=8;width++)
                    {
                        long bias=(1L<<(7*width-1))-1;if(delta< -bias || delta>bias+1)continue;
                        ulong encoded=(ulong)(delta+bias)|(1UL<<(7*width));for(int octet=width-1;octet>=0;octet--)result.Add((byte)(encoded>>(octet*8)));break;
                    }
                }
            }
        }
        foreach(var frame in frames)result.AddRange(frame);return result.ToArray();
    }
    static byte[] Group(byte[] payload,ulong? duration=null,bool reference=false,byte[]? extra=null)=>E(0xa0,Join(E(0xa1,payload),duration is {} value?U(0x9b,value):[],reference?E(0xfb,[0]):[],extra??[]));
}
