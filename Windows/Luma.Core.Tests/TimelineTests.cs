using Luma.Core.Containers;
using Luma.Core.Subtitles;
using System.Text;

namespace Luma.Core.Tests;

internal static class TimelineTests
{
    public static void Run(string fixtures)
    {
        const long frame = 40_000_000;
        DemuxedTrack Video(long[] times) => new(1,"V_MPEG4/ISO/AVC",[],64,48,0,0,frame,times.Select((t,i)=>new MediaPacket([(byte)i],t,null,i==0)).ToList()) { TrackType=1 };
        var reordered = Video([0,3*frame,frame,2*frame,6*frame,4*frame,5*frame]);
        var samples = MatroskaTimeline.Video(reordered,1_000_000);
        Check(samples.Select(s=>s.Data[0]).SequenceEqual(new byte[]{0,1,2,3,4,5,6}),"Packet coding order changed");
        Check(samples.Select(s=>s.PtsNanoseconds).SequenceEqual(reordered.Packets.Select(p=>p.TimestampNanoseconds)),"PTS changed");
        Check(samples.All(s=>s.DtsNanoseconds<=s.PtsNanoseconds)&&samples.Zip(samples.Skip(1)).All(p=>p.Second.DtsNanoseconds-p.First.DtsNanoseconds==frame),"CFR DTS reconstruction");
        var coding = new List<int>{0}; for(int first=1;first<300;first+=3) { if(first+2<300)coding.AddRange([first+2,first,first+1]);else for(int i=first;i<300;i++)coding.Add(i); }
        var fractional=Video(coding.Select(i=>(long)((Int128)i*1_000_000_000/30)).ToArray()) with { DefaultDurationNanoseconds=33_333_333 };
        var rational=MatroskaTimeline.Video(fractional,1);
        Check(rational.Count==300&&rational.Select(s=>s.PtsNanoseconds).SequenceEqual(fractional.Packets.Select(p=>p.TimestampNanoseconds)),"Fractional CFR PTS drift");
        Check(rational.Sum(s=>s.DurationNanoseconds) is >=9_999_999_999 and <=10_000_000_001,"Fractional CFR duration drift");
        Reject(()=>MatroskaTimeline.Video(Video([0,frame,frame]),1_000_000));
        Reject(()=>MatroskaTimeline.Video(Video([frame,0,2*frame]),1_000_000));
        Reject(()=>MatroskaTimeline.Video(Video([0,3*frame,frame,2*frame+2_000_000]),1_000_000));
        Reject(()=>MatroskaTimeline.Video(Video([0]) with { DefaultDurationNanoseconds=0 },1_000_000));
        var linear=MatroskaTimeline.Video(Video([0,frame,2*frame]),1_000_000);
        Check(linear.Select(s=>s.DurationNanoseconds).All(d=>d==frame),"Linear timing");
        var audio = new DemuxedTrack(2,"A_AAC",[0x12,0x10],0,0,44100,2,0,Enumerable.Range(0,100).Select(i=>new MediaPacket([1],(long)i*1024*1000/44100*1_000_000,null,true)).ToList()) { TrackType=2 };
        var aac=MatroskaTimeline.Aac(audio,1_000_000);
        Check(aac.Sum(s=>s.DurationNanoseconds)==(long)((Int128)100*1024*1_000_000_000/44100),"AAC rational clock drift");
        audio.Packets[50]=audio.Packets[50] with { TimestampNanoseconds=audio.Packets[50].TimestampNanoseconds+4_000_000 };
        Reject(()=>MatroskaTimeline.Aac(audio,1_000_000));
        AacBoundaryTests();
        EmbeddedTextTests();
        try { MatroskaTimeline.Video(reordered,1_000_000,new CancellationToken(true)); throw new Exception("Cancellation ignored"); } catch(OperationCanceledException) { }
        Console.WriteLine("PASS: original Matroska coding-order preservation, bounded CFR B-frame DTS/PTS, AAC rational sample grid, malformed/gapped/VFR timeline rejection and cancellation");
        Console.WriteLine("PASS: AAC coarse/half-frame clock and gap/overlap/nonpositive-duration rejection; embedded UTF8/ASS/SSA/WebVTT literal text, drawing suppression, escapes, UTF8/resource/timing limits and cancellation");
    }

    static void AacBoundaryTests()
    {
        DemuxedTrack Audio(double rate, params MediaPacket[] packets) => new(2,"A_AAC",[0x12,0x10],0,0,rate,2,0,packets.ToList()) { TrackType=2 };
        // A one-second gap must not be silently squeezed onto a 21ms sample grid.
        Reject(()=>MatroskaTimeline.Aac(Audio(48000,new([1],0,null,true),new([2],1_000_000_000,null,true)),1_000_000_000));
        var exact=Audio(32000,new([1],0,null,true),new([2],32_000_000,null,true));
        Reject(()=>MatroskaTimeline.Aac(exact,16_000_000)); // Exactly half a frame is ambiguous.
        Check(MatroskaTimeline.Aac(exact,15_999_999)[1].PtsNanoseconds==32_000_000,"Fine-enough AAC timestamp precision boundary");
        foreach(long invalidDuration in new[]{0L,-1L,long.MinValue})
            Reject(()=>MatroskaTimeline.Aac(Audio(48000,new MediaPacket([1],0,invalidDuration,true)),1_000_000));
        Reject(()=>MatroskaTimeline.Aac(Audio(48000,new([1],0,null,true),new([2],0,null,true)),1_000_000));
        Reject(()=>MatroskaTimeline.Aac(Audio(48000,new([1],0,null,true),new([2],23_500_000,null,true)),1_000_000));
        Reject(()=>MatroskaTimeline.Aac(Audio(48000,new([1],long.MaxValue-1,null,true),new([2],long.MaxValue,null,true)),1_000_000));
        foreach(double rate in new[]{double.NaN,double.PositiveInfinity,0,48000.5,65536})
            Reject(()=>MatroskaTimeline.Aac(Audio(rate,new MediaPacket([1],0,null,true)),1_000_000));
        try { MatroskaTimeline.Aac(exact,1_000_000,new CancellationToken(true)); throw new Exception("AAC cancellation ignored"); } catch(OperationCanceledException) { }
    }

