using System.Buffers.Binary;
using System.Text;
using Luma.Core.Containers;
using Luma.Core.Subtitles;

namespace Luma.Core.Tests;

/// <summary>
/// Independent, bounded test-only sample-table reader and original EBML fixture writer.
/// Uses only this repository's synthetic AVC/HEVC media; no decoder, downloaded media,
/// or external multimedia implementation is involved. Native decoding is checked separately.
/// </summary>
public static class NativePacketFixtures
{
    const int MaximumBytes = 64 * 1024 * 1024, MaximumSamples = 100_000;
    sealed record Sample(byte[] Data, long DecodeTicks, long PresentationTicks, long DurationTicks, bool Keyframe);
    sealed record Video(string CodecId, byte[] Configuration, int Width, int Height, uint Timescale,
        List<Sample> Samples, long DefaultDurationNanoseconds)
    {
        public List<MediaPacket> Packets()
        {
            long minimum = Samples.Min(s => s.PresentationTicks);
            return Samples.Select(s => new MediaPacket(s.Data, ToNanoseconds(checked(s.PresentationTicks - minimum), Timescale),
                ToNanoseconds(checked(s.DecodeTicks + s.DurationTicks), Timescale) - ToNanoseconds(s.DecodeTicks, Timescale), s.Keyframe)).ToList();
        }
        public DemuxedMatroska Model() => new([new(1, CodecId, Configuration, Width, Height, 0, 0,
            DefaultDurationNanoseconds, Packets()) { TrackType = 1, Name = "Original synthetic video", Language = "und" }], 1);
    }
    readonly record struct Box(string Type, int Start, int Body, int End);

