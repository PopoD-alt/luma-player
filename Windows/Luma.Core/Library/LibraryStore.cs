using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luma.Core.Library;

public sealed record MediaBookmark(double Time, string Label);
public sealed class MediaEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = "";
    public string Title => Path.GetFileName(FilePath);
    public string Detail => Duration > 0 ? $"{Clock(ResumeTime)} / {Clock(Duration)}" : Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
    public double Duration { get; set; }
    public double ResumeTime { get; set; }
    public List<MediaBookmark> Bookmarks { get; set; } = [];
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
    [JsonIgnore]
    public bool AutoChecks { get => AutomaticallyCheckUpdates; set => AutomaticallyCheckUpdates = value; }
    public DateTimeOffset? LastUpdateAttempt { get; set; }
    public DateTimeOffset? DeferUpdatesUntil { get; set; }
}
public sealed class LibraryStore
{
    const int MaxItems = 2000;
    readonly string file;
    public LibraryDocument Data { get; private set; } = new();
    public string? RecoveryNotice { get; private set; }
    public LibraryStore(string filePath)
    {
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
    public void UpdateProgress(Guid id, double time, double duration)
    {
        var item = Data.Items.Find(x => x.Id == id); if (item is null || !double.IsFinite(time) || !double.IsFinite(duration) || duration <= 0) return;
        item.Duration = ValidTime(duration); item.ResumeTime = Math.Clamp(time, 0, item.Duration);
        if (item.Duration - item.ResumeTime < 1) item.ResumeTime = 0;
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
}
