using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Luma.Core.Library;

/// <summary>A bounded, explicit segment, never a guessed fixed opening length.</summary>
public sealed record PlaybackSkipRange(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("startSeconds")] double StartSeconds,
    [property: JsonPropertyName("endSeconds")] double EndSeconds,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("label")] string? Label = null);
public sealed record PlaybackChapter(double StartSeconds, double DurationSeconds, string Name);

public static class PlaybackSkipPolicy
{
    public const double MaxTime = 31_536_000;
    public const int MaxRanges = 8;
    static readonly Regex IntroLabel = new(@"^(?:intro|opening|op)(?:\s+(?:credits|song|theme|sequence))?(?:\s*#?\d{1,3})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    static readonly Regex CreditsLabel = new(@"^(?:credits|outro|ending|end credits|closing credits|ed)(?:\s+(?:song|theme|sequence))?(?:\s*#?\d{1,3})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public static bool ValidTime(double value) => double.IsFinite(value) && value is >= 0 and <= MaxTime;
    public static string? KindForChapter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160) return null;
        string label = Regex.Replace(name.Trim().ToLowerInvariant(), @"[\s_\-–—:]+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (label == "オープニング" || IntroLabel.IsMatch(label)) return "intro";
        if (label == "エンディング" || CreditsLabel.IsMatch(label)) return "credits";
        return null;
    }
    public static IReadOnlyList<PlaybackSkipRange> ValidatedRanges(IEnumerable<PlaybackSkipRange>? input, double duration = 0)
    {
        if (input is null) return [];
        double bound = ValidTime(duration) && duration > 0 ? duration : MaxTime;
        var result = new List<PlaybackSkipRange>();
        foreach (var range in input.Take(MaxRanges))
        {
            if (range is null || range.Kind is not ("intro" or "credits") || range.Source is not ("manual" or "chapter")
                || !ValidTime(range.StartSeconds) || !ValidTime(range.EndSeconds) || range.StartSeconds >= range.EndSeconds || range.EndSeconds > bound || range.StartSeconds >= bound) continue;
            double end = range.EndSeconds;
            if (end - range.StartSeconds < .25) continue;
            string? label = range.Label?.Trim(); if (label?.Length > 64) label = label[..(char.IsHighSurrogate(label[63]) ? 63 : 64)];
            var safe = range with { EndSeconds = end, Label = label };
            if (!result.Any(r => r.Kind == safe.Kind && Math.Abs(r.StartSeconds - safe.StartSeconds) < .001 && Math.Abs(r.EndSeconds - safe.EndSeconds) < .001)) result.Add(safe);
        }
        return result.Where(r => !result.Any(other => !ReferenceEquals(r, other) && r.StartSeconds < other.EndSeconds && other.StartSeconds < r.EndSeconds))
            .OrderBy(r => r.StartSeconds).ThenBy(r => r.Kind, StringComparer.Ordinal).ToArray();
    }
    public static IReadOnlyList<PlaybackSkipRange> FromChapters(IEnumerable<PlaybackChapter> input, double duration)
    {
        if (!ValidTime(duration) || duration <= 0) return [];
        var chapters = input.Take(1024).Where(c => c is not null && ValidTime(c.StartSeconds) && c.StartSeconds < duration)
            .OrderBy(c => c.StartSeconds).ToArray();
        var ranges = new List<PlaybackSkipRange>();
        for (int i = 0; i < chapters.Length; i++)
        {
            var chapter = chapters[i]; string? kind = KindForChapter(chapter.Name); if (kind is null) continue;
            double end = i + 1 < chapters.Length ? chapters[i + 1].StartSeconds : duration;
            if (ValidTime(chapter.DurationSeconds) && chapter.DurationSeconds > 0) end = Math.Min(end, chapter.StartSeconds + chapter.DurationSeconds);
            double length = end - chapter.StartSeconds;
            if (kind == "intro" && length > Math.Min(600, duration / 2) || kind == "credits" && (length > 1800 || chapter.StartSeconds < duration / 2)) continue;
            if (length >= .25) ranges.Add(new(kind, chapter.StartSeconds, end, "chapter", chapter.Name));
        }
        return ValidatedRanges(ranges, duration);
    }
    /// Manual ranges replace chapter-derived ranges of the same kind.
    public static IReadOnlyList<PlaybackSkipRange> Merge(IEnumerable<PlaybackSkipRange>? saved, IEnumerable<PlaybackSkipRange>? chapters, double duration)
    {
        var manual = ValidatedRanges(saved, duration).Where(r => r.Source == "manual").ToArray();
        return ValidatedRanges(manual.Concat(ValidatedRanges(chapters, duration).Where(c => !manual.Any(m => m.Kind == c.Kind || m.StartSeconds < c.EndSeconds && c.StartSeconds < m.EndSeconds))), duration);
    }
    public static PlaybackSkipRange? Active(IEnumerable<PlaybackSkipRange> ranges, double position)
        => ValidTime(position) ? ranges.FirstOrDefault(r => position >= r.StartSeconds && position < r.EndSeconds - .1) : null;
}

/// <summary>Once per segment and playback. Seeking back leaves the manual button usable.</summary>
public sealed class PlaybackSkipSession
{
    readonly HashSet<string> consumed = new(StringComparer.Ordinal);
    readonly List<double> userSeekTargets = [];
    public int AutomaticSkips { get; private set; }
    static string Key(PlaybackSkipRange range) => FormattableString.Invariant($"{range.Kind}:{range.StartSeconds:R}:{range.EndSeconds:R}");
    public void Reset() { consumed.Clear(); userSeekTargets.Clear(); AutomaticSkips = 0; }
    public void Consume(PlaybackSkipRange range) => consumed.Add(Key(range));
    public void UserSeek(double position)
    {
        if (!PlaybackSkipPolicy.ValidTime(position)) return;
        userSeekTargets.Add(position); if (userSeekTargets.Count > 32) userSeekTargets.RemoveAt(0);
    }
    public PlaybackSkipRange? Automatic(IReadOnlyList<PlaybackSkipRange> ranges, double position, bool enabled, bool playing, bool seekable)
    {
        if (!enabled || !playing || !seekable) return null;
        var active = PlaybackSkipPolicy.Active(ranges, position);
        if (active is null) return null;
        if (userSeekTargets.Any(target => target >= active.StartSeconds && target < active.EndSeconds)) { Consume(active); return null; }
        if (!consumed.Add(Key(active))) return null;
        AutomaticSkips++; return active;
    }
}
