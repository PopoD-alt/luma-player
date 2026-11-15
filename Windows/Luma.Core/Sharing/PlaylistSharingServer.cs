using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Luma.Core.Sharing;

public sealed record SharedMediaItem(string Id, string FilePath, string Title, double DurationSeconds = 0, double ResumeSeconds = 0);

/// <summary>Opt-in HTTP/1.1 implementation of Networking/PROTOCOL.md. No arbitrary path, upload, CORS or playback-control route.</summary>
public sealed class PlaylistSharingServer : IDisposable, IAsyncDisposable
{
    const int MaximumConnections = 12, MaximumHeaderBytes = 16384, MaximumBodyBytes = 4096;
    readonly TimeProvider time;
    readonly string name;
    readonly CancellationTokenSource lifetime = new();
    readonly ConcurrentDictionary<int, Connection> connections = new();
    readonly ConcurrentDictionary<int, Task> workers = new();
    readonly object authentication = new();
    readonly List<byte[]> tokenHashes = [];
    readonly Dictionary<string, Queue<DateTimeOffset>> attempts = new(StringComparer.Ordinal);
    readonly Queue<DateTimeOffset> globalAttempts = [];
    PlaylistSnapshot playlist = new(new(StringComparer.Ordinal), 0, false);
    TcpListener? listener;
    Task? accepting;
    int running, nextConnection;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name => name;
    public string PairingCode { get; private set; } = "";
    public DateTimeOffset PairingExpiresAt { get; private set; }
    public bool IsRunning => Volatile.Read(ref running) == 1;
    public int Port { get; private set; }
    public int PairedDevices { get { lock (authentication) return tokenHashes.Count; } }
    public int ActiveConnections => connections.Count;
    public int ActiveMediaStreams => connections.Values.Count(c => Volatile.Read(ref c.Streaming) != 0);
    public IReadOnlyList<string> Addresses => LocalAddresses().Select(a => new UriBuilder("http", a.ToString(), Port).Uri.GetLeftPart(UriPartial.Authority)).ToArray();