    public static void Run(string fixtures)
    {
        string? export = Environment.GetEnvironmentVariable("LUMA_WINDOWS_MEDIA_EXPORT");
        foreach (var (file, label, count, width, height, reordered, bitDepth) in new[]
        {
            ("Luma-BFrames.mp4", "bframes", 360, 960, 540, true, 8),
            ("Luma-HEVC8.mov", "hevc8", 60, 960, 540, false, 8),
            ("Luma-HEVC10.mov", "hevc10", 60, 960, 540, false, 10)
        })
        {
            string path = Path.Combine(fixtures, file);
            Check(new FileInfo(path).Length is > 0 and <= MaximumBytes, "Source fixture byte limit");
            byte[] bytes = File.ReadAllBytes(path);
            Video source = Parse(bytes);
            Check(source.Samples.Count == count && source.Width == width && source.Height == height, file + " dimensions/frame count");
            Check(source.Samples[0].Keyframe && source.DefaultDurationNanoseconds == 33_333_333, file + " random-access start/30 fps timing");
            int inversions = source.Samples.Zip(source.Samples.Skip(1), (a, b) => b.PresentationTicks < a.PresentationTicks).Count(v => v);
            Check((inversions > 0) == reordered, file + " actual presentation reordering");
            if (label == "bframes") Check(source.CodecId == "V_MPEG4/ISO/AVC" && inversions == 176, "Real encoded B-frame AVC regression coverage");
            else Check(source.CodecId == "V_MPEGH/ISO/HEVC" && (source.Configuration[17] & 7) + 8 == bitDepth,
                "Real encoded HEVC configuration bit depth");

            DemuxedMatroska direct = source.Model();
            byte[] matroska = Matroska(direct.Tracks.Single());
            var demuxed = MatroskaDemuxer.Demux(matroska);
            Check(demuxed.TimestampScaleNanoseconds == 1 && demuxed.Tracks.Count == 1, "Original Matroska fixture 1 ns precision/video-only track");
            VerifyPackets(direct.Tracks.Single(), demuxed.Tracks.Single());
            using var output = new MemoryStream();
            var result = MatroskaRemuxer.Remux(demuxed, output);
            Check(result.AudioTracks.Count == 0 && result.SubtitleTracks.Count == 0 && result.SelectedAudioTrackId is null && result.OriginNanoseconds == 0,
                "Video-only remux metadata/origin");
            byte[] remuxed = output.ToArray();
            Video parsedOutput = Parse(remuxed);
            VerifyPackets(direct.Tracks.Single(), parsedOutput.Model().Tracks.Single());
            Check(parsedOutput.Samples.Zip(parsedOutput.Samples.Skip(1), (a, b) => b.DecodeTicks == a.DecodeTicks + a.DurationTicks).All(v => v),
                "Remuxed sample-table decode timeline is contiguous");
            Check(parsedOutput.Samples.Zip(parsedOutput.Samples.Skip(1), (a, b) => b.PresentationTicks < a.PresentationTicks).Count(v => v) == inversions,
                "Remux retained actual B-frame presentation inversions in coded order");

            // A direct portable model and the original Matroska carrier must agree.
            using var directOutput = new MemoryStream();
            MatroskaRemuxer.Remux(direct, directOutput);
            VerifyPackets(direct.Tracks.Single(), Parse(directOutput.ToArray()).Model().Tracks.Single());
            if (label == "bframes")
            {
                byte[] textMatroska = Matroska(direct.Tracks.Single(), includeText: true);
                var textModel = MatroskaDemuxer.Demux(textMatroska);
                Check(textModel.Tracks.Count == 3 && textModel.Tracks.Count(t => t.TrackType == 17) == 2,
                    "Original native caption fixture keeps video and both subtitle tracks");
                VerifyPackets(direct.Tracks.Single(), textModel.Tracks.Single(t => t.TrackType == 1));
                var ass = textModel.Tracks.Single(t => t.Id == 2);
                var utf8 = textModel.Tracks.Single(t => t.Id == 3);
                Check(ass.CodecId == "S_TEXT/ASS" && ass.IsDefault && ass.Name == "Original ASS" && ass.Language == "en"
                    && utf8.CodecId == "S_TEXT/UTF8" && !utf8.IsDefault && utf8.Name == "Original UTF-8" && utf8.Language == "en",
                    "Native caption track IDs/defaults/names/language");
                foreach (var (track, text) in new[] { (ass, "Original\ncaption"), (utf8, "<Naruto> 2 < 3 and 4 > 1") })
                {
                    Check(track.Packets.Count == 1 && track.Packets[0].TimestampNanoseconds == 500_000_000
                        && track.Packets[0].DurationNanoseconds == 1_000_000_000, "Exact native caption packet start/duration");
                    var decoded = EmbeddedTextDecoder.Decode(track, 0);
                    Check(decoded.Count == 1 && decoded[0].Start == .5 && decoded[0].End == 1.5 && decoded[0].Text == text,
                        "Native caption plain text/overrides/literal comparisons retained");
                    Check(SubtitleParser.At(decoded, .75) == text && SubtitleParser.At(decoded, 1.75) == "",
                        "Native caption timing boundaries");
                }
                using var textOutput = new MemoryStream();
                var textResult = MatroskaRemuxer.Remux(textModel, textOutput);
                Check(textResult.SubtitleTracks.Count == 2, "Native remux retains both selectable embedded caption tracks");
                VerifyPackets(direct.Tracks.Single(), Parse(textOutput.ToArray()).Model().Tracks.Single());
                if (!string.IsNullOrWhiteSpace(export))
                {
                    Directory.CreateDirectory(export);
                    File.WriteAllBytes(Path.Combine(export, "Luma-review-text.mkv"), textMatroska);
                }
                Console.WriteLine("PASS: original B-frame MKV with default ASS and alternate UTF-8 captions; exact packet timing, plain text, selectable metadata and unchanged video payloads");
            }
            MalformedTables(bytes);
            if (!string.IsNullOrWhiteSpace(export))
            {
                Directory.CreateDirectory(export);
                File.WriteAllBytes(Path.Combine(export, "Luma-remux-" + label + ".mp4"), remuxed);
                File.WriteAllBytes(Path.Combine(export, "Luma-review-" + label + ".mkv"), matroska);
            }
            Console.WriteLine($"PASS: original {label} native packets: {source.Width}×{source.Height}, {source.Samples.Count} frames, " +
                $"30 fps, {inversions} presentation inversions, {bitDepth}-bit config; exact coded payload/config/PTS/keyframes through Matroska → MP4");
        }
    }

    static void VerifyPackets(DemuxedTrack expected, DemuxedTrack actual)
    {
        Check(actual.CodecId == expected.CodecId && actual.Width == expected.Width && actual.Height == expected.Height
            && actual.DefaultDurationNanoseconds == expected.DefaultDurationNanoseconds
            && actual.CodecPrivate.SequenceEqual(expected.CodecPrivate) && actual.Packets.Count == expected.Packets.Count,
            "Video metadata/configuration/sample count retained");
        for (int index = 0; index < expected.Packets.Count; index++)
        {
            var a = expected.Packets[index]; var b = actual.Packets[index];
            Check(a.TimestampNanoseconds == b.TimestampNanoseconds && a.Keyframe == b.Keyframe && a.Data.SequenceEqual(b.Data),
                "Exact packet payload, normalized PTS and sync flags in original coding order");
        }
    }

