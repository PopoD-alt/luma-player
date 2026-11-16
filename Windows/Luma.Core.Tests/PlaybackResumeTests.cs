using System.Text.Json;
using Luma.Core.Library;

namespace Luma.Core.Tests;

internal static class PlaybackResumeTests
{
    public static void Run()
    {
        var item = new MediaEntry { FilePath = Path.GetFullPath("resume-fixture.mp4"), Duration = 100, ResumeTime = 17, ComputerProgressSeconds = 30, ComputerUpdatedAtUnixSeconds = 100, MobileProgressSeconds = 42, MobileUpdatedAtUnixSeconds = 110 };
        Check(PlaybackResumePolicy.Resolve(item) is { PositionSeconds: 42, Device: PlaybackResumeDevice.Phone }, "Newer phone checkpoint wins");
        item.ComputerUpdatedAtUnixSeconds = 120;
        Check(PlaybackResumePolicy.Resolve(item) is { PositionSeconds: 30, Device: PlaybackResumeDevice.Computer }, "Newer computer checkpoint wins");
        item.MobileUpdatedAtUnixSeconds = 120;
        Check(PlaybackResumePolicy.Resolve(item).Device == PlaybackResumeDevice.Computer, "Equal timestamps prefer the computer");
        item.MobileProgressSeconds = 0; item.MobileUpdatedAtUnixSeconds = 130;
        Check(PlaybackResumePolicy.Resolve(item) is { PositionSeconds: 0, Device: PlaybackResumeDevice.Phone, Completed: false }, "Known phone zero is a valid new checkpoint");
        item.MobileProgressSeconds = 100; item.MobileCompleted = true;
        Check(PlaybackResumePolicy.Resolve(item) is { PositionSeconds: 0, Device: PlaybackResumeDevice.Phone, Completed: true }, "Newest completed item starts again");
        item.ComputerUpdatedAtUnixSeconds = 140; item.ComputerProgressSeconds = 100; item.ComputerCompleted = true;
        Check(PlaybackResumePolicy.Resolve(item) is { PositionSeconds: 0, Device: PlaybackResumeDevice.Computer, Completed: true }, "Computer completion also restarts");
        item.ComputerUpdatedAtUnixSeconds = item.MobileUpdatedAtUnixSeconds = 0;
        Check(PlaybackResumePolicy.Resolve(item) is { PositionSeconds: 17, UpdatedAtUnixSeconds: 0, Completed: false }, "Missing timestamps preserve legacy bookmark even with stale completion fields");
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, -1, 4_102_444_801 })
        {
            item.MobileUpdatedAtUnixSeconds = bad; item.ComputerUpdatedAtUnixSeconds = 100; item.ComputerCompleted = false; item.ComputerProgressSeconds = 30;
            Check(PlaybackResumePolicy.Resolve(item).Device == PlaybackResumeDevice.Computer, "Invalid phone timestamps cannot win");
        }
        item.MobileUpdatedAtUnixSeconds = 200;
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, -1, 31_536_001 })
        {
            item.MobileProgressSeconds = bad;
            Check(PlaybackResumePolicy.Resolve(item).Device == PlaybackResumeDevice.Computer, "Invalid positions cannot win despite newer timestamps");
        }
        item.MobileProgressSeconds = 90; item.MobileCompleted = false; item.Duration = 20;
        Check(PlaybackResumePolicy.Resolve(item).PositionSeconds == 20, "Checkpoint respects the authoritative host duration");
        var older = new MediaEntry { FilePath = "old.mp4", ResumeTime = 9, ComputerProgressSeconds = 9, ComputerUpdatedAtUnixSeconds = 190 };
        var unplayed = new MediaEntry { FilePath = "new.mp4" };
        Check(PlaybackResumePolicy.MostRecent([unplayed, older, item], older.Id)?.Id == item.Id, "Startup chooses globally latest activity rather than old selection or queue order");
        item.MobileCompleted = true;
        Check(PlaybackResumePolicy.MostRecent([unplayed, older, item])?.Id == item.Id, "Completed recent activity remains visible at startup");
        item.MobileUpdatedAtUnixSeconds = item.ComputerUpdatedAtUnixSeconds = older.ComputerUpdatedAtUnixSeconds = 0;
        Check(PlaybackResumePolicy.MostRecent([unplayed, older, item], older.Id)?.Id == older.Id, "Legacy startup preserves selected item");
        Check(PlaybackResumePolicy.MostRecent([unplayed, older], Guid.NewGuid()) == unplayed && PlaybackResumePolicy.MostRecent([]) is null, "Missing selection and empty libraries have safe fallbacks");
        var restored = JsonSerializer.Deserialize<MediaEntry>(JsonSerializer.Serialize(item))!;
        Check(PlaybackResumePolicy.Resolve(restored).PositionSeconds == 17, "Resume choice survives persisted legacy data");
        string savedLibrary = Path.Combine(Path.GetTempPath(), "Luma-resume-" + Guid.NewGuid().ToString("N"), "library.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savedLibrary)!);
            var invalidSaved = new MediaEntry { FilePath = Path.GetFullPath("resume-fixture.mp4"), Duration = 100, ResumeTime = 17, ComputerProgressSeconds = -2, ComputerUpdatedAtUnixSeconds = 100, MobileProgressSeconds = 25, MobileUpdatedAtUnixSeconds = 4_102_444_801 };
            File.WriteAllText(savedLibrary, JsonSerializer.Serialize(new LibraryDocument { Items = [invalidSaved] }));
            var saved = new LibraryStore(savedLibrary).Data.Items.Single();
            Check(saved.ComputerProgressSeconds is null && saved.ComputerUpdatedAtUnixSeconds == 0 && saved.MobileUpdatedAtUnixSeconds == 0 && PlaybackResumePolicy.Resolve(saved).PositionSeconds == 17,
                "Disk sanitation rejects invalid checkpoints rather than converting them into recent zero/future activity");
        }
        finally { Directory.Delete(Path.GetDirectoryName(savedLibrary)!, true); }
        Console.WriteLine("PASS: newest-device resume, computer tie priority, valid zero, completed restart, legacy and invalid-data fallback, duration clamp and global recent selection");
    }
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Playback resume: " + message); }
}