    public PlaylistSharingServer(string displayName, TimeProvider? timeProvider = null)
    {
        name = CleanText(displayName, 128); if (name.Length == 0) name = "Luma Player";
        time = timeProvider ?? TimeProvider.System;
    }
    public void SetPlaylist(IEnumerable<SharedMediaItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var updated = new Dictionary<string, SharedMediaItem>(StringComparer.Ordinal);
        var source = items.Take(100001).ToArray();
        if (source.Length > 100000) throw new ArgumentException("Playlist source exceeds its inspection bound.", nameof(items));
        foreach (var item in source.Take(1000))
        {
            if (!OpaqueId(item.Id) || !Path.IsPathFullyQualified(item.FilePath)) throw new ArgumentException("Shared items require an opaque ID and an absolute local path.", nameof(items));
            if (!updated.TryAdd(item.Id, item with { FilePath = Path.GetFullPath(item.FilePath), Title = CleanText(item.Title, 512),
                DurationSeconds = SafeTime(item.DurationSeconds), ResumeSeconds = SafeTime(item.ResumeSeconds) })) throw new ArgumentException("Duplicate shared item ID.", nameof(items));
        }
        Volatile.Write(ref playlist, new(updated, source.Length, source.Length > 1000));
        foreach (var connection in connections.Values)
            if (connection.Item is { } old && (!updated.TryGetValue(old.Id, out var live) || !SamePath(old.FilePath, live.FilePath))) connection.Client.Dispose();
    }
    public void Start(IPAddress? bindAddress = null)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Create a new sharing session after stopping.");
        try
        {
            listener = new TcpListener(bindAddress ?? IPAddress.IPv6Any, 0);
            if (bindAddress is null) listener.Server.DualMode = true;
            listener.Start(MaximumConnections);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            PairingCode = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            PairingExpiresAt = time.GetUtcNow().AddMinutes(10);
            accepting = AcceptAsync(lifetime.Token);
        }
        catch { Interlocked.Exchange(ref running, 2); listener?.Stop(); lifetime.Cancel(); throw; }
    }
    public void Stop()
    {
        if (Interlocked.Exchange(ref running, 2) == 2) return;
        lock (authentication)
        {
            foreach (var hash in tokenHashes) CryptographicOperations.ZeroMemory(hash);
            tokenHashes.Clear(); attempts.Clear(); globalAttempts.Clear(); PairingCode = "";
        }
        lifetime.Cancel(); listener?.Stop();
        foreach (var connection in connections.Values) connection.Client.Dispose();
    }
    public void Dispose() => Stop();
    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            if (accepting is not null) await accepting.ConfigureAwait(false);
            await Task.WhenAll(workers.Values).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or TimeoutException) { }
    }
    async Task AcceptAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = await listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (client.Client.RemoteEndPoint is not IPEndPoint source || !IsPrivateAddress(source.Address)) { client.Dispose(); continue; }
                if (connections.Count >= MaximumConnections) { client.Dispose(); continue; }
                client.NoDelay = true; int id = Interlocked.Increment(ref nextConnection);
                var connection = new Connection(client, source.Address.MapToIPv6().ToString()); connections[id] = connection;
                Task worker = ServeAsync(connection, token); workers[id] = worker;
                _ = worker.ContinueWith(_ => { connections.TryRemove(id, out var ignored); workers.TryRemove(id, out var ignoredTask); client.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    async Task ServeAsync(Connection connection, CancellationToken token)
    {
        using var client = connection.Client;
        try
        {
            NetworkStream stream = client.GetStream(); Request request;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try { request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false); }
                catch (BadRequest error) { await Reply(stream, error.Status, new { error = "Invalid request" }, false, token).ConfigureAwait(false); return; }
            }
            if (!IsRunning) return;
            bool head = request.Method == "HEAD";
            if (request.Path == "/v1/info" && request.Query.Length == 0 && request.Method == "GET")
            { await Reply(stream, 200, new { version = 1, name, id = Id, pairingRequired = true }, false, token).ConfigureAwait(false); return; }
            if (request.Path == "/v1/pair" && request.Query.Length == 0 && request.Method == "POST")
            {
                if (!PairAttemptAllowed(connection.Address)) { await Reply(stream, 429, new { error = "Too many pairing attempts" }, false, token).ConfigureAwait(false); return; }
                string? code = null;
                try
                {
                    using var json = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 4 });
                    if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject().Count(p => p.NameEquals("code")) == 1
                        && json.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
                }
                catch (JsonException) { }
                string? issued = IssueToken(code);
                await Reply(stream, issued is null ? 401 : 200, issued is null ? (object)new { error = "Pairing code is invalid or expired" } : new { token = issued, name }, false, token).ConfigureAwait(false); return;
            }
            if (request.Path == "/v1/playlist" && request.Query.Length == 0 && request.Method == "GET")
            {
                string? bearer = Bearer(request);
                if (!Authorized(bearer)) { await Reply(stream, 401, new { error = "Pairing required" }, false, token).ConfigureAwait(false); return; }
                var snapshot = Volatile.Read(ref playlist);
                var items = snapshot.Items.Values.Where(i => File.Exists(i.FilePath)).Select(i => new
                { id = i.Id, title = i.Title, durationSeconds = i.DurationSeconds, resumeSeconds = i.ResumeSeconds, mimeType = MimeType(i.FilePath), streamPath = "/v1/media/" + i.Id + "?token=" + bearer }).ToArray();
                await Reply(stream, 200, new { version = 1, name, items, truncated = snapshot.Truncated, totalItems = snapshot.TotalItems }, false, token).ConfigureAwait(false); return;
            }
            const string mediaPrefix = "/v1/media/";
            if (request.Path.StartsWith(mediaPrefix, StringComparison.Ordinal) && request.Method is "GET" or "HEAD")
            {
                string? supplied = QueryToken(request.Query) ?? Bearer(request);
                if (!Authorized(supplied)) { await Reply(stream, 401, new { error = "Pairing required" }, head, token).ConfigureAwait(false); return; }
                string id = request.Path[mediaPrefix.Length..];
                if (!OpaqueId(id) || !Volatile.Read(ref playlist).Items.TryGetValue(id, out var item)) { await Reply(stream, 404, new { error = "Media unavailable" }, head, token).ConfigureAwait(false); return; }
                await StreamMedia(stream, request, connection, item, head, token).ConfigureAwait(false); return;
            }
            await Reply(stream, request.Path is "/v1/info" or "/v1/pair" or "/v1/playlist" ? 405 : 404, new { error = "Route unavailable" }, head, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    async Task StreamMedia(NetworkStream output, Request request, Connection connection, SharedMediaItem item, bool head, CancellationToken token)
    {
        FileStream input;
        try { input = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await Reply(output, 404, new { error = "Media unavailable" }, head, token).ConfigureAwait(false); return; }
        await using (input.ConfigureAwait(false))
        {
            connection.Item = item;
            if (!Volatile.Read(ref playlist).Items.TryGetValue(item.Id, out var live) || !SamePath(live.FilePath, item.FilePath) || !IsRunning) return;
            long length = input.Length, first = 0, last = length - 1; bool partial = request.Headers.TryGetValue("Range", out var range);
            if (partial && !TryRange(range!, length, out first, out last))
            { await WriteHeader(output, 416, 0, "application/octet-stream", "Content-Range: bytes */" + length + "\r\n", token).ConfigureAwait(false); return; }
            long count = length == 0 ? 0 : last - first + 1;
            string extra = "Accept-Ranges: bytes\r\n" + (partial ? $"Content-Range: bytes {first}-{last}/{length}\r\n" : "");
            await WriteHeader(output, partial ? 206 : 200, count, MimeType(item.FilePath), extra, token).ConfigureAwait(false);
            if (head || count == 0) return;
            Interlocked.Exchange(ref connection.Streaming, 1);
            try
            {
            input.Position = first; byte[] buffer = new byte[65536];
            while (count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (!Volatile.Read(ref playlist).Items.TryGetValue(item.Id, out live) || !SamePath(live.FilePath, item.FilePath)) return;
                int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), token).ConfigureAwait(false); if (read == 0) return;
                await Write(output, buffer.AsMemory(0, read), token, mediaChunk: true).ConfigureAwait(false); count -= read;
            }
            }
            finally { Interlocked.Exchange(ref connection.Streaming, 0); }
        }
    }
    bool PairAttemptAllowed(string address)
    {
        lock (authentication)
        {
            DateTimeOffset now = time.GetUtcNow(), boundary = now.AddMinutes(-1);
            while (globalAttempts.TryPeek(out var first) && first <= boundary) globalAttempts.Dequeue();
            foreach (var entry in attempts) while (entry.Value.TryPeek(out var first) && first <= boundary) entry.Value.Dequeue();
            foreach (string old in attempts.Where(p => p.Value.Count == 0).Select(p => p.Key).ToArray()) attempts.Remove(old);
            if (!attempts.TryGetValue(address, out var queue)) { if (attempts.Count >= 64) return false; attempts[address] = queue = new(); }
            if (queue.Count >= 5 || globalAttempts.Count >= 50) return false;
            queue.Enqueue(now); globalAttempts.Enqueue(now); return true;
        }
    }
    string? IssueToken(string? code)
    {
        lock (authentication)
        {
            if (!IsRunning || time.GetUtcNow() >= PairingExpiresAt || tokenHashes.Count >= 16 || code?.Length != 6
                || !code.All(c => c is >= '0' and <= '9') || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(PairingCode))) return null;
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            tokenHashes.Add(SHA256.HashData(Encoding.ASCII.GetBytes(token))); return token;
        }
    }
    bool Authorized(string? token)
    {
        if (token?.Length != 43 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(token)); bool valid = false;
        lock (authentication) { foreach (var stored in tokenHashes) valid |= CryptographicOperations.FixedTimeEquals(hash, stored); return IsRunning && valid; }
    }
    static string? Bearer(Request request) => request.Headers.TryGetValue("Authorization", out var value) && value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..] : null;
    static string? QueryToken(string query) => query.StartsWith("token=", StringComparison.Ordinal) && !query.Contains('&') && !query.Contains('%') ? query[6..] : null;
    public static bool TryRange(string value, long length, out long first, out long last)
    {
        first = 0; last = length - 1;
        if (length <= 0 || !value.StartsWith("bytes=", StringComparison.Ordinal) || value.Contains(',')) return false;
        string[] parts = value[6..].Split('-'); if (parts.Length != 2) return false;
        bool Number(string s, out long n) { n = 0; return s.Length > 0 && s.All(c => c is >= '0' and <= '9') && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n); }
        if (parts[0].Length == 0) { if (!Number(parts[1], out long suffix) || suffix == 0) return false; first = Math.Max(0, length - suffix); return true; }
        if (!Number(parts[0], out first) || first >= length) return false;
        if (parts[1].Length > 0) { if (!Number(parts[1], out long requestedLast) || requestedLast < first) return false; last = Math.Min(last, requestedLast); }
        return true;
    }
    static async Task<Request> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] bytes = new byte[MaximumHeaderBytes + MaximumBodyBytes]; int count = 0, end = -1;
        while (end < 0)
        {
            if (count >= MaximumHeaderBytes) throw new BadRequest(431);
            int read = await stream.ReadAsync(bytes.AsMemory(count, Math.Min(4096, MaximumHeaderBytes - count)), token).ConfigureAwait(false); if (read == 0) throw new BadRequest(400); count += read;
            for (int i = Math.Max(0, count - read - 3); i + 3 < count; i++) if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) { end = i + 4; break; }
        }
        if (bytes.AsSpan(0, end).ContainsAnyExceptInRange((byte)0x09, (byte)0x7e)) throw new BadRequest(400);
        string[] lines = Encoding.ASCII.GetString(bytes, 0, end - 4).Split("\r\n"); string[] first = lines[0].Split(' ');
        if (lines.Any(line => line.Any(c => char.IsControl(c) && c != '\t'))) throw new BadRequest(400);
        if (first.Length != 3 || first[2] != "HTTP/1.1" || first[0] is not ("GET" or "HEAD" or "POST") || first[1].Length > 2048 || !first[1].StartsWith('/') || first[1].StartsWith("//") || first[1].Contains('\\')) throw new BadRequest(400);
        string target = first[1]; int queryAt = target.IndexOf('?'); string path = queryAt < 0 ? target : target[..queryAt], query = queryAt < 0 ? "" : target[(queryAt + 1)..];
        if (path.Contains('%') || path.Contains('#') || path.Contains("..", StringComparison.Ordinal)) throw new BadRequest(400);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lines.Length > 65) throw new BadRequest(431);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':'); if (colon is < 1 or > 64 || !line[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new BadRequest(400);
        }
        if (!headers.ContainsKey("Host") || headers.ContainsKey("Transfer-Encoding")) throw new BadRequest(400);
        int bodyLength = 0;
        if (headers.TryGetValue("Content-Length", out string? length) && (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out bodyLength) || bodyLength < 0 || bodyLength > MaximumBodyBytes)) throw new BadRequest(413);
        if (first[0] != "POST" && bodyLength != 0 || count - end > bodyLength) throw new BadRequest(400);
        while (count < end + bodyLength)
        { int read = await stream.ReadAsync(bytes.AsMemory(count, end + bodyLength - count), token).ConfigureAwait(false); if (read == 0) throw new BadRequest(400); count += read; }
        return new(first[0], path, query, headers, bytes.AsMemory(end, bodyLength).ToArray());
    }
    static async Task Reply(NetworkStream stream, int status, object body, bool head, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > 4 * 1024 * 1024) { status = 413; bytes = "{\"error\":\"Response limit exceeded\"}"u8.ToArray(); }
        await WriteHeader(stream, status, bytes.Length, "application/json; charset=utf-8", "", token).ConfigureAwait(false);
        if (!head) await Write(stream, bytes, token).ConfigureAwait(false);
    }
    static Task WriteHeader(NetworkStream stream, int status, long length, string type, string extra, CancellationToken token)
    {
        string reason = status switch { 200 => "OK", 206 => "Partial Content", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", 413 => "Content Too Large", 416 => "Range Not Satisfiable", 429 => "Too Many Requests", 431 => "Request Header Fields Too Large", _ => "Service Unavailable" };
        string header = $"HTTP/1.1 {status} {reason}\r\nContent-Length: {length}\r\nContent-Type: {type}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" + extra + "\r\n";
        return Write(stream, Encoding.ASCII.GetBytes(header), token);
    }
    static async Task Write(NetworkStream stream, ReadOnlyMemory<byte> data, CancellationToken token, bool mediaChunk = false)
    { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(mediaChunk ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30)); await stream.WriteAsync(data, timeout.Token).ConfigureAwait(false); }
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4(); if (IPAddress.IsLoopback(address)) return true;
        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
            : address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xfe) == 0xfc);
    }
    public static IReadOnlyList<IPAddress> LocalAddresses() => SharingNetworkInterfaces.LocalAddresses();
    public static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".mp4" or ".m4v" => "video/mp4", ".m4a" => "audio/mp4", ".mkv" => "video/x-matroska", ".mka" => "audio/x-matroska", ".webm" => "video/webm", ".mov" => "video/quicktime", ".avi" => "video/x-msvideo", ".mp3" => "audio/mpeg", ".flac" => "audio/flac", ".wav" => "audio/wav", ".aac" => "audio/aac", ".ogg" or ".opus" => "audio/ogg", ".ts" or ".mts" => "video/mp2t", _ => "application/octet-stream" };
    static bool OpaqueId(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    static string CleanText(string value, int maximum)
    {
        var text = new StringBuilder(Math.Min(value.Length, maximum));
        for (int i = 0; i < value.Length && text.Length < maximum; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) continue;
                if (text.Length + 2 > maximum) break;
                text.Append(c); text.Append(value[++i]);
            }
            else if (!char.IsLowSurrogate(c) && !char.IsControl(c)) text.Append(c);
        }
        return text.ToString();
    }
    static double SafeTime(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 31_536_000) : 0;
    static bool SamePath(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    sealed record PlaylistSnapshot(Dictionary<string, SharedMediaItem> Items, int TotalItems, bool Truncated);
    sealed class Connection(TcpClient client, string address) { public TcpClient Client { get; } = client; public string Address { get; } = address; public SharedMediaItem? Item; public int Streaming; }
    sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Body);
    sealed class BadRequest(int status) : Exception { public int Status { get; } = status; }
}
