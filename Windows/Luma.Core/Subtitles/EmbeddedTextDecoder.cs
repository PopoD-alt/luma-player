using System.Globalization;
using System.Net;
using System.Text;
using Luma.Core.Containers;

namespace Luma.Core.Subtitles;

public static class EmbeddedTextDecoder
{
    const int MaximumTrackCharacters = 4_000_000;
    static readonly HashSet<string> MarkupTags = new(StringComparer.OrdinalIgnoreCase)
        { "b", "i", "u", "s", "font", "span", "c", "v", "lang", "ruby", "rt", "p", "div", "br" };
    public static bool Supports(string codecId) => codecId is "S_TEXT/UTF8" or "S_TEXT/ASS" or "S_TEXT/SSA" or "S_TEXT/WEBVTT";

    public static List<SubtitleCue> Decode(DemuxedTrack track, long movieOriginNanoseconds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Supports(track.CodecId)) throw new NotSupportedException("Embedded subtitle format is not implemented: " + track.CodecId);
        if (track.Packets.Count > 100_000) throw new InvalidDataException("Embedded subtitle track exceeds 100,000 cues.");
        var cues = new List<SubtitleCue>(); int totalCharacters = 0;
        foreach (var packet in track.Packets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (packet.Data.Length > 1_048_576) throw new InvalidDataException("Embedded subtitle packet exceeds 1 MiB.");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(packet.Data).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n'); }
            catch (DecoderFallbackException e) { throw new InvalidDataException("Embedded subtitle packet is not valid UTF-8.", e); }
            if (track.CodecId is "S_TEXT/ASS" or "S_TEXT/SSA")
            {
                var fields = text.Split(',', 9);
                if (fields.Length != 9 || !long.TryParse(fields[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var order) || order < 0)
                    throw new InvalidDataException("ASS/SSA packet requires ReadOrder and eight following fields.");
                text = PlainAss(fields[8]);
            }
            text = WebUtility.HtmlDecode(StripKnownMarkup(text, track.CodecId == "S_TEXT/WEBVTT"));
            text = new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim();
            if (text.Length > 16384) throw new InvalidDataException("Embedded subtitle cue exceeds its text limit.");
            if (packet.DurationNanoseconds is < 0) throw new InvalidDataException("Embedded subtitle duration must not be negative.");
            if (packet.DurationNanoseconds is not long duration || duration == 0 || text.Length == 0) continue;
            double start = (double)((Int128)packet.TimestampNanoseconds - movieOriginNanoseconds) / 1_000_000_000;
            double end = start + duration / 1_000_000_000.0;
            if (!double.IsFinite(start) || !double.IsFinite(end)) throw new InvalidDataException("Embedded subtitle timestamp exceeds its bounds.");
            if (end > 0)
            {
                totalCharacters += text.Length;
                if (totalCharacters > MaximumTrackCharacters) throw new InvalidDataException("Embedded subtitle track exceeds four million displayed characters.");
                cues.Add(new(Math.Max(0, start), end, text));
            }
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    static string PlainAss(string value)
    {
        var output = new StringBuilder(); bool drawing = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char next = value[i + 1];
                string? replacement = next switch { 'N' or 'n' => "\n", 'h' => "\u00A0", '{' or '}' or '\\' => next.ToString(), _ => null };
                if (replacement is not null) { if (!drawing) output.Append(replacement); i++; continue; }
            }
            if (c == '{')
            {
                int end = i + 1;
                for (; end < value.Length; end++)
                {
                    if (value[end] == '\\' && end + 1 < value.Length && value[end + 1] is '\\' or '{' or '}') { end++; continue; }
                    if (value[end] == '}') break;
                }
                if (end == value.Length) throw new InvalidDataException("Unterminated ASS/SSA override block.");
                string block = value[(i + 1)..end];
                for (int p = 0; p + 2 < block.Length; p++)
                {
                    if (block[p] != '\\' || block[p + 1] != 'p') continue;
                    int digit = p + 2; while (digit < block.Length && char.IsWhiteSpace(block[digit])) digit++;
                    int first = digit; bool nonzero = false;
                    while (digit < block.Length && block[digit] is >= '0' and <= '9') { nonzero |= block[digit] != '0'; digit++; }
                    if (digit > first) { drawing = nonzero; p = digit - 1; }
                }
                i = end; continue;
            }
            if (!drawing) output.Append(c);
        }
        return output.ToString();
    }

    // TextBlock displays plain text: preserve literal comparisons and unknown <names>.
    // Strip only recognized formatting tags; WebVTT also allows inline timestamp tags.
    static string StripKnownMarkup(string value, bool webVtt)
    {
        var output = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '<') { output.Append(value[i]); continue; }
            int end = value.IndexOf('>', i + 1);
            if (end < 0) { output.Append(value.AsSpan(i)); break; }
            ReadOnlySpan<char> token = value.AsSpan(i + 1, end - i - 1).Trim();
            bool closing = token.Length > 0 && token[0] == '/';
            var nameToken = closing ? token[1..] : token;
            int length = 0;
            while (length < nameToken.Length && (nameToken[length] is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) length++;
            bool delimiter = length == nameToken.Length || length < nameToken.Length && (char.IsWhiteSpace(nameToken[length]) || nameToken[length] is '/' or '.');
            string name = nameToken[..length].ToString();
            bool known = delimiter && MarkupTags.Contains(name);
            bool timestamp = webVtt && IsTimestampTag(token);
            if (known && (name.Equals("br", StringComparison.OrdinalIgnoreCase) || closing && (name.Equals("p", StringComparison.OrdinalIgnoreCase) || name.Equals("div", StringComparison.OrdinalIgnoreCase)))) output.Append('\n');
            else if (!known && !timestamp) output.Append(value.AsSpan(i, end - i + 1));
            i = end;
        }
        return output.ToString();
    }

    static bool IsTimestampTag(ReadOnlySpan<char> token)
    {
        if (token.Length == 0 || token[0] is < '0' or > '9' || token.IndexOf(':') < 0) return false;
        foreach (char c in token) if (c is not (>= '0' and <= '9') and not ':' and not '.') return false;
        return true;
    }
}
