using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Luma.Core.Subtitles;
public sealed record SubtitleCue(double Start, double End, string Text);
public static class SubtitleParser
{
    public static List<SubtitleCue> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("Subtitle file exceeds the supported limit.");
        var normalized = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n'); var cues = new List<SubtitleCue>();
        for (var i = 0; i < lines.Length; i++)
        {
            var timing = lines[i].Split("-->", StringSplitOptions.TrimEntries);
            if (timing.Length != 2 || !Time(timing[0], out var start)) continue;
            var right = timing[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (right.Length == 0 || !Time(right[0], out var end) || end <= start) continue;
            var content = new List<string>();
            while (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1])) content.Add(lines[++i]);
            var value = string.Join('\n', content);
            if (value.Length > 16384) throw new InvalidDataException("Subtitle cue exceeds the supported limit.");
            try { value = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]*>", "", RegexOptions.None, TimeSpan.FromMilliseconds(100))); }
            catch (RegexMatchTimeoutException error) { throw new InvalidDataException("Subtitle markup exceeds the parsing limit.", error); }
            value = new string(value.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim();
            if (!string.IsNullOrWhiteSpace(value)) cues.Add(new(start, end, value));
            if (cues.Count > 100000) throw new InvalidDataException("Too many subtitle cues.");
        }
        if (cues.Count == 0 && !string.IsNullOrWhiteSpace(normalized)) throw new InvalidDataException("No supported SRT/WebVTT cues were found.");
        return cues.OrderBy(c => c.Start).ToList();
    }
    public static string At(IReadOnlyList<SubtitleCue> cues, double time) => string.Join('\n', cues.Where(c => c.Start <= time && c.End > time).Take(8).Select(c => c.Text));
    static bool Time(string text, out double time)
    {
        time = 0; text = text.Trim(); if (text.Length > 24) return false;
        var parts = text.Split(':'); if (parts.Length is not (2 or 3)) return false;
        static bool Digits(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');
        int hours = 0, minutesIndex = parts.Length - 2;
        if (parts.Length == 3 && (parts[0].Length > 6 || !Digits(parts[0]) || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))) return false;
        var minutesText = parts[minutesIndex];
        if (minutesText.Length != 2 || !Digits(minutesText) || !int.TryParse(minutesText, out var minutes) || minutes >= 60) return false;
        var secondParts = parts[^1].Replace(',', '.').Split('.');
        if (secondParts.Length is not (1 or 2) || secondParts[0].Length != 2 || !Digits(secondParts[0])
            || !int.TryParse(secondParts[0], out var seconds) || seconds >= 60) return false;
        double fraction = 0;
        if (secondParts.Length == 2)
        {
            var digits = secondParts[1];
            if (digits.Length is < 1 or > 3 || !Digits(digits)) return false;
            fraction = int.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, digits.Length);
        }
        time = hours * 3600.0 + minutes * 60 + seconds + fraction;
        return double.IsFinite(time) && time <= 86_400_000;
    }
}
