using System.Buffers.Binary;
using System.Text;
using Luma.Core.Subtitles;

namespace Luma.Core.Containers;

/// <summary>Security boundary: callers must not retry/fall back past an external media reference.</summary>
public sealed class Mp4ExternalDataReferenceException(string message) : NotSupportedException(message);

public sealed record Mp4VideoTrack(uint Id, string SampleEntry, string Codec, int Width, int Height, int? BitDepth, int? Profile)
{ public int SampleDescriptionIndex { get; init; } = 1; }
public sealed record Mp4AudioTrack(uint Id, string SampleEntry, string Codec, int Channels, int SampleRate, int? AacObjectType)
{
    public int SampleEntryChannels { get; init; }
    public int? AacChannels { get; init; }
    public int SampleDescriptionIndex { get; init; } = 1;
}
public sealed record Mp4SubtitleTrack(uint Id, string SampleEntry, string Language, IReadOnlyList<SubtitleCue> Cues);
public sealed record Mp4Inspection(IReadOnlyList<Mp4VideoTrack> VideoTracks, IReadOnlyList<Mp4AudioTrack> AudioTracks,
    IReadOnlyList<Mp4SubtitleTrack> SubtitleTracks)
{
    public IReadOnlyList<string> SubtitleWarnings { get; init; } = [];
    public string? VideoCodec => VideoTracks.Count == 0 ? null : string.Join(" / ", VideoTracks.Select(t => t.Codec).Distinct());
    public int? BitDepth => VideoTracks.Where(t => t.BitDepth is not null).Select(t => t.BitDepth).DefaultIfEmpty().Max();
    public int? AacChannels => AudioTracks.Where(t => t.AacChannels is not null).Select(t => t.AacChannels).DefaultIfEmpty().Max();
}

