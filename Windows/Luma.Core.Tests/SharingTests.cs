using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Luma.Core.Sharing;

namespace Luma.Core.Tests;

public static class SharingTests
{
    public static void Run(string fixtures) { _ = fixtures; RunAsync().GetAwaiter().GetResult(); }
    static async Task RunAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Luma-sharing-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "original-private.mp4"); byte[] bytes = Enumerable.Range(0, 100003).Select(i => (byte)(i * 17)).ToArray(); await File.WriteAllBytesAsync(file, bytes);
            var time = new ManualTime(); await using var server = new PlaylistSharingServer("Original local test", time);
            server.SetPlaylist([new("opaque_123", file, "Original <caption>", 12, 3)]); server.Start(IPAddress.Loopback);
            Check(server.IsRunning && server.Port > 0 && server.PairingCode.Length == 6 && server.PairedDevices == 0, "Sharing begins only with explicit Start and random bounded pairing state");
            Response info = await Send(server.Port, "GET", "/v1/info");
            using (var json = JsonDocument.Parse(info.Body)) Check(info.Status == 200 && json.RootElement.GetProperty("version").GetInt32() == 1 && json.RootElement.GetProperty("pairingRequired").GetBoolean(), "Public protocol info");
            Check((await Send(server.Port, "GET", "/v1/playlist")).Status == 401, "Unpaired playlist denied");
            Check((await Send(server.Port, "POST", "/v1/pair", "{\"code\":\"bad\"}")).Status == 401, "Invalid pairing code denied");
            string token = await Pair(server); string bearer = "Authorization: Bearer " + token + "\r\n";
            Response playlist = await Send(server.Port, "GET", "/v1/playlist", headers: bearer);
            Check(playlist.Status == 200 && !Encoding.UTF8.GetString(playlist.Body).Contains(folder, StringComparison.Ordinal), "Playlist exposes no absolute paths");
            string streamPath;
            using (var json = JsonDocument.Parse(playlist.Body))
            {
                var item = json.RootElement.GetProperty("items")[0]; streamPath = item.GetProperty("streamPath").GetString()!;
                Check(item.GetProperty("id").GetString() == "opaque_123" && item.GetProperty("title").GetString() == "Original <caption>"
                    && item.GetProperty("resumeSeconds").GetDouble() == 3 && item.GetProperty("mimeType").GetString() == "video/mp4", "Exact live playlist metadata");
            }
            Check(token.Length == 43 && server.PairedDevices == 1 && streamPath.EndsWith("?token=" + token, StringComparison.Ordinal), "256-bit in-memory URL token");
            Response all = await Send(server.Port, "GET", streamPath);
            Check(all.Status == 200 && all.Body.SequenceEqual(bytes) && all.Headers["Content-Length"] == bytes.Length.ToString()
                && all.Headers["Connection"] == "close" && !all.Headers.Keys.Any(k => k.StartsWith("Access-Control", StringComparison.OrdinalIgnoreCase)), "Exact original streaming bytes, length, connection-close and no CORS");
            foreach (var (range, start, count) in new[] { ("bytes=0-0", 0, 1), ("bytes=7-23", 7, 17), ("bytes=99999-", 99999, 4), ("bytes=-9", bytes.Length - 9, 9), ("bytes=100000-999999", 100000, 3) })
            {
                var response = await Send(server.Port, "GET", streamPath, headers: "Range: " + range + "\r\n");
                Check(response.Status == 206 && response.Body.SequenceEqual(bytes.AsSpan(start, count).ToArray())
                    && response.Headers["Content-Range"] == $"bytes {start}-{start + count - 1}/{bytes.Length}", "Exact single/suffix/open range");
            }
            Response head = await Send(server.Port, "HEAD", streamPath, headers: "Range: bytes=5-11\r\n");
            Check(head.Status == 206 && head.Body.Length == 0 && head.Headers["Content-Length"] == "7", "HEAD range sends metadata without payload");
            foreach (string invalid in new[] { "bytes=100003-", "bytes=4-2", "bytes=-0", "bytes=0-2,4-8", "bytes=9223372036854775808-", "items=0-1", "bytes=+1-5", "bytes=1 -5" })
            { var response = await Send(server.Port, "GET", streamPath, headers: "Range: " + invalid + "\r\n"); Check(response.Status == 416 && response.Headers["Content-Range"] == "bytes */" + bytes.Length && response.Body.Length == 0, "Invalid range rejected with full length"); }
            Check((await Send(server.Port, "GET", "/v1/media/opaque_123?token=" + new string('a', 43))).Status == 401, "Forged token rejected");
            Check((await Send(server.Port, "GET", "/v1/media/%2e%2e?token=" + token)).Status == 400 && (await Send(server.Port, "GET", "/v1/media/arbitrary-path?token=" + token)).Status == 404, "Encoded traversal/arbitrary paths unavailable");
            Check((await Send(server.Port, "POST", "/v1/media/opaque_123?token=" + token, "x")).Status is 404 or 405, "No upload route");
            Check((await Raw(server.Port, "GET /v1/info HTTP/1.1\r\nHost: local\r\nHost: duplicate\r\n\r\n")).Status == 400, "Duplicate headers rejected");
            Check((await Raw(server.Port, "POST /v1/pair HTTP/1.1\r\nHost: local\r\nTransfer-Encoding: chunked\r\n\r\n")).Status == 400, "Chunked/body smuggling rejected");
            Check((await Raw(server.Port, "POST /v1/pair HTTP/1.1\r\nHost: local\r\nContent-Length: 4097\r\n\r\n")).Status == 413, "Bounded request body");
            Check((await Raw(server.Port, "GET /v1/info HTTP/1.1\r\nHost: local\r\nX-Bad: text\u000bvalue\r\n\r\n")).Status == 400, "Control bytes rejected");

            string longTitle = new string('x', 511) + "😀" + "tail";
            server.SetPlaylist(Enumerable.Range(0, 1002).Select(i => new SharedMediaItem("bounded_" + i, file, longTitle, 40_000_000, 40_000_000)));
            Response bounded = await Send(server.Port, "GET", "/v1/playlist", headers: bearer);
            using (var json = JsonDocument.Parse(bounded.Body))
            {
                var root = json.RootElement; var entries = root.GetProperty("items");
                Check(bounded.Body.Length <= 4 * 1024 * 1024 && entries.GetArrayLength() == 1000 && root.GetProperty("truncated").GetBoolean() && root.GetProperty("totalItems").GetInt32() == 1002, "Shared wire item/JSON limits and exact truncation metadata");
                string title = entries[0].GetProperty("title").GetString()!;
                Check(title.Length == 511 && !title.Any(char.IsSurrogate) && entries[0].GetProperty("durationSeconds").GetDouble() == 31_536_000 && entries[0].GetProperty("resumeSeconds").GetDouble() == 31_536_000, "Surrogate-safe 512UTF16 title and one-year time bound");
            }
            server.SetPlaylist([new("new_id", file, "Updated", 20, 9)]);
            Check((await Send(server.Port, "GET", streamPath)).Status == 404, "Removed playlist entry revoked");
            using (var json = JsonDocument.Parse((await Send(server.Port, "GET", "/v1/playlist", headers: bearer)).Body)) Check(json.RootElement.GetProperty("items")[0].GetProperty("id").GetString() == "new_id", "Playlist updates live");
            File.Delete(file); Check((await Send(server.Port, "GET", "/v1/media/new_id?token=" + token)).Status == 404, "Missing file is not served");
            time.Advance(TimeSpan.FromMinutes(11));
            Check((await Send(server.Port, "POST", "/v1/pair", JsonSerializer.Serialize(new { code = server.PairingCode }))).Status == 401, "Pairing code expires after ten minutes");
            Check((await Send(server.Port, "GET", "/v1/playlist", headers: bearer)).Status == 200, "Paired token remains until stop");

            string large = Path.Combine(folder, "large.mkv"); using (var sparse = File.Create(large)) sparse.SetLength(64L * 1024 * 1024);
            server.SetPlaylist([new("large", large, "Original synthetic sparse file")]);
            using var active = new TcpClient { ReceiveBufferSize = 1024 }; await active.ConnectAsync(IPAddress.Loopback, server.Port);
            await active.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /v1/media/large?token=" + token + " HTTP/1.1\r\nHost: local\r\n\r\n"));
            await Until(() => server.ActiveConnections > 0);
            await Task.Delay(TimeSpan.FromSeconds(16));
            Check(server.ActiveConnections > 0, "Authenticated media backpressure survives a phone pause beyond the old fifteen-second write limit");
            server.SetPlaylist([]); await Until(() => server.ActiveConnections == 0);
            using var hanging = new TcpClient(); await hanging.ConnectAsync(IPAddress.Loopback, server.Port); await hanging.GetStream().WriteAsync("GET /v1/info HTTP/1.1\r\n"u8.ToArray()); await Until(() => server.ActiveConnections > 0);
            int stoppedPort = server.Port; server.Stop(); await Until(() => server.ActiveConnections == 0);
            Check(!server.IsRunning && server.PairedDevices == 0 && server.PairingCode.Length == 0, "Stop cancels active connections and revokes tokens/code");
            using (var rejected = new TcpClient())
            using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                Task connection = rejected.ConnectAsync(IPAddress.Loopback, stoppedPort, connectTimeout.Token).AsTask();
                try { await connection.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("Stopped listener accepted a client"); }
                catch (SocketException) { }
                catch (TimeoutException) { }
                catch (OperationCanceledException) when (connectTimeout.IsCancellationRequested) { }
                finally
                {
                    connectTimeout.Cancel(); rejected.Dispose();
                    try { await connection; } catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
            await using var renewed = new PlaylistSharingServer("New sharing session", time); renewed.Start(IPAddress.Loopback);
            Check((await Send(renewed.Port, "GET", "/v1/playlist", headers: bearer)).Status == 401, "Previous session token is not reusable");
            for (int i = 0; i < 5; i++) Check((await Send(renewed.Port, "POST", "/v1/pair", "{\"code\":\"bad\"}")).Status == 401, "Bounded failed attempts");
            Check((await Send(renewed.Port, "POST", "/v1/pair", "{\"code\":\"bad\"}")).Status == 429, "Per-source pairing rate limit");
            time.Advance(TimeSpan.FromMinutes(1.1)); _ = await Pair(renewed);
            foreach (string accepted in new[] { "127.0.0.1", "10.1.2.3", "172.16.1.2", "192.168.2.1", "169.254.2.3", "::1", "fc01::1", "fe80::1", "::ffff:192.168.1.2" }) Check(PlaylistSharingServer.IsPrivateAddress(IPAddress.Parse(accepted)), "Private source accepted");
            foreach (string denied in new[] { "8.8.8.8", "172.32.0.1", "0.0.0.0", "224.0.0.1", "2001:4860:4860::8888", "ff02::1" }) Check(!PlaylistSharingServer.IsPrivateAddress(IPAddress.Parse(denied)), "Public/multicast source rejected");
            Console.WriteLine("PASS: local sharing protocol pairing/auth/expiry/rate limits, live opaque playlist, no private paths/CORS, exact GET/HEAD/suffix/open ranges, malformed requests, missing/removal revocation, active-stream cancellation and stopped listener/token revocation");
        }
        finally { Directory.Delete(folder, true); }
    }
    static async Task<string> Pair(PlaylistSharingServer server)
    { Response response = await Send(server.Port, "POST", "/v1/pair", JsonSerializer.Serialize(new { code = server.PairingCode })); using var json = JsonDocument.Parse(response.Body); Check(response.Status == 200, "Valid pairing succeeds"); return json.RootElement.GetProperty("token").GetString()!; }
    static Task<Response> Send(int port, string method, string target, string body = "", string headers = "")
    { byte[] encoded = Encoding.UTF8.GetBytes(body); return Raw(port, method + " " + target + " HTTP/1.1\r\nHost: localhost\r\n" + headers + (method == "POST" ? "Content-Length: " + encoded.Length + "\r\nContent-Type: application/json\r\n" : "") + "\r\n" + body); }
    static async Task<Response> Raw(int port, string request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token); NetworkStream stream = client.GetStream(); await stream.WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token);
        using var buffer = new MemoryStream(); byte[] chunk = new byte[65536]; int read; while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0) { Check(buffer.Length + read <= 4 * 1024 * 1024 + 16384, "Test response bound"); buffer.Write(chunk, 0, read); }
        byte[] bytes = buffer.ToArray(); int end = -1; for (int i = 0; i + 3 < bytes.Length; i++) if (bytes.AsSpan(i, 4).SequenceEqual("\r\n\r\n"u8)) { end = i; break; }
        Check(end >= 0, "HTTP response header exists"); string[] lines = Encoding.ASCII.GetString(bytes, 0, end).Split("\r\n"); int status = int.Parse(lines[0].Split(' ')[1]); var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1)) { int colon = line.IndexOf(':'); headers.Add(line[..colon], line[(colon + 1)..].Trim()); } return new(status, headers, bytes[(end + 4)..]);
    }
    static async Task Until(Func<bool> predicate) { DateTime end = DateTime.UtcNow.AddSeconds(3); while (!predicate()) { if (DateTime.UtcNow > end) throw new TimeoutException("Sharing operation did not settle"); await Task.Delay(10); } }
    static void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Sharing test: " + label); }
    sealed record Response(int Status, Dictionary<string, string> Headers, byte[] Body);
    sealed class ManualTime : TimeProvider { long ticks; public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Read(ref ticks)); public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks); }
}
