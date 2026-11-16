using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;

namespace Luma.Core.Library;

public sealed record MediaBookmark(double Time, string Label);
public sealed class MediaEntry : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = "";
    public string Title => Path.GetFileName(FilePath);
    public string Detail => Duration > 0 ? $"{Clock(ResumeTime)} / {Clock(Duration)}" : Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
    public double Duration { get; set; }
    public double ResumeTime { get; set; }
    public double? ComputerProgressSeconds { get; set; }
    public double ComputerUpdatedAtUnixSeconds { get; set; }
    public bool ComputerCompleted { get; set; }
    public double? MobileProgressSeconds { get; set; }
    public double MobileDurationSeconds { get; set; }
    public double MobileUpdatedAtUnixSeconds { get; set; }
    public bool MobileCompleted { get; set; }
    [JsonIgnore] public string ComputerProgressDetail => ProgressDetail("Computer", ComputerProgressSeconds ?? ResumeTime, Duration, ComputerCompleted);
    [JsonIgnore] public string PhoneProgressDetail => MobileProgressSeconds is { } value ? ProgressDetail("Phone", value, MobileDurationSeconds > 0 ? MobileDurationSeconds : Duration, MobileCompleted) : "Phone · Not played yet";
    [JsonIgnore] public double ComputerProgressFraction => Duration > 0 ? Math.Clamp((ComputerProgressSeconds ?? ResumeTime) / Duration, 0, 1) : 0;
    [JsonIgnore] public double PhoneProgressFraction => MobileProgressSeconds is { } position && (MobileDurationSeconds > 0 || Duration > 0) ? Math.Clamp(position / (MobileDurationSeconds > 0 ? MobileDurationSeconds : Duration), 0, 1) : 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void ProgressChanged()
    {
        foreach (string name in new[] { nameof(Detail), nameof(ComputerProgressDetail), nameof(PhoneProgressDetail), nameof(ComputerProgressFraction), nameof(PhoneProgressFraction), nameof(MobileProgressSeconds), nameof(ComputerProgressSeconds) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    static string ProgressDetail(string device, double position, double duration, bool completed)
        => duration > 0 ? $"{device} · {(completed ? "Finished · " : "")}{Math.Clamp(position / duration * 100, 0, 100):0}% · {Clock(position)} / {Clock(duration)}"
            : $"{device} · {(completed ? "Finished · " : "")}{Clock(position)}";
    public List<MediaBookmark> Bookmarks { get; set; } = [];
    public List<PlaybackSkipRange> SkipRanges { get; set; } = [];
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 86_400_000) : 0);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
    public override string ToString() => Title;
}
public sealed class LibraryDocument
{
    public List<MediaEntry> Items { get; set; } = [];
    public Guid? SelectedId { get; set; }
    public bool AutomaticallyCheckUpdates { get; set; } = true;
    public bool AutoSkipEnabled { get; set; }
    [JsonIgnore]
    public bool AutoChecks { get => AutomaticallyCheckUpdates; set => AutomaticallyCheckUpdates = value; }
    public DateTimeOffset? LastUpdateAttempt { get; set; }
    public DateTimeOffset? DeferUpdatesUntil { get; set; }
}
public sealed class LibraryStore
{
    const int MaxItems = 2000;
    readonly string file;
    readonly TimeProvider time;
    public LibraryDocument Data { get; private set; } = new();
    public string? RecoveryNotice { get; private set; }
    public LibraryStore(string filePath, TimeProvider? timeProvider = null)
    {
        time = timeProvider ?? TimeProvider.System;
        file = Path.GetFullPath(filePath);
        if (!File.Exists(file)) return;
        try
        {
            if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("Library is too large.");
            var saved = JsonSerializer.Deserialize<LibraryDocument>(File.ReadAllText(file)) ?? throw new InvalidDataException("Empty library.");
            if (saved.Items is null || saved.Items.Count > MaxItems) throw new InvalidDataException("Invalid queue.");
            var seen = new HashSet<Guid>();
            foreach (var item in saved.Items)
            {
                if (item is null || item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.FilePath) || !Path.IsPathFullyQualified(item.FilePath) || !seen.Add(item.Id)) throw new InvalidDataException("Invalid media entry.");
                item.FilePath = Path.GetFullPath(item.FilePath);
                item.Duration = ValidTime(item.Duration); item.ResumeTime = Math.Min(ValidTime(item.ResumeTime), item.Duration > 0 ? item.Duration : double.MaxValue);
                bool invalidComputer = item.ComputerProgressSeconds is { } rawComputer && !WireTime(rawComputer);
                item.ComputerProgressSeconds = item.ComputerProgressSeconds is { } computer && !invalidComputer ? Math.Min(computer, item.Duration > 0 ? item.Duration : double.MaxValue) : null;
                item.ComputerUpdatedAtUnixSeconds = invalidComputer ? 0 : ValidTimestamp(item.ComputerUpdatedAtUnixSeconds);
                item.MobileDurationSeconds = item.Duration > 0 ? item.Duration : ValidTime(item.MobileDurationSeconds);
                item.MobileProgressSeconds = item.MobileProgressSeconds is { } mobile && WireTime(mobile) ? Math.Min(mobile, item.MobileDurationSeconds > 0 ? item.MobileDurationSeconds : double.MaxValue) : null;
                item.MobileUpdatedAtUnixSeconds = item.MobileProgressSeconds is null ? 0 : ValidTimestamp(item.MobileUpdatedAtUnixSeconds);
                if (item.MobileProgressSeconds is null) item.MobileCompleted = false;
                item.SkipRanges = PlaybackSkipPolicy.ValidatedRanges(item.SkipRanges, item.Duration).Where(r => r.Source == "manual").ToList();
                item.Bookmarks = (item.Bookmarks ?? []).Where(b => b is not null && double.IsFinite(b.Time) && b.Time >= 0).Take(1000)
                    .Select(b => new MediaBookmark(Math.Min(ValidTime(b.Time), item.Duration > 0 ? item.Duration : double.MaxValue),
                        string.IsNullOrWhiteSpace(b.Label) ? MediaEntry.Clock(b.Time) : b.Label[..Math.Min(b.Label.Length, 256)])).ToList();
            }
            Data = saved;
            if (!Data.Items.Any(x => x.Id == Data.SelectedId)) Data.SelectedId = null;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException)
        {
            var backup = file + ".corrupt-" + Guid.NewGuid().ToString("N") + ".json";
            File.Move(file, backup);
            RecoveryNotice = "The previous library could not be read. A backup was preserved beside it.";
        }
    }
    public List<MediaEntry> Add(IEnumerable<string> paths)
    {
        var added = new List<MediaEntry>();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var input in paths)
        {
            if (Data.Items.Count >= MaxItems) break;
            var path = Path.GetFullPath(input);
            if (!File.Exists(path) || Data.Items.Any(x => string.Equals(x.FilePath, path, comparison))) continue;
            var item = new MediaEntry { FilePath = path }; Data.Items.Add(item); added.Add(item);
        }
        Flush(); return added;
    }
    public void UpdateProgress(Guid id, double time, double duration, bool completed = false)
    {
        var item = Data.Items.Find(x => x.Id == id); if (item is null || !double.IsFinite(time) || !double.IsFinite(duration) || duration <= 0) return;
        double length = ValidTime(duration), position = Math.Clamp(time, 0, length); bool finished = completed || length - position <= .05;
        bool changed = item.ComputerProgressSeconds is not { } old || Math.Abs(old - position) > .05 || item.ComputerCompleted != finished || item.Duration != length;
        item.Duration = length; item.ResumeTime = length - position < 1 ? 0 : position; item.ComputerProgressSeconds = position; item.ComputerCompleted = finished;
        if (item.MobileProgressSeconds is { } mobile)
        {
            item.MobileDurationSeconds = length; item.MobileProgressSeconds = Math.Min(mobile, length);
            item.MobileCompleted &= length - item.MobileProgressSeconds.Value <= 1;
        }
        if (changed) item.ComputerUpdatedAtUnixSeconds = ValidTimestamp(this.time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d);
        item.ProgressChanged();
    }
    public bool UpdateMobileProgress(Guid id, double position, double duration, bool completed, double updatedAtUnixSeconds)
    {
        var item = Data.Items.Find(x => x.Id == id);
        if (item is null || !WireTime(position) || !WireTime(duration) || !double.IsFinite(updatedAtUnixSeconds) || updatedAtUnixSeconds is < 0 or > 4_102_444_800) return false;
        double length = item.Duration > 0 ? item.Duration : duration;
        item.MobileProgressSeconds = length > 0 ? Math.Min(position, length) : position;
        item.MobileDurationSeconds = length; item.MobileCompleted = completed && (length <= 0 || length - item.MobileProgressSeconds.Value <= 1);
        item.MobileUpdatedAtUnixSeconds = updatedAtUnixSeconds; item.ProgressChanged(); return true;
    }
    public bool SetSkipRanges(Guid id, IEnumerable<PlaybackSkipRange> ranges)
    {
        var item = Data.Items.Find(x => x.Id == id); if (item is null) return false;
        item.SkipRanges = PlaybackSkipPolicy.ValidatedRanges(ranges, item.Duration).Where(r => r.Source == "manual").ToList();
        Flush(); return true;
    }
    public void Bookmark(Guid id, double time)
    {
        var item = Data.Items.Find(x => x.Id == id);
        if (item is null || !double.IsFinite(time) || time < 0 || item.Bookmarks.Count >= 1000) return;
        time = Math.Min(ValidTime(time), item.Duration > 0 ? item.Duration : double.MaxValue);
        if (item.Bookmarks.Any(b => Math.Abs(b.Time - time) < .25)) return;
        item.Bookmarks.Add(new(time, MediaEntry.Clock(time))); Flush();
    }
    public void Move(Guid id, int direction)
    {
        var index = Data.Items.FindIndex(x => x.Id == id); var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= Data.Items.Count) return;
        (Data.Items[index], Data.Items[target]) = (Data.Items[target], Data.Items[index]); Flush();
    }
    public void Remove(Guid id) { Data.Items.RemoveAll(x => x.Id == id); if (Data.SelectedId == id) Data.SelectedId = null; Flush(); }
    public void Clear() { Data.Items.Clear(); Data.SelectedId = null; Flush(); }
    public void Flush()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(Data)); File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static double ValidTime(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 86_400_000) : 0;
    static bool WireTime(double value) => double.IsFinite(value) && value is >= 0 and <= 31_536_000;
    static double ValidTimestamp(double value) => double.IsFinite(value) && value is >= 0 and <= 4_102_444_800 ? value : 0;
}
