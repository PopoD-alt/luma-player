namespace Luma.Core.Library;

public enum PlaybackResumeDevice { Computer, Phone }

public sealed record PlaybackResumeCheckpoint(double PositionSeconds, PlaybackResumeDevice Device, double UpdatedAtUnixSeconds, bool Completed)
{
    public string DeviceName => Device == PlaybackResumeDevice.Phone ? "Phone" : "Computer";
}

/// <summary>Chooses a checkpoint when opening media; incoming progress never changes an active player.</summary>
public static class PlaybackResumePolicy
{
    const double MaximumTimestamp = 4_102_444_800;
    const double MaximumPosition = 31_536_000;

    public static PlaybackResumeCheckpoint Resolve(MediaEntry item)
    {
        ArgumentNullException.ThrowIfNull(item);
        double computer = item.ComputerProgressSeconds ?? item.ResumeTime;
        bool hasComputer = ValidPosition(computer) && ValidTimestamp(item.ComputerUpdatedAtUnixSeconds);
        bool hasPhone = item.MobileProgressSeconds is { } phone && ValidPosition(phone) && ValidTimestamp(item.MobileUpdatedAtUnixSeconds);
        if (hasPhone && (!hasComputer || item.MobileUpdatedAtUnixSeconds > item.ComputerUpdatedAtUnixSeconds))
            return Checkpoint(item.MobileProgressSeconds!.Value, PlaybackResumeDevice.Phone, item.MobileUpdatedAtUnixSeconds, item.MobileCompleted, item.Duration > 0 ? item.Duration : item.MobileDurationSeconds);
        if (hasComputer)
            return Checkpoint(computer, PlaybackResumeDevice.Computer, item.ComputerUpdatedAtUnixSeconds, item.ComputerCompleted, item.Duration);
        // Libraries written before device timestamps retain their original
        // local bookmark. A missing phone timestamp cannot replace it.
        return Checkpoint(ValidPosition(item.ResumeTime) ? item.ResumeTime : 0, PlaybackResumeDevice.Computer, 0, false, item.Duration);
    }

    public static MediaEntry? MostRecent(IEnumerable<MediaEntry> items, Guid? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        MediaEntry? first = null, selected = null, recent = null;
        double latest = 0;
        foreach (var item in items)
        {
            first ??= item;
            if (item.Id == selectedId) selected = item;
            double stamp = Resolve(item).UpdatedAtUnixSeconds;
            if (stamp > latest) { latest = stamp; recent = item; }
        }
        return recent ?? selected ?? first;
    }

    static PlaybackResumeCheckpoint Checkpoint(double position, PlaybackResumeDevice device, double stamp, bool completed, double duration)
        => new(completed ? 0 : ValidPosition(duration) && duration > 0 ? Math.Min(position, duration) : position, device, stamp, completed);
    static bool ValidPosition(double value) => double.IsFinite(value) && value is >= 0 and <= MaximumPosition;
    static bool ValidTimestamp(double value) => double.IsFinite(value) && value is > 0 and <= MaximumTimestamp;
}