    static Video Parse(byte[] data)
    {
        Check(data.Length is > 0 and <= MaximumBytes, "MP4 fixture byte limit");
        Box[] top = Boxes(data, 0, data.Length);
        Check(!top.Any(b => b.Type == "moof"), "Fragmented test media is unsupported");
        Box[] mdats = top.Where(b => b.Type == "mdat").ToArray(); Check(mdats.Length > 0, "Media payload missing");
        Box moov = top.Single(b => b.Type == "moov");
        Box[] videoTracks = Boxes(data, moov.Body, moov.End).Where(b => b.Type == "trak")
            .Where(t => { var handler = Child(data, Child(data, t, "mdia"), "hdlr"); Need(handler.Body, 12, handler.End); return FourCC(data, handler.Body + 8) == "vide"; }).ToArray();
        Check(videoTracks.Length == 1, "Exactly one original video track required");
        Box mdia = Child(data, videoTracks[0], "mdia"), mdhd = Child(data, mdia, "mdhd");
        Need(mdhd.Body, 4, mdhd.End); byte mdhdVersion = data[mdhd.Body];
        Check(mdhdVersion is 0 or 1, "Unsupported mdhd version");
        int scaleOffset = mdhd.Body + (mdhdVersion == 0 ? 12 : 20); Need(scaleOffset, 4, mdhd.End);
        uint timescale = U32(data, scaleOffset); Check(timescale is > 0 and <= 1_000_000_000, "Invalid video timescale");
        Box table = Child(data, Child(data, mdia, "minf"), "stbl"), stsd = Child(data, table, "stsd");
        Need(stsd.Body, 8, stsd.End); Check(U32(data, stsd.Body + 4) == 1, "Multiple video sample descriptions unsupported");
        Box entry = Boxes(data, stsd.Body + 8, stsd.End).Single();
        string codec = entry.Type switch { "avc1" or "avc3" => "V_MPEG4/ISO/AVC", "hvc1" or "hev1" => "V_MPEGH/ISO/HEVC", _ => throw new InvalidDataException("Unsupported fixture video sample entry.") };
        Need(entry.Body, 78, entry.End); int width = U16(data, entry.Body + 24), height = U16(data, entry.Body + 26);
        Check(width is > 0 and <= 8192 && height is > 0 and <= 8192 && (long)width * height <= 16_777_216, "Video dimensions out of bounds");
        Box config = Boxes(data, entry.Body + 78, entry.End, allowSampleEntryTerminator: true)
            .Single(b => b.Type == (codec == "V_MPEG4/ISO/AVC" ? "avcC" : "hvcC"));
        Check(config.End - config.Body is >= 7 and <= 1_048_576, "Missing/oversized codec configuration");
        byte[] configuration = data[config.Body..config.End];
        Check(configuration[0] == 1 && (codec != "V_MPEGH/ISO/HEVC" || configuration.Length >= 23), "Invalid AVC/HEVC configuration");

        Box stsz = Child(data, table, "stsz"); Need(stsz.Body, 12, stsz.End);
        uint constantSize = U32(data, stsz.Body + 4), countValue = U32(data, stsz.Body + 8);
        Check(countValue is > 0 and <= MaximumSamples, "Video sample count out of bounds"); int count = (int)countValue;
        Check(stsz.End - stsz.Body == 12L + (constantSize == 0 ? count * 4L : 0), "stsz count/length mismatch");
        var sizes = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint size = constantSize == 0 ? U32(data, stsz.Body + 12 + i * 4) : constantSize;
            Check(size is > 0 and <= MaximumBytes, "Sample size out of bounds"); sizes[i] = (int)size;
        }
        Box[] offsetTables = Boxes(data, table.Body, table.End).Where(b => b.Type is "stco" or "co64").ToArray();
        Check(offsetTables.Length == 1, "Exactly one chunk offset table required"); Box offsets = offsetTables[0];
        Need(offsets.Body, 8, offsets.End); uint chunkValue = U32(data, offsets.Body + 4);
        Check(chunkValue is > 0 && chunkValue <= countValue, "Chunk count out of bounds"); int chunks = (int)chunkValue, offsetWidth = offsets.Type == "co64" ? 8 : 4;
        Check(offsets.End - offsets.Body == 8L + chunks * (long)offsetWidth, "Chunk offset table length mismatch");
        Box stsc = Child(data, table, "stsc"); Need(stsc.Body, 8, stsc.End);
        uint mapValue = U32(data, stsc.Body + 4); Check(mapValue is > 0 && mapValue <= chunkValue, "stsc map count out of bounds"); int maps = (int)mapValue;
        Check(stsc.End - stsc.Body == 8L + maps * 12L, "stsc length mismatch");
        var chunkMap = new (uint First, uint Samples)[maps];
        for (int i = 0; i < maps; i++)
        {
            int at = stsc.Body + 8 + i * 12; uint first = U32(data, at), samples = U32(data, at + 4);
            Check(first is > 0 && first <= chunkValue && (i == 0 ? first == 1 : first > chunkMap[i - 1].First)
                && samples is > 0 && samples <= countValue && U32(data, at + 8) == 1, "Invalid sample/chunk description map");
            chunkMap[i] = (first, samples);
        }
        var payloads = new byte[count][]; var extents = new List<(long Start, long End)>(); int sampleIndex = 0, mapIndex = 0;
        for (int chunk = 1; chunk <= chunks; chunk++)
        {
            while (mapIndex + 1 < maps && chunkMap[mapIndex + 1].First <= chunk) mapIndex++;
            ulong value = offsetWidth == 8 ? U64(data, offsets.Body + 8 + (chunk - 1) * 8) : U32(data, offsets.Body + 8 + (chunk - 1) * 4);
            Check(value <= (ulong)data.Length, "Chunk offset outside file"); long cursor = (long)value, beginning = cursor;
            uint perChunk = chunkMap[mapIndex].Samples; Check(perChunk <= count - sampleIndex, "Chunk sample count exceeds stsz");
            for (int i = 0; i < perChunk; i++)
            {
                int size = sizes[sampleIndex]; Check(mdats.Any(m => cursor >= m.Body && cursor + size <= m.End), "Sample extent outside mdat");
                payloads[sampleIndex++] = data.AsSpan((int)cursor, size).ToArray(); cursor += size;
            }
            extents.Add((beginning, cursor));
        }
        Check(sampleIndex == count, "Chunk mapping does not cover every sample");
        var sortedExtents = extents.OrderBy(e => e.Start).ToArray();
        Check(sortedExtents.Zip(sortedExtents.Skip(1), (a, b) => a.End <= b.Start).All(v => v), "Overlapping media chunks");
        long[] durations = Expand(data, Child(data, table, "stts"), count, false, false);
        Box[] composition = Boxes(data, table.Body, table.End).Where(b => b.Type == "ctts").ToArray();
        Check(composition.Length <= 1, "Duplicate composition table");
        long[] deltas = composition.Length == 0 ? new long[count] : Expand(data, composition[0], count, true, true);
        Box[] sync = Boxes(data, table.Body, table.End).Where(b => b.Type == "stss").ToArray(); Check(sync.Length <= 1, "Duplicate sync sample table");
        var keys = Enumerable.Repeat(sync.Length == 0, count).ToArray();
        if (sync.Length == 1)
        {
            Box stss = sync[0]; Need(stss.Body, 8, stss.End); uint entries = U32(data, stss.Body + 4);
            Check(entries <= count && stss.End - stss.Body == 8L + entries * 4L, "stss length/count mismatch"); uint previous = 0;
            for (int i = 0; i < entries; i++) { uint index = U32(data, stss.Body + 8 + i * 4); Check(index > previous && index <= count, "Invalid sync sample index"); keys[index - 1] = true; previous = index; }
        }
        var samplesOutput = new List<Sample>(count); long decode = 0;
        for (int i = 0; i < count; i++) { samplesOutput.Add(new(payloads[i], decode, checked(decode + deltas[i]), durations[i], keys[i])); decode = checked(decode + durations[i]); }
        Check(keys[0], "Fixture must start at a random-access sample");
        var presentation = samplesOutput.Select(s => s.PresentationTicks).Order().ToArray(); long origin = presentation[0];
        Check(count > 1, "Multiple samples required for strict CFR fixture validation");
        long span = checked(presentation[^1] - origin), intervals = count - 1;
        Check(span > 0, "Invalid presentation span");
        for (int rank = 0; rank < count; rank++)
        {
            Check(rank == 0 || presentation[rank] > presentation[rank - 1], "Duplicate presentation timestamp");
            Check(Int128.Abs(((Int128)presentation[rank] - origin) * intervals - (Int128)rank * span) <= intervals,
                "Original source must form a complete CFR grid to one media tick");
            Check(Int128.Abs((Int128)durations[rank] * intervals - span) <= intervals, "Original source has variable frame durations");
        }
        Check(Int128.Abs((Int128)decode * intervals - (Int128)count * span) <= intervals, "Decode duration differs from complete CFR presentation span");
        long defaultDuration = Rounded((Int128)span * 1_000_000_000, (Int128)timescale * intervals);
        Check(defaultDuration > 0, "Invalid CFR duration");
        return new(codec, configuration, width, height, timescale, samplesOutput, defaultDuration);
    }

    static long[] Expand(byte[] bytes, Box box, int samples, bool allowSigned, bool zeroAllowed)
    {
        Need(box.Body, 8, box.End); byte version = bytes[box.Body]; Check(allowSigned ? version is 0 or 1 : version == 0, "Unsupported timing table version");
        uint entries = U32(bytes, box.Body + 4); Check(entries is > 0 && entries <= samples && box.End - box.Body == 8L + entries * 8L, "Timing table count/length mismatch");
        var values = new long[samples]; int output = 0;
        for (int i = 0; i < entries; i++)
        {
            int at = box.Body + 8 + i * 8; uint count = U32(bytes, at);
            long value = allowSigned && version == 1 ? BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at + 4, 4)) : U32(bytes, at + 4);
            Check(count > 0 && count <= samples - output && (zeroAllowed || value > 0), "Invalid timing run");
            Array.Fill(values, value, output, (int)count); output += (int)count;
        }
        Check(output == samples, "Timing table does not cover every sample"); return values;
    }
    static Box[] Boxes(byte[] bytes, int start, int end, bool allowSampleEntryTerminator = false)
    {
        Need(start, end - start, bytes.Length); var boxes = new List<Box>();
        while (start < end)
        {
            // QuickTime sample descriptions may end their extension atoms with a zero uint32 terminator.
            if (allowSampleEntryTerminator && end - start == 4 && U32(bytes, start) == 0) break;
            Check(boxes.Count < 100_000, "Box count limit"); Need(start, 8, end); ulong size = U32(bytes, start); int header = 8;
            if (size == 1) { Need(start, 16, end); size = U64(bytes, start + 8); header = 16; }
            if (size == 0) size = (ulong)(end - start);
            Check(size >= (ulong)header && size <= (ulong)(end - start), "Box size exceeds parent");
            boxes.Add(new(FourCC(bytes, start + 4), start, start + header, start + (int)size)); start += (int)size;
        }
        return boxes.ToArray();
    }
    static Box Child(byte[] bytes, Box parent, string type, int skip = 0) => Boxes(bytes, checked(parent.Body + skip), parent.End).Single(b => b.Type == type);
    static string FourCC(byte[] bytes, int at) => Encoding.ASCII.GetString(bytes, at, 4);
    static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at, 2));
    static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
    static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at, 8));
    static long Rounded(Int128 numerator, Int128 denominator) { Check(numerator >= 0 && denominator > 0, "Invalid rational timestamp"); return checked((long)((numerator + denominator / 2) / denominator)); }
    static long ToNanoseconds(long ticks, uint scale) => Rounded((Int128)ticks * 1_000_000_000, scale);
    static void Need(int at, int count, int end) => Check(at >= 0 && count >= 0 && at <= end && count <= end - at, "Truncated box/table");
    static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException("Native packet fixture: " + message); }

    static byte[] Matroska(DemuxedTrack video, bool includeText = false)
    {
        static byte[] Join(params byte[][] values) => values.SelectMany(v => v).ToArray();
        static byte[] Unsigned(ulong value)
        {
            var bytes = new List<byte>(); bool started = false;
            for (int shift = 56; shift >= 0; shift -= 8) { byte next = (byte)(value >> shift); if (started || next != 0 || shift == 0) { bytes.Add(next); started = true; } }
            return bytes.ToArray();
        }
        static byte[] Element(ulong id, byte[] payload)
        {
            ulong size = (ulong)payload.Length; int width = 1; while (size >= (1UL << (width * 7)) - 1) width++;
            ulong encoded = size | (1UL << (width * 7));
            byte[] vint = Enumerable.Range(0, width).Reverse().Select(i => (byte)(encoded >> (i * 8))).ToArray();
            return Join(Unsigned(id), vint, payload);
        }
        static byte[] U(ulong id, ulong value) => Element(id, Unsigned(value));
        byte[] header = Element(0x1a45dfa3, Join(U(0x42f7, 1), Element(0x4282, Encoding.UTF8.GetBytes("matroska")), U(0x4285, 4)));
        byte[] track = Element(0xae, Join(U(0xd7, 1), U(0x73c5, 1), U(0x83, 1), U(0x9c, 0),
            Element(0x86, Encoding.UTF8.GetBytes(video.CodecId)), Element(0x63a2, video.CodecPrivate), U(0x23e383, (ulong)video.DefaultDurationNanoseconds),
            Element(0xe0, Join(U(0xb0, (ulong)video.Width), U(0xba, (ulong)video.Height)))));
        byte[] TextTrack(ulong id, string codec, string name, bool isDefault) => Element(0xae,
            Join(U(0xd7, id), U(0x73c5, id), U(0x83, 17), U(0x88, isDefault ? 1UL : 0UL), U(0x9c, 0),
                Element(0x86, Encoding.UTF8.GetBytes(codec)), Element(0x536e, Encoding.UTF8.GetBytes(name)),
                Element(0x22b59c, Encoding.UTF8.GetBytes("eng")), Element(0x22b59d, Encoding.UTF8.GetBytes("en"))));
        byte[] entries = includeText ? Join(track, TextTrack(2, "S_TEXT/ASS", "Original ASS", true),
            TextTrack(3, "S_TEXT/UTF8", "Original UTF-8", false)) : track;
        var body = new MemoryStream(); body.Write(Element(0x1549a966, U(0x2ad7b1, 1))); body.Write(Element(0x1654ae6b, entries));
        foreach (var packet in video.Packets)
        {
            Check(packet.TimestampNanoseconds >= 0, "Fixture cluster timestamp must be nonnegative");
            // One Cluster per coded packet keeps relative timestamps exactly zero.
            // Cluster PTS may decrease for B-frames; physical order remains coding order.
            body.Write(Element(0x1f43b675, Join(U(0xe7, (ulong)packet.TimestampNanoseconds),
                Element(0xa3, Join([0x81, 0, 0, (byte)(packet.Keyframe ? 0x80 : 0)], packet.Data)))));
        }
        if (includeText)
        {
            byte[] Group(byte id, string text) => Element(0xa0, Join(Element(0xa1,
                Join([(byte)(0x80 | id), 0, 0, 0], Encoding.UTF8.GetBytes(text))), U(0x9b, 1_000_000_000)));
            body.Write(Element(0x1f43b675, Join(U(0xe7, 500_000_000),
                Group(2, "0,0,Default,,0,0,0,,{\\i1}Original\\Ncaption"), Group(3, "<Naruto> 2 < 3 and 4 > 1"))));
        }
        return Join(header, Element(0x18538067, body.ToArray()));
    }

    static void MalformedTables(byte[] source)
    {
        void Reject(byte[] invalid)
        {
            try { _ = Parse(invalid); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Malformed native fixture table was accepted.");
        }
        Box moov = Boxes(source, 0, source.Length).Single(b => b.Type == "moov"), track = Boxes(source, moov.Body, moov.End).First(b => b.Type == "trak");
        Box table = Child(source, Child(source, Child(source, track, "mdia"), "minf"), "stbl");
        byte[] invalid = (byte[])source.Clone(); Box sizes = Child(source, table, "stsz"); BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(sizes.Body + 8, 4), uint.MaxValue); Reject(invalid);
        invalid = (byte[])source.Clone(); Box timing = Child(source, table, "stts"); BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(timing.Body + 12, 4), 0); Reject(invalid);
        invalid = (byte[])source.Clone(); Box map = Child(source, table, "stsc"); BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(map.Body + 8, 4), 0); Reject(invalid);
        invalid = (byte[])source.Clone(); Box offsets = Boxes(source, table.Body, table.End).Single(b => b.Type is "stco" or "co64");
        if (offsets.Type == "co64") BinaryPrimitives.WriteUInt64BigEndian(invalid.AsSpan(offsets.Body + 8, 8), ulong.MaxValue);
        else BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(offsets.Body + 8, 4), uint.MaxValue);
        Reject(invalid); Reject(source[..^1]);
    }
}
