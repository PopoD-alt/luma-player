using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Luma.Core.Library;
using Luma.Core.Sharing;

namespace Luma.Core.Tests;

internal static class SharedProgressTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Luma-progress-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { Library(directory); Protocol(directory).GetAwaiter().GetResult(); }
        finally { Directory.Delete(directory, true); }
    }
    static void Library(string directory)
    {
        string media = Path.Combine(directory, "library-video.mp4"), path = Path.Combine(directory, "progress-library.json"); File.WriteAllText(media, "original synthetic content");
        Guid id = Guid.NewGuid(); File.WriteAllText(path, JsonSerializer.Serialize(new { Items = new[] { new { Id = id, FilePath = media, Duration = 100d, ResumeTime = 15d } } }));
        var clock = new ManualTime(); var store = new LibraryStore(path, clock); var item = store.Data.Items.Single();
        Check(item.MobileProgressSeconds is null && item.ComputerProgressSeconds is null && item.ComputerProgressDetail.Contains("15%", StringComparison.Ordinal), "Old library has distinct unknown phone state and desktop fallback");
        store.UpdateProgress(id, 99.5, 100);
        Check(item.ResumeTime == 0 && item.ComputerProgressSeconds == 99.5 && !item.ComputerCompleted, "Near-end restart policy does not falsely report completion");
        store.UpdateProgress(id, 100, 100, completed: true); double stamp = item.ComputerUpdatedAtUnixSeconds;
        Check(item.ResumeTime == 0 && item.ComputerProgressSeconds == 100 && item.ComputerCompleted && stamp > 0, "Completed desktop retains actual progress independently of restart bookmark");
        clock.Advance(TimeSpan.FromSeconds(10)); store.UpdateProgress(id, 100, 100, completed: true);
        Check(item.ComputerUpdatedAtUnixSeconds == stamp, "A paused checkpoint does not pretend playback just advanced");
        Check(store.UpdateMobileProgress(id, 200, 999, true, stamp + 1), "Known-duration phone update accepted");
        Check(item.Duration == 100 && item.MobileProgressSeconds == 100 && item.MobileDurationSeconds == 100 && item.MobileCompleted
            && item.ResumeTime == 0 && item.ComputerProgressSeconds == 100, "Phone clamp never overwrites computer progress or authoritative duration");
        Check(store.UpdateMobileProgress(id, 0, 100, false, stamp + 2) && item.MobileProgressSeconds == 0 && !item.MobileCompleted, "Phone zero is a known beginning, not absent progress");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1, 31_536_001 })
            Check(!store.UpdateMobileProgress(id, invalid, 100, false, stamp + 3), "Invalid phone progress rejected before mutation");
        Check(!store.UpdateMobileProgress(id, 4, 100, false, 4_102_444_801) && item.MobileProgressSeconds == 0, "Invalid timestamp rejected");
        string unknown = Path.Combine(directory, "unknown-duration.mp4"); File.WriteAllText(unknown, "own fixture"); var second = store.Add([unknown]).Single();
        Check(store.UpdateMobileProgress(second.Id, 40, 80, false, stamp + 4) && second.Duration == 0 && second.MobileDurationSeconds == 80 && second.PhoneProgressDetail.Contains("50%", StringComparison.Ordinal), "Phone-reported duration is separate and provides percentage for unopened desktop file");
        store.Flush(); var loaded = new LibraryStore(path, clock); var restored = loaded.Data.Items.Single(i => i.Id == id);
        Check(restored.MobileProgressSeconds == 0 && restored.ComputerProgressSeconds == 100 && restored.ComputerCompleted && restored.ResumeTime == 0, "Independent phone/computer progress persists without changing resume semantics");
        Check(loaded.Data.Items.Single(i => i.Id == second.Id).Duration == 0 && loaded.Data.Items.Single(i => i.Id == second.Id).MobileDurationSeconds == 80, "Separate phone duration persists");
        var knownLater = loaded.Data.Items.Single(i => i.Id == second.Id); loaded.UpdateProgress(second.Id, 2, 20);
        Check(knownLater.Duration == 20 && knownLater.MobileDurationSeconds == 20 && knownLater.MobileProgressSeconds == 20, "New authoritative desktop duration canonicalizes earlier phone metadata");
        Console.WriteLine("PASS: backward-compatible computer/phone progress persistence, completion/bookmark distinction, timestamp stability, duration isolation and zero/unknown semantics");
    }
    static async Task Protocol(string directory)
    {
        string media = Path.Combine(directory, "shared-progress.mp4"); await File.WriteAllTextAsync(media, "original generated media");
        var clock = new ManualTime(); await using var server = new PlaylistSharingServer("Progress fixture", clock);
        server.SetPlaylist([new("progress_item", media, "Original", 12, 3, ComputerProgressSeconds: 4, ComputerUpdatedAtUnixSeconds: 100)]);
        SharedPlaybackProgress? received = null; int reports = 0; server.ProgressReported += update => { received = update; Interlocked.Increment(ref reports); };
        server.Start(IPAddress.Loopback);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri("http://127.0.0.1:" + server.Port), Timeout = TimeSpan.FromSeconds(5) };
        using (var info = JsonDocument.Parse(await client.GetStringAsync("/v1/info"))) Check(info.RootElement.GetProperty("progressReporting").GetBoolean(), "Progress capability is advertised additively");
        string Body(double position, double duration = 99, bool completed = false, string id = "progress_item") => JsonSerializer.Serialize(new { itemId = id, positionSeconds = position, durationSeconds = duration, completed });
        async Task<HttpStatusCode> Post(string body, string path = "/v1/progress") { using var response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json")); return response.StatusCode; }
        Check(await Post(Body(5)) == HttpStatusCode.Unauthorized && reports == 0, "Unpaired progress never reaches persistence notification");
        using var pair = await client.PostAsync("/v1/pair", new StringContent(JsonSerializer.Serialize(new { code = server.PairingCode }), Encoding.UTF8, "application/json"));
        using var pairJson = JsonDocument.Parse(await pair.Content.ReadAsStringAsync()); string token = pairJson.RootElement.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var before = JsonDocument.Parse(await client.GetStringAsync("/v1/playlist")))
        {
            var entry = before.RootElement.GetProperty("items")[0];
            Check(entry.GetProperty("mobileProgressSeconds").ValueKind == JsonValueKind.Null && entry.GetProperty("computerProgressSeconds").GetDouble() == 4 && entry.GetProperty("resumeSeconds").GetDouble() == 3, "New fields preserve old bookmark and unknown phone state");
        }
        using (var response = await client.PostAsync("/v1/progress", new StringContent(Body(50, 1000, true), Encoding.UTF8, "application/json")))
        using (var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var root = accepted.RootElement;
            Check(response.StatusCode == HttpStatusCode.OK && root.GetProperty("version").GetInt32() == 1 && root.GetProperty("positionSeconds").GetDouble() == 12
                && root.GetProperty("durationSeconds").GetDouble() == 12 && root.GetProperty("completed").GetBoolean(), "Canonical accepted response clamps to host duration");
        }
        Check(reports == 1 && received is { ItemId: "progress_item", PositionSeconds: 12, DurationSeconds: 12, Completed: true } && received.Revision > 0, "Accepted phone metadata notifies once with monotonic revision");
        // A periodic desktop snapshot can race the UI's queued progress handler.
        server.SetPlaylist([new("progress_item", media, "Original", 6, 3, ComputerProgressSeconds: 4)]);
        using (var after = JsonDocument.Parse(await client.GetStringAsync("/v1/playlist")))
        {
            var entry = after.RootElement.GetProperty("items")[0];
            Check(entry.GetProperty("mobileProgressSeconds").GetDouble() == 6 && entry.GetProperty("mobileDurationSeconds").GetDouble() == 6
                && entry.GetProperty("mobileCompleted").GetBoolean() && entry.GetProperty("durationSeconds").GetDouble() == 6
                && entry.GetProperty("resumeSeconds").GetDouble() == 3 && entry.GetProperty("computerProgressSeconds").GetDouble() == 4,
                "Desktop checkpoint retains pending phone metadata, canonicalizes newly known host duration and leaves computer position alone");
        }
        Check(await Post(Body(0)) == HttpStatusCode.OK && received!.PositionSeconds == 0 && !received.Completed, "Known zero update clears completion without absence");
        foreach (string bad in new[] { "[]", "{}", "null", "{", "{\"itemId\":\"progress_item\",\"positionSeconds\":1,\"positionSeconds\":2,\"durationSeconds\":12,\"completed\":false}",
            "{\"itemId\":\"progress_item\",\"positionSeconds\":\"4\",\"durationSeconds\":12,\"completed\":false}",
            "{\"itemId\":\"progress_item\",\"positionSeconds\":1e999,\"durationSeconds\":12,\"completed\":false}",
            "{\"itemId\":\"progress_item\",\"positionSeconds\":-1,\"durationSeconds\":12,\"completed\":false}",
            "{\"itemId\":\"progress_item\",\"positionSeconds\":31536001,\"durationSeconds\":12,\"completed\":false}",
            "{\"itemId\":\"progress_item\",\"positionSeconds\":1,\"durationSeconds\":12,\"completed\":\"false\"}",
            "{\"itemId\":\"progress_item\",\"positionSeconds\":1,\"durationSeconds\":12,\"completed\":false,\"filePath\":\"/arbitrary\"}" })
            Check(await Post(bad) == HttpStatusCode.BadRequest && reports == 2, "Malformed/duplicate/typed/out-of-bound progress is rejected before notification");
        Check(await OversizedProgressHeader(server.Port, token) == 413 && reports == 2, "Oversized progress is rejected from its headers before reading a body or notifying persistence");
        Check(await Post(Body(1), "/v1/progress?token=" + token) == HttpStatusCode.MethodNotAllowed, "Progress does not accept query credentials");
        Check(await Post(Body(1, id: "removed")) == HttpStatusCode.NotFound && reports == 2, "Only live item IDs accept progress");
        await PartialReport(server, token, Body(8), () => server.SetPlaylist([]), expectedStatus: 404);
        Check(reports == 2, "An item removed while the request body is in flight cannot receive progress");
        server.SetPlaylist([]); Check(await Post(Body(1)) == HttpStatusCode.NotFound && reports == 2, "Removed items cannot be resurrected");
        server.SetPlaylist([new("progress_item", media, "Original", 12, 3)]); File.Delete(media);
        Check(await Post(Body(1)) == HttpStatusCode.NotFound && reports == 2, "Missing file cannot receive progress");
        await File.WriteAllTextAsync(media, "own generated media");
        clock.Advance(TimeSpan.FromMinutes(2));
        for (int i = 0; i < 30; i++) Check(await Post(Body(1)) == HttpStatusCode.OK, "Thirty progress posts per minute allowed");
        Check(await Post(Body(2)) == HttpStatusCode.TooManyRequests && reports == 32, "Per-token progress rate limit is separate from pairing");
        clock.Advance(TimeSpan.FromMinutes(1.01)); Check(await Post(Body(2)) == HttpStatusCode.OK && reports == 33, "Progress rate window recovers");
        await using var restarted = new PlaylistSharingServer("Restarted", clock); restarted.SetPlaylist([new("progress_item", media, "Original")]); restarted.Start(IPAddress.Loopback);
        using var revoked = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:" + restarted.Port) };
        revoked.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var denied = await revoked.PostAsync("/v1/progress", new StringContent(Body(1), Encoding.UTF8, "application/json"));
        Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Old-session tokens cannot persist progress in a new sharing session");
        await PartialReport(server, token, Body(9), server.Stop, expectedStatus: null);
        Check(!server.IsRunning && server.PairedDevices == 0 && reports == 33, "Stop while a body is in flight revokes the session before progress notification");
        Console.WriteLine("PASS: authenticated bounded live progress API, strict types/duplicates, canonical duration clamp, zero/completion, pending-update retention, revocation/removal and per-token rate limiting");
    }
    static async Task<int> OversizedProgressHeader(int port, string token)
    {
        // Exercise the early Content-Length gate without queuing an invalid
        // body. Closing unread bytes may send a TCP reset on Windows and mask
        // an otherwise valid 413 response from a buffering HttpClient.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token); var stream = client.GetStream();
        string header = $"POST /v1/progress HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {token}\r\nContent-Length: 4097\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token);
        using var response = new MemoryStream(); byte[] bytes = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0) { Check(response.Length + read <= 8192, "Oversize rejection response bound"); response.Write(bytes, 0, read); }
        string status = Encoding.ASCII.GetString(response.ToArray()).Split("\r\n", 2)[0];
        string[] fields = status.Split(' '); Check(fields.Length >= 3 && fields[0] == "HTTP/1.1" && int.TryParse(fields[1], out _), "Oversize rejection has an HTTP status");
        return int.Parse(fields[1]);
    }
    static async Task PartialReport(PlaylistSharingServer server, string token, string body, Action duringBody, int? expectedStatus)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        byte[] data = Encoding.UTF8.GetBytes(body); string header = $"POST /v1/progress HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {token}\r\nContent-Length: {data.Length}\r\n\r\n";
        var stream = client.GetStream(); await stream.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token); await stream.WriteAsync(data.AsMemory(0, data.Length / 2), timeout.Token);
        DateTime deadline = DateTime.UtcNow.AddSeconds(2); while (server.ActiveConnections == 0) { Check(DateTime.UtcNow < deadline, "Partial request admitted"); await Task.Delay(5, timeout.Token); }
        duringBody();
        try
        {
            await stream.WriteAsync(data.AsMemory(data.Length / 2), timeout.Token); using var response = new MemoryStream(); byte[] bytes = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0) { Check(response.Length + read <= 8192, "Partial response bound"); response.Write(bytes, 0, read); }
            string text = Encoding.ASCII.GetString(response.ToArray());
            if (expectedStatus is { } status) Check(text.StartsWith("HTTP/1.1 " + status + " ", StringComparison.Ordinal), "Partial request sees current live item state");
            else Check(text.Length == 0, "Stopped partial request receives no success");
        }
        catch (Exception error) when (expectedStatus is null && error is IOException or SocketException or OperationCanceledException) { }
    }
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Shared progress: " + message); }
    sealed class ManualTime : TimeProvider
    {
        long ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Read(ref ticks));
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }
}
