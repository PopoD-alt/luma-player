using System.Text.Json;
using Luma.Core.Containers;
using Luma.Core.Library;
using Luma.Core.Analysis;

namespace Luma.Core.Tests;

public static class PlaybackSkipTests
{
    static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    static void WriteNativeAudioFixture(string path)
    {
        const int rate=8000,frames=rate*8; using var file=File.Create(path); using var writer=new BinaryWriter(file);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+frames*2);writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(rate);writer.Write(rate*2);writer.Write((short)2);writer.Write((short)16);writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));writer.Write(frames*2);
        for(int i=0;i<frames;i++)
        {
            double t=i/(double)rate;int beat=(int)(t*8);double frequency=130+(beat*137)%2500, second=210+(beat*241)%3000;
            double envelope=.2+.03*(beat%5)+.04*Math.Sin(t*3.7);
            double signal=envelope*(Math.Sin(2*Math.PI*frequency*t)+(.15+.06*(beat%9))*Math.Sin(2*Math.PI*second*t));
            writer.Write((short)Math.Clamp((int)(signal*23000),short.MinValue,short.MaxValue));
        }
    }
    public static void Run(string? fixtures = null)
    {
        foreach (string label in new[] { "Intro", "Opening", "Opening Credits", "OP", "OP 2", "opening-theme", "オープニング" })
            Check(PlaybackSkipPolicy.KindForChapter(label) == "intro", "Explicit opening label: " + label);
        foreach (string label in new[] { "Credits", "Ending", "End Credits", "Closing credits", "ED", "OutRo", "エンディング" })
            Check(PlaybackSkipPolicy.KindForChapter(label) == "credits", "Explicit credit label: " + label);
        foreach (string label in new[] { "Chapter 1", "1", "Prologue", "Epilogue", "Cold open", "Post-credits", "Opening scene", "The credits are missing", "Story", "Recap", new string('x', 161) })
            Check(PlaybackSkipPolicy.KindForChapter(label) is null, "Uncertain story/post-credit label must stay playing: " + label);
        var ranges = PlaybackSkipPolicy.FromChapters([new(0,1,"Cold open"),new(1,2,"Opening"),new(3,5,"Story"),new(8,2,"Credits"),new(10,2,"Post-credits")],12);
        Check(ranges.Count == 2 && ranges[0] is { Kind:"intro", StartSeconds:1, EndSeconds:3 } && ranges[1] is { Kind:"credits", StartSeconds:8, EndSeconds:10 }, "Chapter-derived exact boundaries preserve cold open/story/post-credits");
        Check(PlaybackSkipPolicy.Active(ranges, 1)?.Kind == "intro" && PlaybackSkipPolicy.Active(ranges, 3) is null && PlaybackSkipPolicy.Active(ranges, 10) is null, "Segment boundaries are half open");
        var manual = new PlaybackSkipRange("intro", .5, 1.5, "manual", "Saved intro");
        var merged = PlaybackSkipPolicy.Merge([manual],ranges,12);
        Check(merged.Count == 2 && merged[0] == manual && merged[1].Kind == "credits", "Saved range overrides only its own labelled kind");
        foreach (var bad in new[] { manual with { StartSeconds=double.NaN },manual with { EndSeconds=double.PositiveInfinity },manual with { StartSeconds=-1 },manual with { StartSeconds=2, EndSeconds=1 },manual with { EndSeconds=13 },manual with { Kind="scene" },manual with { Source="unknown" },manual with { EndSeconds=31_536_001 } })
            Check(PlaybackSkipPolicy.ValidatedRanges([bad],12).Count == 0, "Unsafe segment rejected");
        Check(PlaybackSkipPolicy.ValidatedRanges([manual, new("credits",1,3,"manual")],12).Count==0,"Overlapping ranges are rejected rather than skipping ambiguous story");
        Check(PlaybackSkipPolicy.ValidatedRanges([manual,manual],12).Count==1,"Identical segments deduplicate");
        var session=new PlaybackSkipSession();
        Check(session.Automatic(ranges,1.5,false,true,true) is null && session.Automatic(ranges,1.5,true,false,true) is null && session.Automatic(ranges,1.5,true,true,false) is null,"Off/paused/unseekable player never automatically seeks");
        Check(session.Automatic(ranges,1.5,true,true,true)?.EndSeconds==3 && session.Automatic(ranges,1.6,true,true,true) is null && session.Automatic(ranges,1.2,true,true,true) is null,"Asynchronous seek and later rewind cannot repeat automatic skip");
        Check(session.Automatic(ranges,8.2,true,true,true)?.EndSeconds==10 && session.AutomaticSkips==2,"Ending skip stops before post-credit chapter");
        session.Reset();session.Consume(ranges[0]);Check(session.Automatic(ranges,1.2,true,true,true) is null && PlaybackSkipPolicy.Active(ranges,1.2) is not null,"Explicit user seek keeps manual skip available while suppressing auto-repeat");
        session.Reset();session.UserSeek(1.4);Check(session.Automatic(ranges,1.5,true,true,true) is null,"Explicit seek before chapter discovery suppresses a late automatic seek");
        session.Reset();Check(session.Automatic(ranges,1.5,true,true,true) is not null,"New playthrough resets automatic history");
        string temp=Path.Combine(Path.GetTempPath(),"luma-skip-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            string media=Path.Combine(temp,"fixture.mp4"),path=Path.Combine(temp,"library.json");File.WriteAllText(media,"authored fixture");
            File.WriteAllText(path,JsonSerializer.Serialize(new { Items=new[]{new { Id=Guid.NewGuid(), FilePath=media, Duration=12, ResumeTime=4 }} }));
            var store=new LibraryStore(path);var entry=store.Data.Items.Single();Check(!store.Data.AutoSkipEnabled && entry.SkipRanges.Count==0,"Older libraries opt out and have no ranges");
            store.Data.AutoSkipEnabled=true;Check(store.SetSkipRanges(entry.Id,[manual]),"Manual range mutation");var loaded=new LibraryStore(path);
            Check(loaded.Data.AutoSkipEnabled && loaded.Data.Items.Single().SkipRanges.SequenceEqual(new[]{manual}) && loaded.Data.Items.Single().ResumeTime==4,"Skip preference/ranges persist independently from playback progress");
            string sound=Path.Combine(temp,"audio.wav");WriteNativeAudioFixture(sound);byte[] encoded=File.ReadAllBytes(sound);
            var fingerprint=new RepeatedAudioFingerprint(4);var identical=new RepeatedAudioFingerprint(4);
            short[] pcm=Enumerable.Range(0,32000).Select(i=>System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(44+i*2,2))).ToArray();fingerprint.Append(pcm);identical.Append(pcm);
            Check(RepeatedAudioMatcher.Match(fingerprint,identical,"intro",0,0,3).Any(r=>r.FirstEndSeconds-r.FirstStartSeconds>=3),"Native audio fixture has enough varied spectral content for conservative matching");
            string wire=JsonSerializer.Serialize(manual);Check(wire.Contains("\"startSeconds\"") && wire.Contains("\"kind\""),"Additive wire keys match desktop/Android contract");
        }
        finally{Directory.Delete(temp,true);}
        if(fixtures is not null)
        {
            var old=MatroskaDemuxer.Demux(File.ReadAllBytes(Path.Combine(fixtures,"Luma-AVC-AAC.mkv")));
            var chaptered=MatroskaDemuxer.Demux(File.ReadAllBytes(Path.Combine(fixtures,"Luma-Skip-Chapters.mkv")));
            Check(old.Tracks.Count==chaptered.Tracks.Count,"Chapter fixture tracks retained");
            foreach(var track in old.Tracks)
            {
                var other=chaptered.Tracks.Single(t=>t.Id==track.Id);
                Check(track.Packets.Count==other.Packets.Count && track.CodecPrivate.SequenceEqual(other.CodecPrivate),"Original encoded packets/config retained");
                Check(track.Packets.Zip(other.Packets).All(pair=>pair.First.Data.SequenceEqual(pair.Second.Data) && pair.First.TimestampNanoseconds==pair.Second.TimestampNanoseconds),"All chapter fixture packet bytes/timestamps unchanged");
            }
        }
        if (Environment.GetEnvironmentVariable("LUMA_WINDOWS_MEDIA_EXPORT") is { Length: >0 } export)
        { Directory.CreateDirectory(export); WriteNativeAudioFixture(Path.Combine(export,"Luma-review-skip-audio.wav")); }
        Console.WriteLine("PASS: bounded labelled/manual intro-credit ranges, story/post-credit preservation, explicit-seek override, once-per-playthrough skip, old-library opt-out and saved-range persistence");
    }
}