    static void EmbeddedTextTests()
    {
        DemuxedTrack Track(string codec, params MediaPacket[] packets) => new(3,codec,[],0,0,0,0,0,packets.ToList()) { TrackType=17 };
        MediaPacket Packet(string text,long timestamp=0,long? duration=1_000_000_000) => new(Encoding.UTF8.GetBytes(text),timestamp,duration,true);
        string Text(string codec,string text) => EmbeddedTextDecoder.Decode(Track(codec,Packet(text)),0).Single().Text;
        string Ass(string value) => "0,0,Default,,0,0,0,,"+value;
        foreach(string literal in new[]{"<Naruto>","2 < 3 and 4 > 1","2 <3 and4>1","<b7>safe</b7>","Unclosed <Naruto"})
            Check(Text("S_TEXT/UTF8",literal)==literal,"Embedded literal angle text changed: "+literal);
        Check(Text("S_TEXT/UTF8","<b>Hello</b><br/>line &amp; &lt;tag&gt;")=="Hello\nline & <tag>","Known formatting tags/entities");
        Check(Text("S_TEXT/UTF8","<p>A</p><div>B</div>")=="A\nB","Paragraph break plain text");
        Check(Text("S_TEXT/WEBVTT","<v Naruto><c.red>Hello</c></v><00:00:01.250> &lt;literal&gt;")=="Hello <literal>","WebVTT voice/class/timestamp tags");
        Check(Text("S_TEXT/UTF8","<00:00:01.250>")=="<00:00:01.250>","UTF8 cue must preserve non-WebVTT timestamp literals");
        foreach(string codec in new[]{"S_TEXT/ASS","S_TEXT/SSA"})
        {
            Check(Text(codec,Ass(@"{\p1}m 0 0 l 10 10{\p0}Hello\Nworld\h!"))=="Hello\nworld\u00a0!","ASS vector drawing suppression/newlines");
            Check(Text(codec,Ass(@"\{literal\}\\\Nend"))=="{literal}\\\nend","ASS escaped braces/backslash/newline");
            Check(Text(codec,Ass(@"{\pos(10,10)}Visible"))=="Visible","ASS position tag confused with drawing mode");
            Check(Text(codec,Ass(@"{\fnFont\}Name}Text"))=="Text","ASS escaped closing brace prematurely ended override");
            Reject(()=>Text(codec,"0,too,few,fields"));
            Reject(()=>Text(codec,"-1,0,Default,,0,0,0,,Text"));
            Reject(()=>Text(codec,Ass(@"{\b1Unterminated")));
        }
        Check(Text("S_TEXT/UTF8","\uFEFFLine\r\n2\0\t3")=="Line\n2\t3","Embedded newline/BOM/control cleanup");
        var clipped=EmbeddedTextDecoder.Decode(Track("S_TEXT/UTF8",Packet("Before",-500_000_000)),0).Single();
        Check(clipped.Start==0&&clipped.End==.5,"Embedded cue origin clipping");
        var precise=EmbeddedTextDecoder.Decode(Track("S_TEXT/UTF8",Packet("Precise",long.MaxValue-100)),long.MaxValue-200).Single();
        Check(Math.Abs(precise.Start-0.0000001)<1e-14,"Embedded origin subtraction lost sub-microsecond precision");
        Check(EmbeddedTextDecoder.Decode(Track("S_TEXT/UTF8",Packet("Zero",duration:0),Packet("Unknown",duration:null)),0).Count==0,"Absent/zero cue duration invented an end");
        Reject(()=>EmbeddedTextDecoder.Decode(Track("S_TEXT/UTF8",Packet("Negative",duration:-1)),0));
        Reject(()=>EmbeddedTextDecoder.Decode(Track("S_HDMV/PGS",Packet("bitmap")),0));
        Reject(()=>EmbeddedTextDecoder.Decode(Track("S_TEXT/UTF8",new MediaPacket([0xff],0,1_000_000_000,true)),0));
        Reject(()=>EmbeddedTextDecoder.Decode(Track("S_TEXT/UTF8",new MediaPacket(new byte[1_048_577],0,1_000_000_000,true)),0));
        Reject(()=>Text("S_TEXT/UTF8",new string('x',16385)));
        var many=Track("S_TEXT/UTF8",Enumerable.Repeat(Packet(new string('x',16000)),251).ToArray());
        Reject(()=>EmbeddedTextDecoder.Decode(many,0));
        Reject(()=>EmbeddedTextDecoder.Decode(Track("S_TEXT/UTF8",Enumerable.Repeat(Packet("cue"),100_001).ToArray()),0));
        try { EmbeddedTextDecoder.Decode(Track("S_TEXT/UTF8",Packet("cancel")),0,new CancellationToken(true)); throw new Exception("Subtitle cancellation ignored"); } catch(OperationCanceledException) { }
    }
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch(Exception e) when(e is InvalidDataException or NotSupportedException) { return; } throw new Exception("Invalid timeline accepted"); }
}
