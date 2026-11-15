using System.Text.Json;
using Luma.Core.Sharing;

namespace Luma.Core.Tests;

internal static class SharingTestHost
{
    public static async Task<int> RunAsync(string fixtures, string readyFile)
    {
        fixtures = Path.GetFullPath(fixtures); readyFile = Path.GetFullPath(readyFile);
        string demo = Path.Combine(fixtures, "Luma-Demo.mp4"), hevc = Path.Combine(fixtures, "Luma-HEVC10-AAC51.mp4");
        if (!File.Exists(demo) || !File.Exists(hevc)) throw new FileNotFoundException("Generated test media is missing.");
        string demoId = Guid.NewGuid().ToString("N"), hevcId = Guid.NewGuid().ToString("N");
        await using var server = new PlaylistSharingServer("Luma generated test playlist");
        server.SetPlaylist([new(demoId, demo, "Luma Demo", 12, 1), new(hevcId, hevc, "Luma HEVC10 AAC 5.1", 2, .25)]);
        server.Start(); Directory.CreateDirectory(Path.GetDirectoryName(readyFile)!);
        string temporary = readyFile + ".pending";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { port = server.Port, code = server.PairingCode, ids = new { demo = demoId, hevc10 = hevcId } }));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, readyFile, true);
            Console.WriteLine("Sharing test host ready; generated fixtures only.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { while (await Console.In.ReadLineAsync(timeout.Token) is not null) { } }
            catch (OperationCanceledException) { }
            return 0;
        }
        finally { server.Stop(); if (File.Exists(temporary)) File.Delete(temporary); if (File.Exists(readyFile)) File.Delete(readyFile); }
    }
}