/// <summary>
/// Original seek-based ISO BMFF inspection and bounded plain tx3g/QuickTime text extraction.
/// Reads codec configuration and selected subtitle tables/data, never entire movie payloads.
/// Does not decode AVC/HEVC/AAC or render text styles. ISO/IEC14496-12/15 and Apple QTFF:
/// https://developer.apple.com/documentation/quicktime-file-format/text_sample_data
/// https://developer.apple.com/documentation/quicktime-file-format/edit_list_atom
/// </summary>
public static class Mp4Inspector
{
    static readonly object StructuralErrorKey = new();
    public static Mp4Inspection Inspect(Stream source, bool extractTextSubtitles = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek) throw new NotSupportedException("MP4 inspection requires a readable seekable stream.");
        long position = source.Position;
        try { return new Reader(source, cancellationToken).Inspect(extractTextSubtitles); }
        catch (OverflowException e) { throw new InvalidDataException("MP4 metadata exceeds its integer bounds.", e); }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated MP4 metadata or subtitle sample.", e); }
        finally { source.Position = position; }
    }

    readonly record struct Box(string Type, long Start, long End) { public long Length => End - Start; }
    readonly record struct Entry(Box Box, ushort Reference);
    readonly record struct Edit(ulong Duration, long MediaTime);
    readonly record struct ChunkMap(uint First, uint Count, uint Description);
    sealed record SubtitlePlan(uint Id, string Language, List<Entry> Entries, List<Box> Tables, List<Box> Track,
        uint MediaScale, ulong MediaDuration);

    sealed class Reader(Stream source, CancellationToken token)
    {
        const int MaximumBoxes = 100_000, MaximumTracks = 128, MaximumSamples = 100_000;
        const long MetadataBudget = 67_108_864, TextBudget = 33_554_432;
        readonly long fileLength = source.Length;
        readonly List<(long Start, long End)> mediaRanges = [];
        long metadataRead, textRead;
        int boxes, subtitleSamples, subtitleCues, subtitleCharacters;

        public Mp4Inspection Inspect(bool extract)
        {
            var top = Children(0, fileLength);
            var moov = One(top, "moov", true)!.Value;
            foreach (var box in top.Where(b => b.Type == "mdat")) mediaRanges.Add((box.Start, box.End));
            var children = Children(moov.Start, moov.End);
            var mvhd = One(children, "mvhd", true)!.Value;
            uint movieScale = Scale(mvhd, out _, out _);
            var video = new List<Mp4VideoTrack>(); var audio = new List<Mp4AudioTrack>(); var subtitles = new List<Mp4SubtitleTrack>();
            var subtitlePlans = new List<SubtitlePlan>(); var warnings = new List<string>();
            var tracks = children.Where(b => b.Type == "trak").ToArray(); var ids = new HashSet<uint>();
            if (tracks.Length > MaximumTracks) throw new NotSupportedException("MP4 track count exceeds128.");
            foreach (var track in tracks)
            {
                token.ThrowIfCancellationRequested(); var parts = Children(track.Start, track.End);
                Box tkhd = One(parts, "tkhd", true)!.Value; byte[] trackHeader = Prefix(tkhd, 24);
                if (trackHeader[0] is not (0 or 1)) throw new NotSupportedException("Unsupported MP4 track-header version.");
                uint id = U32(trackHeader, trackHeader[0] == 1 ? 20 : 12);
                if (id == 0 || !ids.Add(id)) throw new InvalidDataException("Duplicate or zero MP4 track ID.");
                Box mdia = One(parts, "mdia", true)!.Value; var media = Children(mdia.Start, mdia.End);
                byte[] handler = Prefix(One(media, "hdlr", true)!.Value, 12); string kind = Encoding.ASCII.GetString(handler, 8, 4);
                if (kind is not ("vide" or "soun" or "text" or "subt" or "sbtl")) continue;
                uint mediaScale = Scale(One(media, "mdhd", true)!.Value, out ulong mediaDuration, out string language);
                var minf = One(media, "minf", true)!.Value; var mediaInfo = Children(minf.Start, minf.End);
                var stbl = One(mediaInfo, "stbl", true)!.Value; var tables = Children(stbl.Start, stbl.End);
                if (kind is "vide" or "soun")
                {
                    // Never let metadata-only fallback hide an external A/V data reference either.
                    if (One(mediaInfo, "dinf") is not null) _ = ValidateReferences(mediaInfo);
                    var descriptions = Descriptions(One(tables, "stsd", true)!.Value);
                    for (int index = 0; index < descriptions.Count; index++)
                    {
                        var entry = descriptions[index];
                        if (kind == "vide") video.Add(Video(id, entry.Box) with { SampleDescriptionIndex = index + 1 });
                        if (kind == "soun") audio.Add(Audio(id, entry.Box) with { SampleDescriptionIndex = index + 1 });
                    }
                    continue;
                }
                // Reference metadata is a security preflight, never a recoverable caption failure.
                int references = ValidateReferences(mediaInfo);
                try
                {
                    // Check security before format fallback, including metadata-only inspection.
                    var entries = Descriptions(One(tables, "stsd", true)!.Value);
                    if (entries.Any(e => e.Reference > references)) throw new InvalidDataException("MP4 sample uses an unavailable data reference.");
                    if (entries.Any(e => e.Box.Type is not ("tx3g" or "text"))) throw new NotSupportedException("MP4 subtitle sample entry is not supported by the plain-text reader.");
                    if (entries.Any(e => e.Box.Length < (e.Box.Type == "tx3g" ? 38 : 52))) throw new InvalidDataException("Truncated MP4 text sample-description layout.");
                    subtitlePlans.Add(new(id, language, entries, tables, parts, mediaScale, mediaDuration));
                }
                catch (Exception error) when (CaptionOnly(error)) { warnings.Add($"Track {id}: captions unavailable: {error.Message}"); }
            }
            // Finish A/V metadata before optional caption table reads can exhaust their budget.
            foreach (var plan in subtitlePlans)
            {
                token.ThrowIfCancellationRequested();
                int savedSamples = subtitleSamples, savedCues = subtitleCues, savedCharacters = subtitleCharacters;
                try
                {
                    IReadOnlyList<SubtitleCue> cues = [];
                    if (extract)
                    {
                        if (top.Any(b => b.Type == "moof") || children.Any(b => b.Type == "mvex")) throw new NotSupportedException("Fragmented MP4 subtitle extraction is not implemented.");
                        cues = Text(plan.Id, plan.Tables, plan.Entries, Edits(plan.Track), movieScale, plan.MediaScale, plan.MediaDuration);
                    }
                    subtitles.Add(new(plan.Id, string.Join("/", plan.Entries.Select(e => e.Box.Type).Distinct()), plan.Language, cues));
                }
                catch (Exception error) when (CaptionOnly(error))
                {
                    // Failed tracks retain no decoded output; actual I/O/metadata read budgets stay spent.
                    subtitleSamples = savedSamples; subtitleCues = savedCues; subtitleCharacters = savedCharacters;
                    warnings.Add($"Track {plan.Id}: captions unavailable: {error.Message}");
                }
            }
            return new(video, audio, subtitles) { SubtitleWarnings = warnings };
        }

        static bool CaptionOnly(Exception error) => error is InvalidDataException or NotSupportedException
            && error is not Mp4ExternalDataReferenceException && !error.Data.Contains(StructuralErrorKey);
        static Exception Structure(string message, bool unsupported = false)
        {
            Exception error = unsupported ? new NotSupportedException(message) : new InvalidDataException(message);
            error.Data[StructuralErrorKey] = true; return error;
        }

        List<Box> Children(long start, long end, bool quickTimeEntryTerminator = false)
        {
            var result = new List<Box>(); long cursor = start;
            while (cursor < end)
            {
                token.ThrowIfCancellationRequested();
                if (++boxes > MaximumBoxes) throw Structure("MP4 box count exceeds100,000.", true);
                // Apple's ImageDescription extension list may end with a four-byte zero sentinel.
                if (quickTimeEntryTerminator && end - cursor == 4 && U32(Read(cursor, 4), 0) == 0) break;
                if (end - cursor < 8) throw Structure("Partial MP4 box header.");
                byte[] header = Read(cursor, 8); uint size32 = U32(header, 0); long headerLength = 8;
                ulong size = size32;
                if (size32 == 1) { if (end - cursor < 16) throw Structure("Partial extended MP4 header."); size = U64(Read(cursor + 8, 8), 0); headerLength = 16; }
                if (size32 == 0) size = (ulong)(end - cursor);
                if (size < (ulong)headerLength || size > (ulong)(end - cursor)) throw Structure("MP4 box exceeds parent/file bounds.");
                long next = checked(cursor + (long)size);
                result.Add(new(Encoding.ASCII.GetString(header, 4, 4), cursor + headerLength, next)); cursor = next;
            }
            return result;
        }
        static Box? One(List<Box> boxes, string name, bool required = false)
        {
            var found = boxes.Where(b => b.Type == name).Take(2).ToArray();
            if (found.Length > 1 || required && found.Length == 0) throw new InvalidDataException("Missing or duplicate MP4 " + name + " box.");
            return found.Length == 0 ? null : found[0];
        }
        List<Entry> Descriptions(Box stsd)
        {
            byte[] header = Prefix(stsd, 8);
            if (U32(header, 0) != 0) throw new NotSupportedException("Unsupported MP4 sample-description version/flags.");
            uint count = U32(header, 4);
            if (count is < 1 or > 64) throw new NotSupportedException("MP4 sample-description count exceeds1–64.");
            var boxes = Children(stsd.Start + 8, stsd.End);
            if (boxes.Count != count) throw new InvalidDataException("MP4 sample-description count disagrees with entries.");
            return boxes.Select(b => { byte[] p = Prefix(b, 8); ushort reference = U16(p, 6); if (reference == 0) throw new InvalidDataException("Zero MP4 data reference."); return new Entry(b, reference); }).ToList();
        }
        Mp4VideoTrack Video(uint id, Box entry)
        {
            byte[] header = Prefix(entry, 78); int width = U16(header, 24), height = U16(header, 26);
            if (width == 0 || height == 0) throw new InvalidDataException("Zero MP4 video dimensions.");
            string codec = entry.Type; int? depth = null, profile = null;
            if (entry.Type is "hvc1" or "hev1")
            {
                Box config = One(Children(entry.Start + 78, entry.End, true), "hvcC", true)!.Value;
                byte[] h = Prefix(config, 23); if (h[0] != 1) throw new InvalidDataException("Invalid HEVC configuration version.");
                profile = h[1] & 31; depth = 8 + (h[17] & 7); codec = "HEVC";
            }
            else if (entry.Type is "avc1" or "avc3")
            {
                Box config = One(Children(entry.Start + 78, entry.End, true), "avcC", true)!.Value;
                byte[] h = Prefix(config, 7); if (h[0] != 1) throw new InvalidDataException("Invalid AVC configuration version.");
                profile = h[1]; depth = h[1] is 66 or 77 or 88 or 100 ? 8 : null; codec = "AVC";
            }
            return new(id, entry.Type, codec, width, height, depth, profile);
        }
        Mp4AudioTrack Audio(uint id, Box entry)
        {
            byte[] header = Prefix(entry, 28); int version = U16(header, 8), channels = U16(header, 16), rate = (int)(U32(header, 24) >> 16);
            int originalChannels = channels; string codec = entry.Type; int? objectType = null, ascChannels = null;
            if (entry.Type == "mp4a")
            {
                if (version is not (0 or 1)) throw new NotSupportedException("AAC inspection supports AudioSampleEntry versions0/1.");
                int skip = version == 1 ? 44 : 28; if (entry.Length < skip) throw new InvalidDataException("Truncated MP4 audio sample entry.");
                var children = Children(entry.Start + skip, entry.End, true); Box? esds = One(children, "esds");
                if (esds is null && One(children, "wave") is Box wave) esds = One(Children(wave.Start, wave.End, true), "esds");
                if (esds is Box description)
                {
                    if (description.Length > 1_048_576) throw new NotSupportedException("MP4 audio descriptor exceeds1MiB.");
                    byte[] payload = Payload(description); if (payload.Length < 4 || U32(payload, 0) != 0) throw new InvalidDataException("Invalid MP4 ES descriptor version/flags.");
                    byte[]? asc = FindAsc(payload, 4, payload.Length, 0, false);
                    if (asc is not null)
                    {
                        var parsed = Aac(asc); objectType = parsed.Type; if (parsed.Channels > 0) { channels = parsed.Channels; ascChannels = channels; }
                        rate = parsed.Rate; codec = parsed.Type == 2 ? "AAC-LC" : parsed.Type is 5 or 29 ? "HE-AAC" : "AAC";
                    }
                }
                if (objectType is null) codec = "MPEG-4 audio";
            }
            return new(id, entry.Type, codec, channels, rate, objectType) { SampleEntryChannels = originalChannels, AacChannels = ascChannels };
        }
        static byte[]? FindAsc(byte[] data, int start, int end, int depth, bool aac)
        {
            if (depth > 4) throw new NotSupportedException("MP4 ES descriptor nesting exceeds its bound.");
            byte[]? result = null; int cursor = start;
            while (cursor < end)
            {
                int tag = data[cursor++], length = 0, n = 0;
                byte b;
                do { if (cursor >= end || n++ == 4) throw new InvalidDataException("Truncated/oversized ES descriptor length."); b = data[cursor++]; length = checked(length * 128 + (b & 127)); } while ((b & 128) != 0);
                if (length > end - cursor) throw new InvalidDataException("ES descriptor exceeds its parent.");
                int next = cursor + length; byte[]? found = null;
                if (tag == 3)
                {
                    if (length < 3) throw new InvalidDataException("Truncated ES Descriptor.");
                    int flags = data[cursor + 2], child = cursor + 3;
                    if ((flags & 128) != 0) child += 2;
                    if ((flags & 64) != 0) { if (child >= next) throw new InvalidDataException("Truncated ES URL length."); child += 1 + data[child]; }
                    if ((flags & 32) != 0) child += 2;
                    if (child > next) throw new InvalidDataException("Truncated ES optional fields.");
                    found = FindAsc(data, child, next, depth + 1, false);
                }
                else if (tag == 4)
                {
                    if (length < 13) throw new InvalidDataException("Truncated decoder configuration descriptor.");
                    int indication = data[cursor]; found = FindAsc(data, cursor + 13, next, depth + 1, indication == 0x40 || indication is >= 0x66 and <= 0x68);
                }
                else if (tag == 5 && aac)
                {
                    if (length is < 2 or > 64) throw new InvalidDataException("AAC AudioSpecificConfig exceeds2–64 bytes.");
                    found = data.AsSpan(cursor, length).ToArray();
                }
                if (found is not null) { if (result is not null) throw new InvalidDataException("Duplicate AAC AudioSpecificConfig."); result = found; }
                cursor = next;
            }
            return result;
        }
        static (int Type, int Rate, int Channels) Aac(byte[] data)
        {
            int bit = 0;
            int Bits(int n) { if (bit + n > data.Length * 8) throw new InvalidDataException("Truncated AAC AudioSpecificConfig."); int x = 0; for (int i = 0; i < n; i++, bit++) x = (x << 1) | ((data[bit / 8] >> (7 - bit % 8)) & 1); return x; }
            int Type() { int t = Bits(5); return t == 31 ? 32 + Bits(6) : t; }
            int Rate() { int i = Bits(4); int[] rates = [96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350]; int rate = i == 15 ? Bits(24) : i < rates.Length ? rates[i] : throw new InvalidDataException("Reserved AAC frequency index."); if (rate is < 1 or > 768000) throw new InvalidDataException("Invalid AAC sample rate."); return rate; }
            int type = Type(), rate = Rate(), layout = Bits(4);
            if (type == 0) throw new InvalidDataException("Reserved AAC audio object type.");
            if (type is 5 or 29) { rate = Rate(); _ = Type(); }
            int channels = layout switch { 0 => 0, 1 or 2 or 3 or 4 or 5 or 6 => layout, 7 => 8, _ => throw new NotSupportedException("AAC channel configuration is not understood.") };
            return (type, rate, channels);
        }
        uint Scale(Box box, out ulong duration, out string language)
        {
            byte[] header = Prefix(box, 4); int version = header[0];
            if (version is not (0 or 1)) throw new NotSupportedException("Unsupported MP4 media/movie header version.");
            int scaleOffset = version == 1 ? 20 : 12, durationOffset = version == 1 ? 24 : 16;
            int needed = box.Type == "mdhd" ? version == 1 ? 36 : 24 : version == 1 ? 32 : 20;
            header = Prefix(box, needed); uint scale = U32(header, scaleOffset); if (scale == 0) throw new InvalidDataException("Zero MP4 timescale.");
            duration = version == 1 ? U64(header, durationOffset) : U32(header, durationOffset); language = "und";
            if (box.Type == "mdhd")
            {
                ushort packed = U16(header, version == 1 ? 32 : 20);
                int a = (packed >> 10) & 31, b = (packed >> 5) & 31, c = packed & 31;
                if (a is >= 1 and <= 26 && b is >= 1 and <= 26 && c is >= 1 and <= 26) language = new string([(char)(a + 96), (char)(b + 96), (char)(c + 96)]);
            }
            return scale;
        }
        List<Edit> Edits(List<Box> track)
        {
            if (One(track, "edts") is not Box edts) return [];
            Box elst = One(Children(edts.Start, edts.End), "elst", true)!.Value; byte[] data = Payload(elst);
            if (data.Length < 8 || data[0] is not (0 or 1)) throw new InvalidDataException("Invalid MP4 edit list header.");
            int version = data[0], width = version == 1 ? 20 : 12; uint count = U32(data, 4);
            if (count > 128 || data.Length != 8L + (long)count * width) throw new NotSupportedException("MP4 edit list exceeds its supported shape/count.");
            var result = new List<Edit>();
            for (int i = 0, p = 8; i < count; i++, p += width)
            {
                ulong duration = version == 1 ? U64(data, p) : U32(data, p);
                long mediaTime = version == 1 ? unchecked((long)U64(data, p + 8)) : unchecked((int)U32(data, p + 4));
                if (mediaTime < -1 || U32(data, p + width - 4) != 65536) throw new NotSupportedException("MP4 dwell/reverse/rate-changing edits are not implemented.");
                result.Add(new(duration, mediaTime));
            }
            return result;
        }
        int ValidateReferences(List<Box> mediaInfo)
        {
            Box dinf = One(mediaInfo, "dinf", true)!.Value; Box dref = One(Children(dinf.Start, dinf.End), "dref", true)!.Value;
            byte[] head = Prefix(dref, 8); uint count = U32(head, 4); var references = Children(dref.Start + 8, dref.End);
            if (U32(head, 0) != 0 || count != references.Count) throw new InvalidDataException("Invalid MP4 data-reference table.");
            foreach (var reference in references)
            {
                // Legacy QuickTime uses a self-contained alis entry with no alias payload.
                if (reference.Type is not ("url " or "alis") || U32(Prefix(reference, 4), 0) != 1) throw new Mp4ExternalDataReferenceException("External/non-self-contained MP4 data references are not followed.");
            }
            return references.Count;
        }
        IReadOnlyList<SubtitleCue> Text(uint id, List<Box> tables, List<Entry> entries, List<Edit> edits, uint movieScale, uint scale, ulong mediaDuration)
        {
            byte[] sizesData = Payload(One(tables, "stsz", true)!.Value);
            if (sizesData.Length < 12 || U32(sizesData, 0) != 0) throw new InvalidDataException("Invalid MP4 subtitle sample-size table.");
            uint fixedSize = U32(sizesData, 4), count = U32(sizesData, 8);
            if (count > MaximumSamples || sizesData.Length != 12L + (fixedSize == 0 ? (long)count * 4 : 0)) throw new NotSupportedException("MP4 subtitle sample-size count/bounds exceed limits.");
            if (count > MaximumSamples - subtitleSamples) throw new NotSupportedException("MP4 subtitle sample count exceeds100,000 across tracks.");
            subtitleSamples += (int)count;
            if (count == 0) return [];
            var sizes = new uint[count]; for (int i = 0; i < count; i++) { sizes[i] = fixedSize == 0 ? U32(sizesData, 12 + i * 4) : fixedSize; if (sizes[i] is < 2 or > 1_048_576) throw new NotSupportedException("MP4 text sample must be2 bytes–1MiB."); }
            long[] durations = Timing(One(tables, "stts", true)!.Value, (int)count, false);
            long[] offsets = One(tables, "ctts") is Box ctts ? Timing(ctts, (int)count, true) : new long[count];
            Box? stco = One(tables, "stco"), co64 = One(tables, "co64");
            if (stco.HasValue == co64.HasValue) throw new InvalidDataException("MP4 subtitles require exactly one stco/co64 table.");
            Box chunkBox = stco ?? co64!.Value; byte[] chunksData = Payload(chunkBox); int stride = stco is not null ? 4 : 8;
            if (chunksData.Length < 8 || U32(chunksData, 0) != 0) throw new InvalidDataException("Invalid MP4 chunk-offset header.");
            uint chunks = U32(chunksData, 4);
            if (chunks is < 1 or > MaximumSamples || chunksData.Length != 8L + (long)chunks * stride) throw new NotSupportedException("MP4 subtitle chunk table exceeds limits.");
            byte[] mappingData = Payload(One(tables, "stsc", true)!.Value);
            if (mappingData.Length < 8 || U32(mappingData, 0) != 0) throw new InvalidDataException("Invalid MP4 sample-to-chunk header.");
            uint mappings = U32(mappingData, 4);
            if (mappings is < 1 or > MaximumSamples || mappingData.Length != 8L + (long)mappings * 12) throw new NotSupportedException("MP4 sample-to-chunk count/bounds exceed limits.");
            var mapping = new ChunkMap[mappings];
            for (int i = 0; i < mappings; i++)
            {
                int p = 8 + i * 12; mapping[i] = new(U32(mappingData, p), U32(mappingData, p + 4), U32(mappingData, p + 8)); var map = mapping[i];
                if (map.First == 0 || map.First > chunks || i == 0 && map.First != 1 || i > 0 && map.First <= mapping[i - 1].First || map.Count == 0 || map.Description == 0 || map.Description > entries.Count)
                    throw new InvalidDataException("Invalid MP4 subtitle sample-to-chunk run.");
            }
            var packets = new List<MediaPacket>(); int sample = 0, run = 0; long dts = 0;
            for (int chunk = 1; chunk <= chunks; chunk++)
            {
                token.ThrowIfCancellationRequested(); if (run + 1 < mapping.Length && chunk >= mapping[run + 1].First) run++;
                ulong rawOffset = stride == 4 ? U32(chunksData, 8 + (chunk - 1) * 4) : U64(chunksData, 8 + (chunk - 1) * 8);
                if (rawOffset > long.MaxValue || mapping[run].Count > count - sample) throw new InvalidDataException("MP4 subtitle chunk/sample count exceeds bounds.");
                long position = (long)rawOffset;
                for (int n = 0; n < mapping[run].Count; n++, sample++)
                {
                    if ((sample & 1023) == 0) token.ThrowIfCancellationRequested();
                    if (!mediaRanges.Any(r => position >= r.Start && position <= r.End && sizes[sample] <= r.End - position)) throw new InvalidDataException("MP4 subtitle sample lies outside local mdat payloads.");
                    long end = position + sizes[sample];
                    byte[] lengthBytes = ReadText(position, 2); int textLength = U16(lengthBytes, 0);
                    if (textLength > sizes[sample] - 2) throw new InvalidDataException("MP4 text length exceeds its sample.");
                    byte[] text = ReadText(position + 2, textLength);
                    long pts = checked(dts + offsets[sample]); AddText(packets, text, pts, durations[sample], scale, edits, movieScale);
                    dts = checked(dts + durations[sample]); position = end;
                }
            }
            if (sample != count || mediaDuration != uint.MaxValue && mediaDuration != ulong.MaxValue && (ulong)dts > mediaDuration) throw new InvalidDataException("MP4 subtitle sample/timing count disagrees with media duration.");
            var track = new DemuxedTrack(id, "S_TEXT/UTF8", [], 0, 0, 0, 0, 0, packets) { TrackType = 17 };
            var cues = EmbeddedTextDecoder.Decode(track, 0, token);
            subtitleCues += cues.Count; subtitleCharacters += cues.Sum(c => c.Text.Length);
            if (subtitleCues > MaximumSamples || subtitleCharacters > 4_000_000) throw new NotSupportedException("MP4 subtitle output exceeds100,000 cues/four million characters across tracks.");
            return cues;
        }
        long[] Timing(Box table, int count, bool composition)
        {
            byte[] data = Payload(table);
            if (data.Length < 8 || data[0] > (composition ? 1 : 0) || (U32(data, 0) & 0xffffff) != 0) throw new InvalidDataException("Invalid MP4 subtitle timing version/flags.");
            uint entries = U32(data, 4); if (entries > MaximumSamples || data.Length != 8L + (long)entries * 8) throw new NotSupportedException("MP4 subtitle timing table exceeds limits.");
            var result = new long[count]; int used = 0;
            for (int i = 0; i < entries; i++)
            {
                uint n = U32(data, 8 + i * 8); uint raw = U32(data, 12 + i * 8);
                if (n == 0 || n > count - used) throw new InvalidDataException("MP4 subtitle timing runs exceed sample count.");
                long value = composition && data[0] == 1 ? unchecked((int)raw) : raw;
                Array.Fill(result, value, used, (int)n); used += (int)n;
            }
            if (used != count) throw new InvalidDataException("MP4 subtitle timing does not cover every sample."); return result;
        }
        static void AddText(List<MediaPacket> packets, byte[] text, long pts, long duration, uint scale, List<Edit> edits, uint movieScale)
        {
            Int128 start = (Int128)pts * 1_000_000_000 / scale, end = ((Int128)pts + duration) * 1_000_000_000 / scale;
            void Add(Int128 begin, Int128 finish)
            {
                begin = Int128.Max(0, begin); if (finish <= begin) return;
                if (begin > long.MaxValue || finish > long.MaxValue || packets.Count >= MaximumSamples) throw new NotSupportedException("MP4 subtitle cue timeline/count exceeds its bounds.");
                packets.Add(new(text, (long)begin, (long)(finish - begin), true));
            }
            if (edits.Count == 0) { Add(start, end); return; }
            Int128 cursor = 0;
            foreach (var edit in edits)
            {
                Int128 movieStart = cursor * 1_000_000_000 / movieScale; cursor += edit.Duration; Int128 movieEnd = cursor * 1_000_000_000 / movieScale;
                if (edit.MediaTime == -1) continue;
                Int128 mediaStart = (Int128)edit.MediaTime * 1_000_000_000 / scale;
                Int128 a = Int128.Max(start, mediaStart), b = Int128.Min(end, mediaStart + movieEnd - movieStart);
                if (b > a) Add(movieStart + a - mediaStart, movieStart + b - mediaStart);
            }
        }
        byte[] Prefix(Box box, int length) { if (box.Length < length) throw new InvalidDataException("Truncated MP4 " + box.Type + " payload."); return Read(box.Start, length); }
        byte[] Payload(Box box) { if (box.Length > MetadataBudget) throw new NotSupportedException("MP4 metadata box exceeds64MiB."); return Read(box.Start, (int)box.Length); }
        byte[] Read(long position, int count)
        {
            metadataRead = checked(metadataRead + count); if (metadataRead > MetadataBudget) throw new NotSupportedException("MP4 metadata read budget exceeds64MiB.");
            return ReadAt(position, count);
        }
        byte[] ReadText(long position, int count)
        {
            textRead = checked(textRead + count); if (textRead > TextBudget) throw new NotSupportedException("MP4 subtitle read budget exceeds32MiB.");
            return ReadAt(position, count);
        }
        byte[] ReadAt(long position, int count)
        {
            token.ThrowIfCancellationRequested(); if (position < 0 || count < 0 || position > fileLength || count > fileLength - position) throw new InvalidDataException("MP4 read lies outside file bounds.");
            source.Position = position; byte[] data = new byte[count]; source.ReadExactly(data); return data;
        }
        static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset));
    }
}
