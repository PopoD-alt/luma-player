using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Luma.Core.Updates;

public sealed record WindowsUpdateConfig(string FeedURL, string PublicKey)
{
    public static WindowsUpdateConfig? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) return null;
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("feedURL", out var feed) || feed.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("publicKey", out var key) || key.ValueKind != JsonValueKind.String) return null;
            var url = feed.GetString()!; var publicKey = key.GetString()!;
            if (!SafeHttps(url)) return null;
            using var verifier = CreateVerifier(publicKey);
            return new(url, publicKey);
        }
        catch (Exception error) when (error is JsonException or CryptographicException or FormatException or ArgumentException) { return null; }
    }
    internal static bool SafeHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    // KeySize alone also permits other 256-bit curves. Require a complete SPKI,
    // the named NIST P-256 curve OID and a correctly sized exported public point.
    internal static ECDsa CreateVerifier(string publicKey)
    {
        if (publicKey is null || publicKey.Length is < 32 or > 2048) throw new CryptographicException("Invalid Windows publisher key.");
        var verifier = ECDsa.Create();
        try
        {
            byte[] bytes = Convert.FromBase64String(publicKey);
            verifier.ImportSubjectPublicKeyInfo(bytes, out var used);
            var point = verifier.ExportParameters(false);
            if (used != bytes.Length || verifier.KeySize != 256 || !point.Curve.IsNamed
                || point.Curve.Oid.Value != "1.2.840.10045.3.1.7" || point.Q.X?.Length != 32 || point.Q.Y?.Length != 32)
                throw new CryptographicException("Windows publisher keys must use NIST P-256.");
            return verifier;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        {
            verifier.Dispose(); throw new CryptographicException("Invalid NIST P-256 publisher key.", error);
        }
        catch { verifier.Dispose(); throw; }
    }
}

public sealed record WindowsRelease(string Version, int Build, string Url, string Sha256, long Size, string Notes)
{
    public static WindowsRelease Verify(byte[] envelope, string publicKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length > 262144) throw new InvalidDataException("Update manifest exceeds the supported limit.");
        try
        {
            using var outer = JsonDocument.Parse(envelope);
            var payload = Convert.FromBase64String(RequiredString(outer.RootElement, "payload"));
            var signature = Convert.FromBase64String(RequiredString(outer.RootElement, "signature"));
            if (signature.Length != 64 || payload.Length > 196608) throw new InvalidDataException("Invalid update signature or payload size.");
            using var verifier = WindowsUpdateConfig.CreateVerifier(publicKey);
            if (!verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException("The publisher signature is invalid.");
            using var parsed = JsonDocument.Parse(payload); var json = parsed.RootElement;
            if (!json.TryGetProperty("build", out var build) || !build.TryGetInt32(out int buildValue)
                || !json.TryGetProperty("size", out var size) || !size.TryGetInt64(out long sizeValue)) throw new InvalidDataException("Invalid release numbers.");
            var result = new WindowsRelease(RequiredString(json,"version"), buildValue, RequiredString(json,"url"),
                RequiredString(json,"sha256"), sizeValue, RequiredString(json,"notes"));
            result.Validate(); return result;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new InvalidDataException("Malformed Windows update manifest.", error); }
    }
    static string RequiredString(JsonElement json, string name)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing or invalid update field: " + name);
        return value.GetString()!;
    }
    internal void Validate()
    {
        if (Build <= 0 || string.IsNullOrWhiteSpace(Version) || Version.Length > 80 || !WindowsUpdateConfig.SafeHttps(Url)
            || Size is <= 0 or > 1073741824 || Notes is null || Notes.Length > 16384 || Sha256 is null || Sha256.Length != 64
            || Sha256.Any(c => !"0123456789abcdef".Contains(c))) throw new InvalidDataException("Invalid Windows release.");
        var suffix = Path.GetExtension(new Uri(Url).AbsolutePath).ToLowerInvariant();
        if (suffix is not (".zip" or ".exe")) throw new InvalidDataException("Unsupported Windows update type.");
    }
    public async Task VerifyDownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Validate();
        if (new FileInfo(path).Length != Size) throw new InvalidDataException("Update length does not match.");
        await using var file = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(file, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(Sha256))) throw new InvalidDataException("Update checksum does not match.");
    }
}

public static class UpdatePolicy
{
    public static bool Due(DateTimeOffset now, bool enabled, DateTimeOffset? last, DateTimeOffset? deferUntil) => enabled && !(deferUntil > now) && (last is null || now < last || now - last >= TimeSpan.FromDays(1));
    public static bool Offer(int build, int current, bool manual, bool enabled, bool idle, DateTimeOffset now, DateTimeOffset? deferUntil) => build > 0 && build > current && (manual || (enabled && idle && !(deferUntil > now)));
}

public sealed class WindowsUpdateClient : IDisposable
{
    readonly HttpClient client;
    public WindowsUpdateClient() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    public WindowsUpdateClient(HttpMessageHandler handler) { client = new(handler) { Timeout = TimeSpan.FromMinutes(5) }; }
    public async Task<WindowsRelease> CheckAsync(WindowsUpdateConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config); cancellationToken.ThrowIfCancellationRequested();
        using (var verifier = WindowsUpdateConfig.CreateVerifier(config.PublicKey)) { }
        using var response = await GetAsync(config.FeedURL, cancellationToken);
        if (response.Content.Headers.ContentLength > 262144) throw new InvalidDataException("Manifest too large.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(); var block = new byte[8192]; int count;
        while ((count = await source.ReadAsync(block, cancellationToken)) > 0)
        {
            if (output.Length + count > 262144) throw new InvalidDataException("Manifest too large.");
            output.Write(block, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return WindowsRelease.Verify(output.ToArray(), config.PublicKey);
    }
    public async Task DownloadAsync(WindowsRelease release, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release); cancellationToken.ThrowIfCancellationRequested(); release.Validate();
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Update destination already exists; it was left unchanged.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await GetAsync(release.Url, cancellationToken);
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("Unexpected update length.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var block = new byte[65536]; long total = 0; int count;
                while ((count = await input.ReadAsync(block, cancellationToken)) > 0)
                {
                    total += count; if (total > release.Size) throw new InvalidDataException("Update exceeds its declared length.");
                    await output.WriteAsync(block.AsMemory(0, count), cancellationToken);
                }
                if (total != release.Size) throw new InvalidDataException("Truncated update.");
            }
            await release.VerifyDownloadAsync(temporary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsUpdateConfig.SafeHttps(url)) throw new InvalidDataException("Updates require credential-free HTTPS.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new() { NoCache = true };
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                if (response.RequestMessage?.RequestUri is Uri final && !WindowsUpdateConfig.SafeHttps(final.AbsoluteUri))
                { response.Dispose(); throw new InvalidDataException("Update response downgraded HTTPS."); }
                return response;
            }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = response.Headers.Location; response.Dispose();
                if (next is null) throw new InvalidDataException("Update redirect has no target.");
                try { url = new Uri(new Uri(url), next).AbsoluteUri; }
                catch (UriFormatException error) { throw new InvalidDataException("Invalid update redirect.", error); }
                continue;
            }
            response.Dispose(); throw new HttpRequestException("Update server did not return a downloadable file.");
        }
        throw new InvalidDataException("Too many update redirects.");
    }
    public void Dispose() => client.Dispose();
}
