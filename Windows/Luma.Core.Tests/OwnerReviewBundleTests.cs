using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luma.Core.Runtime;

namespace Luma.Core.Tests;

public static class OwnerReviewBundleTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "Luma-owner-bundle-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "prepared"), publish = Path.Combine(root, "app"), target = Path.Combine(publish, "libvlc");
        Directory.CreateDirectory(source); Directory.CreateDirectory(publish);
        try
        {
            var files = new Dictionary<string, object>();
            foreach (string name in new[] { "libvlc.dll", "libvlccore.dll", "plugins/codec/original.dll", "Sources/CorrespondingSource.tar", "Notices/COPYING.LIB", "Audit/actual-build.json" })
            {
                byte[] bytes = Encoding.UTF8.GetBytes("Original owner review component fixture: " + name);
                string path = Path.Combine(source, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
                files[name] = new { bytes = bytes.Length, sha256 = Hash(bytes) };
            }
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, package = "windows-original-owner-test", redistributionApproved = true, developmentOnly = false, files });
            File.WriteAllBytes(Path.Combine(source, LibVlcRuntimeManifest.FileName), manifest);
            var pins = new LibVlcRuntimeApproval("windows-original-owner-test", Hash(manifest), Hash(File.ReadAllBytes(Path.Combine(source, "Sources/CorrespondingSource.tar"))));
            var result = OwnerReviewRuntimeBundle.CopyApproved(source, target, pins);
            Check(result.FileCount == files.Count, "The complete approved component is staged");
            foreach (string path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                Check(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(Path.Combine(target, Path.GetRelativePath(source, path)))), "Source, notices, audit and native bytes remain exact");
            Reject(() => OwnerReviewRuntimeBundle.CopyApproved(source, target, pins), "An existing app-local component is never overwritten");
            Check(File.ReadAllBytes(Path.Combine(target, LibVlcRuntimeManifest.FileName)).SequenceEqual(manifest), "Existing output is preserved after rejection");
            Reject(() => OwnerReviewRuntimeBundle.CopyApproved(source, Path.Combine(source, "nested"), pins), "Nested staging cannot copy itself");
            Reject(() => OwnerReviewRuntimeBundle.CopyApproved(source, root, pins), "An ancestor destination is rejected");
            foreach (var wrong in new[] { pins with { Package = "windows-different" }, pins with { ManifestSha256 = new string('0', 64) }, pins with { SourceBundleSha256 = new string('0', 64) } })
                Reject(() => OwnerReviewRuntimeBundle.CopyApproved(source, Path.Combine(publish, "rejected"), wrong), "All three build pins are bound to the copy");
            string corrupt = Path.Combine(source, "plugins/codec/original.dll"); byte[] original = File.ReadAllBytes(corrupt); byte[] changed = original.ToArray(); changed[0] ^= 1; File.WriteAllBytes(corrupt, changed);
            Reject(() => OwnerReviewRuntimeBundle.CopyApproved(source, Path.Combine(publish, "rejected"), pins), "A same-size changed plugin is rejected before staging");
            File.WriteAllBytes(corrupt, original);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { OwnerReviewRuntimeBundle.CopyApproved(source, Path.Combine(publish, "canceled"), pins, cancel.Token); throw new InvalidOperationException("Staging ignored cancellation"); }
                catch (OperationCanceledException) { }
            }
            Check(!Directory.Exists(Path.Combine(publish, "rejected")) && !Directory.Exists(Path.Combine(publish, "canceled"))
                && !Directory.EnumerateDirectories(publish, ".luma-runtime-*").Any(), "Rejected/canceled staging leaves no partial destination");
            string extra = Path.Combine(source, "plugins/extra.dll"); File.WriteAllText(extra, "Unreviewed module");
            Reject(() => OwnerReviewRuntimeBundle.CopyApproved(source, Path.Combine(publish, "rejected"), pins), "An extra plugin cannot enter the owner folder"); File.Delete(extra);
            string replacement = Path.Combine(root, "original-link-target.dll"); File.WriteAllBytes(replacement, original); File.Delete(corrupt); bool linked = false;
            try { File.CreateSymbolicLink(corrupt, replacement); linked = true; }
            catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            { Console.WriteLine("INFO: this host cannot create a synthetic file symlink; its symlink rejection is not exercised here"); }
            try
            {
                if (linked) Reject(() => OwnerReviewRuntimeBundle.CopyApproved(source, Path.Combine(publish, "rejected"), pins), "Even a same-byte symlink cannot enter the owner folder");
            }
            finally { if (File.Exists(corrupt)) File.Delete(corrupt); File.WriteAllBytes(corrupt, original); }
            Console.WriteLine("PASS: owner-review runtime staging preserves the complete pinned source/notices/native tree; changed/mismatched/extra components, overlaps, overwrite and cancellation fail closed without partial output");
        }
        finally { Directory.Delete(root, true); }
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Owner bundle: " + message); }
    static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { return; }
        throw new InvalidOperationException("Owner bundle: " + message);
    }
}
