using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luma.Core.Library;
using Luma.Core.Subtitles;
using Luma.Core.Updates;

namespace Luma.Core.Tests;
public static class FoundationTests
{
    public static void Run(string fixturesRoot)
    {
        var temp = Path.Combine(Path.GetTempPath(), "Luma-core-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try { Library(temp); Subtitles(); Updates(temp,fixturesRoot); }
        finally { Directory.Delete(temp,true); }
    }
    static void Check(bool value,string name) { if(!value) throw new InvalidOperationException(name); }
    static void Reject<T>(Action action) where T:Exception
    {
        try { action(); } catch(T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
    }
    static void Library(string temp)
    {
        var path=Path.Combine(temp,"library.json"); var store=new LibraryStore(path);
        var first=Path.Combine(temp,"first.wav"); var second=Path.Combine(temp,"second.avi");
        File.WriteAllText(first,"own fixture"); File.WriteAllText(second,"own fixture");
        var items=store.Add([first,Path.Combine(temp,".","first.wav"),second]); Check(items.Count==2,"Canonical path deduplication");
        store.UpdateProgress(items[0].Id,15,100); store.UpdateProgress(items[0].Id,double.NaN,100);
        store.Bookmark(items[0].Id,12); store.Bookmark(items[0].Id,12.1); store.Bookmark(items[0].Id,double.PositiveInfinity);
        store.Move(items[0].Id,1); store.Data.SelectedId=items[0].Id;
        var now=new DateTimeOffset(2026,11,14,12,0,0,TimeSpan.Zero);
        store.Data.AutoChecks=false; store.Data.LastUpdateAttempt=now; store.Data.DeferUpdatesUntil=now.AddDays(1);
        store.Flush(); var loaded=new LibraryStore(path);
        Check(loaded.Data.Items[1].Id==items[0].Id && loaded.Data.Items[1].ResumeTime==15 && loaded.Data.Items[1].Bookmarks.Count==1 && loaded.Data.SelectedId==items[0].Id,"Persistence/order/progress/bookmark");
        Check(!loaded.Data.AutoChecks && !loaded.Data.AutomaticallyCheckUpdates && loaded.Data.LastUpdateAttempt==now && loaded.Data.DeferUpdatesUntil==now.AddDays(1),"Updater preference persistence/compatible alias");
        Check(!File.ReadAllText(path).Contains("AutoChecks"),"Alias must not fork the persisted schema");
        loaded.UpdateProgress(items[0].Id,99.5,100); Check(loaded.Data.Items[1].ResumeTime==0,"Completed playback reset");
        loaded.UpdateProgress(items[0].Id,-5,100); Check(loaded.Data.Items[1].ResumeTime==0,"Negative progress clamp");
        loaded.Bookmark(items[0].Id,1000); Check(loaded.Data.Items[1].Bookmarks.Last().Time==100,"Bookmark clamp");
        File.Delete(first); loaded.Flush(); var missing=new LibraryStore(path);
        Check(missing.Data.Items.Any(i=>i.Id==items[0].Id),"A missing media file must remain represented");
        missing.Remove(items[1].Id); Check(File.Exists(second) && missing.Data.Items.Count==1,"Remove preserves media");
        missing.Clear(); Check(missing.Data.Items.Count==0 && missing.Data.SelectedId is null && File.Exists(second),"Clear preserves media");
        File.WriteAllText(path,"corrupt"); var recovered=new LibraryStore(path);
        var backups=Directory.GetFiles(temp,"library.json.corrupt-*");
        Check(recovered.RecoveryNotice is not null && backups.Length==1 && File.ReadAllText(backups[0])=="corrupt","Corrupt-library backup contents");
        Check(Directory.GetFiles(temp,"*.tmp").Length==0,"Atomic library temporary cleanup");
        Console.WriteLine("PASS: library canonical deduplication, atomic persistence, missing-file retention, progress/bookmark clamps, updater settings, ordering, media preservation and corrupt-backup contents");
    }
    static void Subtitles()
    {
        var srt=SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:03,000\n<b>Hello</b> &amp; world\n\n2\n00:00:02,000 --> 00:00:04,000\nSecond\n");
        Check(srt.Count==2 && SubtitleParser.At(srt,2.5)=="Hello & world\nSecond" && SubtitleParser.At(srt,4)=="" && SubtitleParser.At(srt,double.NaN)=="","Subtitle overlap/end boundary");
        Check(SubtitleParser.Parse("\uFEFFWEBVTT\r\n\r\ncue\r\n00:01.250 --> 00:02.500\talign:start\r\nCaption\r\n")[0].Start==1.25,"WebVTT BOM/CRLF/settings");
        Check(SubtitleParser.Parse("\uFEFF \n\t").Count==0,"Empty BOM subtitle");
        foreach(string timing in new[]{"00:00:01 --> ","00:00:01 --> \talign:start","00:00:01 --> 00:00:01","00:00:02 --> 00:00:01","00:60:00 --> 01:01:00","00:00:60 --> 00:01:02","00.5:01 --> 00:02","00:01.2500 --> 00:02.500","NaN:01 --> 00:02","00:00:01 --> 00:00:02 --> 00:00:03"})
            Reject<InvalidDataException>(()=>SubtitleParser.Parse("1\n"+timing+"\nBad"));
        var mixed=SubtitleParser.Parse("1\n00:00:01 --> \nBad\n\n2\n00:02 --> 00:03\nGood\n");
        Check(mixed.Count==1 && mixed[0].Text=="Good","Malformed cue must not prevent a later valid cue");
        Reject<InvalidDataException>(()=>SubtitleParser.Parse(new string('x',8*1024*1024+1)));
        Reject<InvalidDataException>(()=>SubtitleParser.Parse("00:01 --> 00:02\n"+new string('x',16385)));
        Console.WriteLine("PASS: typed SRT/WebVTT empty-RHS/malformed timing rejection, bounded clock fields/cues, BOM/CRLF/settings, overlap and end-boundary rendering");
    }
    static void Updates(string temp,string fixturesRoot)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey=Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var bytes=Encoding.UTF8.GetBytes("Original Windows test download");
        var release=new WindowsRelease("0.6.2",2,"https://example.invalid/Luma.zip",Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.Length,"Own fixture");
        byte[] Sign(byte[] payload,ECDsa signer) => JsonSerializer.SerializeToUtf8Bytes(new {payload=Convert.ToBase64String(payload),signature=Convert.ToBase64String(signer.SignData(payload,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))});
        byte[] Envelope(WindowsRelease item,ECDsa? signer=null) => Sign(JsonSerializer.SerializeToUtf8Bytes(new {version=item.Version,build=item.Build,url=item.Url,sha256=item.Sha256,size=item.Size,notes=item.Notes}),signer??key);
        string Config(string feed,string publisher) => JsonSerializer.Serialize(new{feedURL=feed,publicKey=publisher});
        var valid=Envelope(release); Check(WindowsRelease.Verify(valid,publicKey)==release,"Valid ECDSA publisher signature");
        var config=WindowsUpdateConfig.FromJson(Config("https://example.invalid/feed",publicKey)); Check(config is not null,"Valid NIST P-256 config");
        using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Reject<CryptographicException>(()=>WindowsRelease.Verify(valid,Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        var tampered=JsonSerializer.Deserialize<Dictionary<string,string>>(valid)!;
        tampered["payload"]=Convert.ToBase64String(Encoding.UTF8.GetBytes("changed"));
        Reject<CryptographicException>(()=>WindowsRelease.Verify(JsonSerializer.SerializeToUtf8Bytes(tampered),publicKey));
        using var p384=ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string foreign=Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo());
        Check(WindowsUpdateConfig.FromJson(Config("https://example.invalid/feed",foreign)) is null,"Reject foreign curve config");
        Reject<CryptographicException>(()=>WindowsRelease.Verify(valid,foreign));
        string trailingKey=Convert.ToBase64String(key.ExportSubjectPublicKeyInfo().Concat(new byte[]{0}).ToArray());
        Check(WindowsUpdateConfig.FromJson(Config("https://example.invalid/feed",trailingKey)) is null,"Require full SPKI consumption");
        Reject<CryptographicException>(()=>WindowsRelease.Verify(valid,trailingKey));
        ECDsa? k256=null;
        try { k256=ECDsa.Create(ECCurve.CreateFromFriendlyName("secp256k1")); } catch(Exception error) when(error is CryptographicException or PlatformNotSupportedException or NotSupportedException) { }
        if(k256 is not null)
        {
            using(k256)
            {
                string nonNist=Convert.ToBase64String(k256.ExportSubjectPublicKeyInfo());
                Check(WindowsUpdateConfig.FromJson(Config("https://example.invalid/feed",nonNist)) is null,"A 256-bit non-NIST curve must be rejected");
                Reject<CryptographicException>(()=>WindowsRelease.Verify(Envelope(release,k256),nonNist));
            }
        }
        foreach(string raw in new[]{"{}","{", "[]","{\"feedURL\":null,\"publicKey\":null}",Config("", ""),Config("http://example.invalid/feed",publicKey),Config("https://user@example.invalid/feed",publicKey),Config("https://example.invalid/feed#fragment",publicKey)})
            Check(WindowsUpdateConfig.FromJson(raw) is null,"Malformed/unconfigured config must disable checks");
        var shipped=Path.GetFullPath(Path.Combine(fixturesRoot,"..","..","Windows","appsettings.windows.json"));
        if(File.Exists(shipped)) Check(WindowsUpdateConfig.FromJson(File.ReadAllText(shipped)) is null,"Shipped Windows feed must remain unconfigured");
        foreach(var invalid in new[]{release with{Url="http://example.invalid/Luma.zip"},release with{Url="https://user@example.invalid/Luma.zip"},release with{Url="https://example.invalid/Luma.dmg"},release with{Size=0},release with{Size=1073741825},release with{Build=0},release with{Sha256=release.Sha256.ToUpperInvariant()},release with{Notes=new string('x',16385)}})
            Reject<InvalidDataException>(()=>WindowsRelease.Verify(Envelope(invalid),publicKey));
        foreach(string raw in new[]{"{}","[]","{\"payload\":null,\"signature\":0}"}) Reject<InvalidDataException>(()=>WindowsRelease.Verify(Encoding.UTF8.GetBytes(raw),publicKey));
        Reject<InvalidDataException>(()=>WindowsRelease.Verify(new byte[262145],publicKey));
        Reject<InvalidDataException>(()=>WindowsRelease.Verify(Sign(Encoding.UTF8.GetBytes("{\"version\":null,\"build\":2,\"size\":1}"),key),publicKey));
        Check(WindowsRelease.Verify(Envelope(release with{Url="https://example.invalid/Luma.EXE?download=1"}),publicKey).Build==2,"EXE release kind");
        Console.WriteLine("PASS: actual ECDSA P-256 signatures/wrong-key/tamper, named-curve/full-SPKI checks, typed malformed/HTTPS/type/size/hash/manifest bounds and unconfigured Windows feed");

        using(var client=new WindowsUpdateClient(new FixtureHandler((_,_)=>Body(valid)))) Check(client.CheckAsync(config!).GetAwaiter().GetResult()==release,"Actual signed CheckAsync");
        var redirects=new FixtureHandler((request,_)=>request.RequestUri!.AbsolutePath=="/feed"?Redirect("/final"):Body(valid,false));
        using(var client=new WindowsUpdateClient(redirects)) Check(client.CheckAsync(config!).GetAwaiter().GetResult()==release && redirects.Count==2,"Safe HTTPS relative redirect");
        foreach(string target in new[]{"http://example.invalid/Luma.zip","https://user@example.invalid/Luma.zip","https://example.invalid/Luma.zip#fragment"})
        {
            var handler=new FixtureHandler((_,_)=>Redirect(target));
            using var client=new WindowsUpdateClient(handler);
            Reject<InvalidDataException>(()=>client.CheckAsync(config!).GetAwaiter().GetResult()); Check(handler.Count==1,"Unsafe redirect must stop before another request");
        }
        var loop=new FixtureHandler((_,_)=>Redirect("/loop"));
        using(var client=new WindowsUpdateClient(loop)) Reject<InvalidDataException>(()=>client.CheckAsync(config!).GetAwaiter().GetResult());
        Check(loop.Count==6,"Redirect count bound");
        using(var client=new WindowsUpdateClient(new FixtureHandler((_,_)=>Body(new byte[262145],false)))) Reject<InvalidDataException>(()=>client.CheckAsync(config!).GetAwaiter().GetResult());
        using(var client=new WindowsUpdateClient(new FixtureHandler((_,_)=>Body(new byte[262145])))) Reject<InvalidDataException>(()=>client.CheckAsync(config!).GetAwaiter().GetResult());
        var path=Path.Combine(temp,"update.zip");
        using(var client=new WindowsUpdateClient(new FixtureHandler((_,_)=>Body(bytes,false)))) client.DownloadAsync(release,path).GetAwaiter().GetResult();
        Check(File.ReadAllBytes(path).SequenceEqual(bytes),"Verified headerless download bytes");
        var existing=new FixtureHandler((_,_)=>Body(bytes));
        using(var client=new WindowsUpdateClient(existing)) Reject<IOException>(()=>client.DownloadAsync(release,path).GetAwaiter().GetResult());
        Check(existing.Count==0 && File.ReadAllBytes(path).SequenceEqual(bytes),"Existing destination must remain unchanged");
        int badIndex=0;
        foreach(byte[] invalid in new[]{bytes.Concat(new byte[]{1}).ToArray(),bytes[..^1],Enumerable.Repeat((byte)0,bytes.Length).ToArray()})
        {
            string bad=Path.Combine(temp,$"bad-{badIndex++}.zip");
            using var client=new WindowsUpdateClient(new FixtureHandler((_,_)=>Body(invalid,false)));
            Reject<InvalidDataException>(()=>client.DownloadAsync(release,bad).GetAwaiter().GetResult());
            Check(!File.Exists(bad) && Directory.GetFiles(temp,"*.partial").Length==0,"Failed streamed download cleanup");
        }
        var invalidMetadata=new FixtureHandler((_,_)=>Body(bytes));
        using(var client=new WindowsUpdateClient(invalidMetadata)) Reject<InvalidDataException>(()=>client.DownloadAsync(release with{Size=1073741825},Path.Combine(temp,"invalid.zip")).GetAwaiter().GetResult());
        Check(invalidMetadata.Count==0,"Invalid release metadata must fail before HTTP");
        using(var cancel=new CancellationTokenSource())
        {
            var handler=new FixtureHandler((_,_)=>Body(bytes,false,()=>cancel.Cancel()));
            using var client=new WindowsUpdateClient(handler);
            Reject<OperationCanceledException>(()=>client.DownloadAsync(release,Path.Combine(temp,"cancel.zip"),cancel.Token).GetAwaiter().GetResult());
            Check(!File.Exists(Path.Combine(temp,"cancel.zip")) && Directory.GetFiles(temp,"*.partial").Length==0,"Cancelled download cleanup");
        }
        using(var cancel=new CancellationTokenSource())
        {
            using var client=new WindowsUpdateClient(new FixtureHandler((_,_)=>Body(valid,false,()=>cancel.Cancel())));
            Reject<OperationCanceledException>(()=>client.CheckAsync(config!,cancel.Token).GetAwaiter().GetResult());
        }
        var preCancelled=new FixtureHandler((_,_)=>Body(bytes));
        using(var client=new WindowsUpdateClient(preCancelled)) Reject<OperationCanceledException>(()=>client.DownloadAsync(release,Path.Combine(temp,"pre-cancel.zip"),new CancellationToken(true)).GetAwaiter().GetResult());
        Check(preCancelled.Count==0,"Pre-cancellation must not request HTTP");
        var now=new DateTimeOffset(2026,11,14,12,0,0,TimeSpan.Zero);
        Check(UpdatePolicy.Due(now,true,null,null) && !UpdatePolicy.Due(now,true,now,null) && !UpdatePolicy.Due(now,false,null,null),"Daily update throttle/disable");
        Check(UpdatePolicy.Due(now.AddDays(1),true,now,null) && !UpdatePolicy.Due(now.AddDays(1).AddTicks(-1),true,now,null) && UpdatePolicy.Due(now.AddHours(-1),true,now,null),"Throttle boundary/clock rollback");
        Check(!UpdatePolicy.Due(now,true,null,now.AddDays(1)) && !UpdatePolicy.Offer(2,1,false,true,true,now,now.AddDays(1)),"Not Now defer");
        Check(!UpdatePolicy.Offer(2,1,false,true,false,now,null) && UpdatePolicy.Offer(2,1,true,false,false,now,now.AddDays(1)) && !UpdatePolicy.Offer(1,1,true,true,true,now,null) && !UpdatePolicy.Offer(0,-1,true,true,true,now,null),"Idle/manual/newer-build policy");
        Console.WriteLine("PASS: fake-HTTP signed checks, redirect/downgrade bounds, streaming manifest/download caps, exact hash/size, existing-file preservation, partial cleanup, mid-stream/pre-cancellation and deterministic update policy");
    }
    static HttpResponseMessage Redirect(string target) { var response=new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location=new Uri(target,UriKind.RelativeOrAbsolute); return response; }
    static HttpResponseMessage Body(byte[] bytes,bool knownLength=true,Action? afterRead=null) => new(HttpStatusCode.OK){Content=knownLength?new ByteArrayContent(bytes):new StreamContent(new ChunkStream(bytes,afterRead))};
    sealed class FixtureHandler(Func<HttpRequestMessage,CancellationToken,HttpResponseMessage> response):HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        { token.ThrowIfCancellationRequested(); Count++; return Task.FromResult(response(request,token)); }
    }
    sealed class ChunkStream(byte[] bytes,Action? afterRead):Stream
    {
        int position; bool notified;
        public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException(); public override long Position{get=>position;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
        public override int Read(Span<byte> buffer)
        {
            int count=Math.Min(Math.Min(buffer.Length,7),bytes.Length-position); bytes.AsSpan(position,count).CopyTo(buffer); position+=count;
            if(count>0 && !notified){notified=true;afterRead?.Invoke();} return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){token.ThrowIfCancellationRequested();return ValueTask.FromResult(Read(buffer.Span));}
        public override void Flush(){} public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException(); public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
