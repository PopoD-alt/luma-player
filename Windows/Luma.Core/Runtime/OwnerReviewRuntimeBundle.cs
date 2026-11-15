using System.Text.Json;

namespace Luma.Core.Runtime;

/// <summary>Copies an already centrally approved component into a new app-local folder.
/// The caller must run the registry-backed distribution verifier before selecting its pins.</summary>
public static class OwnerReviewRuntimeBundle
{
    public static ApprovedLibVlcRuntime CopyApproved(string source, string destination, LibVlcRuntimeApproval approval, CancellationToken cancellationToken = default)
    {
        string input = Path.GetFullPath(source), output = Path.GetFullPath(destination);
        static bool Contains(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Contains(input, output) || Contains(output, input)) throw new IOException("The source component and app-local destination must not overlap.");
        if (Directory.Exists(output) || File.Exists(output) || new DirectoryInfo(output).LinkTarget is not null)
            throw new IOException("The app-local runtime destination must be new.");
        LibVlcRuntimeManifest.ValidateApproved(input, approval, cancellationToken);
        string parent = Path.GetDirectoryName(output) ?? throw new IOException("A runtime destination parent is required.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The app publish folder must already exist.");
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("The app publish folder cannot be a link or junction.");
        string pending = Path.Combine(parent, ".luma-runtime-" + Guid.NewGuid().ToString("N"));
        byte[] manifest = File.ReadAllBytes(Path.Combine(input, LibVlcRuntimeManifest.FileName));
        // Validate this exact manifest before using it to select any copy paths.
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Security.Cryptography.SHA256.HashData(manifest), Convert.FromHexString(approval.ManifestSha256)))
            throw new InvalidDataException("The approved manifest changed before staging.");
        using var json = JsonDocument.Parse(manifest);
        try
        {
            Directory.CreateDirectory(pending);
            byte[] buffer = new byte[65536];
            foreach (var entry in json.RootElement.GetProperty("files").EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = entry.Name.Replace('/', Path.DirectorySeparatorChar);
                string sourceFile = Path.Combine(input, relative), target = Path.Combine(pending, relative);
                long expected = entry.Value.GetProperty("bytes").GetInt64();
                if ((File.GetAttributes(sourceFile) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The component changed into a link during staging.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var reader = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (reader.Length != expected) throw new InvalidDataException("A component file changed before staging: " + entry.Name);
                using var writer = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long remaining = expected;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) throw new EndOfStreamException("A component file was truncated during staging: " + entry.Name);
                    writer.Write(buffer, 0, count); remaining -= count;
                }
                if (reader.ReadByte() != -1) throw new InvalidDataException("A component file grew during staging: " + entry.Name);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(Path.Combine(pending, LibVlcRuntimeManifest.FileName), manifest);
            var result = LibVlcRuntimeManifest.ValidateApproved(pending, approval, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(pending, output);
            return result;
        }
        finally
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, true);
        }
    }
}
