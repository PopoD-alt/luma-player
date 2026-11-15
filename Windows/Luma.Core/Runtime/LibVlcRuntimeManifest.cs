using System.Security.Cryptography;
using System.Text.Json;

namespace Luma.Core.Runtime;

public sealed record LibVlcRuntimeApproval(string Package, string ManifestSha256, string SourceBundleSha256);

public sealed record ApprovedLibVlcRuntime(string Package, int FileCount, string SourceBundleSha256);

/// <summary>Validates the central runtime manifest for an optional app-local deployment.</summary>
public static class LibVlcRuntimeManifest
{
    public const string FileName = "luma-libvlc-runtime.json";
    public static ApprovedLibVlcRuntime ValidateApproved(string folder, LibVlcRuntimeApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();
        bool Hash(string? value) => value?.Length == 64 && value.All(char.IsAsciiHexDigit);
        if (!Hash(approval.ManifestSha256) || !Hash(approval.SourceBundleSha256)) throw new InvalidDataException("This app build has no valid approved runtime pin.");
        string root = Path.GetFullPath(folder);
        CheckOrdinary(root, directory: true);
        string manifestPath = Path.Combine(root, FileName); CheckOrdinary(manifestPath, directory: false);
        if (new FileInfo(manifestPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("The runtime manifest exceeds its size limit.");
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(manifestBytes), Convert.FromHexString(approval.ManifestSha256)))
            throw new InvalidDataException("The bundled manifest does not match this app build's approved component.");
        using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 16 });
        var json = document.RootElement;
        if (json.ValueKind == JsonValueKind.Object && json.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != json.EnumerateObject().Count())
            throw new InvalidDataException("The runtime manifest contains ambiguous duplicate fields.");
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int value) || value != 1
            || !json.TryGetProperty("redistributionApproved", out var approved) || approved.ValueKind != JsonValueKind.True
            || !json.TryGetProperty("developmentOnly", out var development) || development.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("The bundled runtime is not approved for redistribution.");
        if (!json.TryGetProperty("package", out var packageValue) || packageValue.ValueKind != JsonValueKind.String
            || packageValue.GetString() is not string package || !package.StartsWith("windows-", StringComparison.Ordinal) || package.Length > 128 || package != approval.Package)
            throw new InvalidDataException("The manifest does not describe a Windows runtime package.");
        if (!json.TryGetProperty("files", out var entries) || entries.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The bundled runtime has no file inventory.");
        var expected = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in entries.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRelative(file.Name);
            if (expected.Count >= 10000 || file.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase) || file.Value.ValueKind != JsonValueKind.Object
                || !file.Value.TryGetProperty("bytes", out var bytesValue) || bytesValue.ValueKind != JsonValueKind.Number || !bytesValue.TryGetInt64(out long bytes) || bytes < 0 || bytes > 4L * 1024 * 1024 * 1024
                || !file.Value.TryGetProperty("sha256", out var hashValue) || hashValue.ValueKind != JsonValueKind.String
                || hashValue.GetString() is not string hash || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("The bundled runtime has an invalid file entry.");
            total = checked(total + bytes);
            if (total > 8L * 1024 * 1024 * 1024 || !expected.TryAdd(file.Name, new(bytes, Convert.FromHexString(hash))))
                throw new InvalidDataException("The bundled runtime inventory is duplicated or exceeds its limit.");
        }
        const string source = "Sources/CorrespondingSource.tar";
        if (!expected.ContainsKey("libvlc.dll") || !expected.ContainsKey("libvlccore.dll") || !expected.ContainsKey(source)
            || !expected.Keys.Any(n => n.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            || !expected.Keys.Any(n => n.StartsWith("Notices/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The bundled runtime is missing its libraries, plugins, source companion or notices.");
        if (!CryptographicOperations.FixedTimeEquals(expected[source].Hash, Convert.FromHexString(approval.SourceBundleSha256)))
            throw new InvalidDataException("The source companion does not match this app build's approved component.");
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var pending = new Stack<string>(); pending.Push(root); int examined = 0;
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++examined > 20000) throw new InvalidDataException("Runtime tree exceeds its inspection limit.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime inventory paths cannot be links or junctions.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Equals(FileName, StringComparison.Ordinal)) continue;
                if (!expected.TryGetValue(relative, out var entry) || !found.Add(relative)) throw new InvalidDataException("An extra or duplicated runtime file is present: " + relative);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length != entry.Bytes) throw new InvalidDataException("Runtime file length mismatch: " + relative);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[65536]; int read;
                while ((read = file.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); sha.AppendData(buffer, 0, read); }
                if (!CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), entry.Hash)) throw new InvalidDataException("Runtime file hash mismatch: " + relative);
            }
        }
        if (found.Count != expected.Count) throw new InvalidDataException("A file listed in the runtime manifest is missing.");
        return new(package, found.Count, Convert.ToHexString(expected[source].Hash).ToLowerInvariant());
    }
    public static string ResolvePath(string? requestedPath, string bundledFolder, IEnumerable<string> installedPaths, LibVlcRuntimeApproval? approval = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            if (!Path.IsPathFullyQualified(requestedPath) || !File.Exists(requestedPath))
                throw new DllNotFoundException("LUMA_LIBVLC_PATH must name an existing absolute path to a compatible libvlc.dll.");
            return Path.GetFullPath(requestedPath);
        }
        bool bundlePresent = Directory.Exists(bundledFolder) || File.Exists(bundledFolder) || new DirectoryInfo(bundledFolder).LinkTarget is not null;
        if (bundlePresent)
        {
            try
            {
                if (approval is null) throw new InvalidDataException("This app build has no approved bundled runtime configuration.");
                ValidateApproved(bundledFolder, approval); return Path.Combine(Path.GetFullPath(bundledFolder), "libvlc.dll");
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
            { throw new DllNotFoundException("The bundled playback component did not pass its approved central manifest: " + error.Message + " Restore the complete app folder or choose an explicit compatible runtime with LUMA_LIBVLC_PATH.", error); }
        }
        string? installed = installedPaths.FirstOrDefault(File.Exists);
        if (installed is null) throw new DllNotFoundException("Install official 64-bit VLC3 separately, or set LUMA_LIBVLC_PATH. This review build has no approved bundled playback component.");
        return Path.GetFullPath(installed);
    }
    static void CheckOrdinary(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new InvalidDataException("The runtime manifest and root must be ordinary files/directories.");
    }
    static void ValidateRelative(string path)
    {
        if (path.Length is 0 or > 1024 || path.Contains('\\') || path.Contains(':') || path.Contains('\0') || path.StartsWith('/'))
            throw new InvalidDataException("Unsafe runtime inventory path.");
        foreach (string part in path.Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c))
                || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')
                throw new InvalidDataException("Unsafe runtime inventory path component.");
        }
    }
    sealed record Entry(long Bytes, byte[] Hash);
}
