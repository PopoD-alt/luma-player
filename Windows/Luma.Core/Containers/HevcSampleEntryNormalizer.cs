using System.Buffers.Binary;
using System.Text;

namespace Luma.Core.Containers;

/// <summary>
/// Original conservative, seek-based hev1 to hvc1 compatibility copy. No codec
/// decoding or media transcoding: only the validated sample-entry fourcc changes.
/// hvc1 requires parameter sets in the sample description, never video samples:
/// https://mp4ra.org/registered-types/codecs
/// https://developer.apple.com/streaming/hls-authoring-specification-for-apple-devices.html
/// Caller owns independent streams and must keep input unchanged during the call.
/// </summary>
public static class HevcSampleEntryNormalizer
{
    public const int MaximumSamples = 1_000_000;
    public const long MaximumMetadataBytes = 64 * 1024 * 1024;
    const long MaximumFileBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>
    /// Returns false without writing when no hev1 entry exists. Unsafe variants
    /// throw NotSupportedException; malformed bounds/framing throw InvalidDataException.
    /// Validation finishes before output is written. Output must be empty, writable
    /// and seekable; partial output is truncated on cancellation/copy failure.
    /// Input position is preserved, and a successful output is positioned at its end.
    /// </summary>
    public static bool Normalize(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(input, output)) throw new NotSupportedException("Normalization requires independent input and output streams.");
        if (!input.CanRead || !input.CanSeek || !output.CanWrite || !output.CanSeek)
            throw new NotSupportedException("Normalization requires seekable input and empty writable seekable output.");
        if (output.Length != 0 || output.Position != 0) throw new NotSupportedException("Normalization preserves existing output; provide an empty stream.");
        if (input is FileStream sourceFile && output is FileStream destinationFile
            && string.Equals(Path.GetFullPath(sourceFile.Name), Path.GetFullPath(destinationFile.Name),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new NotSupportedException("Input and output must be different files.");
        long originalPosition = input.Position; bool writing = false;
        try
        {
            var reader = new Reader(input, cancellationToken);
            List<long> patches = reader.Validate();
            if (patches.Count == 0) return false;
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Length != reader.FileLength) throw new IOException("Input changed during normalization validation.");
            input.Position = 0; writing = true;
            byte[] buffer = new byte[262_144]; long remaining = reader.FileLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) throw new InvalidDataException("Input was truncated during compatibility copy.");
                cancellationToken.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); remaining -= read;
            }
            foreach (long patch in patches)
            {
                cancellationToken.ThrowIfCancellationRequested(); output.Position = patch; output.Write("hvc1"u8);
            }
            cancellationToken.ThrowIfCancellationRequested(); output.Position = output.Length; return true;
        }
        catch (OverflowException error) { Rollback(); throw new InvalidDataException("HEVC container metadata exceeds integer bounds.", error); }
        catch (EndOfStreamException error) { Rollback(); throw new InvalidDataException("Truncated HEVC sample data.", error); }
        catch { Rollback(); throw; }
        finally { input.Position = originalPosition; }

        void Rollback()
        {
            if (!writing) return;
            try { output.SetLength(0); output.Position = 0; }
            catch (Exception error) when (error is IOException or NotSupportedException or ObjectDisposedException) { }
        }
    }

    readonly record struct Box(string Type, long Header, long Start, long End) { public long Length => End - Start; }
    sealed record Track(List<Box> MediaInfo, List<Box> Tables, List<Box> Entries);
    readonly record struct ChunkMap(uint First, uint Samples, uint Description);
    sealed class Reader(Stream input, CancellationToken token)
    {
        public long FileLength { get; } = input.Length;
        readonly List<(long Start, long End)> mediaRanges = [];
        long metadataRead;
        int boxes, sampleCount, nalCount;

        public List<long> Validate()
        {
            if (FileLength <= 0) throw new InvalidDataException("Empty ISO media file.");
            if (FileLength > MaximumFileBytes) throw new NotSupportedException("Compatibility normalization exceeds the 64 GiB file limit.");
            var top = Children(0, FileLength);
            Box moov = One(top, "moov"); var movie = Children(moov.Start, moov.End);
            var tracks = new List<Track>(); var patches = new List<long>();
            foreach (Box track in movie.Where(b => b.Type == "trak"))
            {
                token.ThrowIfCancellationRequested();
                if (tracks.Count >= 128) throw new NotSupportedException("Normalization track count exceeds 128.");
                var parts = Children(track.Start, track.End); Box mdia = One(parts, "mdia");
                var media = Children(mdia.Start, mdia.End); Box minf = One(media, "minf");
                var info = Children(minf.Start, minf.End); Box stbl = One(info, "stbl");
                var tables = Children(stbl.Start, stbl.End); Box stsd = One(tables, "stsd");
                byte[] header = Prefix(stsd, 8); if (U32(header, 0) != 0) throw new NotSupportedException("Unsupported sample-description version/flags.");
                uint count = U32(header, 4); if (count is < 1 or > 64) throw new NotSupportedException("Sample-description count exceeds 1–64.");
                var entries = Children(stsd.Start + 8, stsd.End);
                if (entries.Count != count) throw new InvalidDataException("Sample-description count disagrees with entry bounds.");
                tracks.Add(new(info, tables, entries));
                foreach (Box entry in entries.Where(e => e.Type == "hev1")) patches.Add(entry.Header + 4);
            }
            if (patches.Count == 0) return [];
            if (top.Any(b => b.Type is "moof" or "pssh") || movie.Any(b => b.Type is "mvex" or "pssh"))
                throw new NotSupportedException("Fragmented or protected media is not normalized.");
            foreach (Box mdat in top.Where(b => b.Type == "mdat")) mediaRanges.Add((mdat.Start, mdat.End));
            if (mediaRanges.Count == 0) throw new InvalidDataException("HEVC media payload is missing.");
            foreach (Track track in tracks)
            {
                token.ThrowIfCancellationRequested();
                if (track.Entries.Count != 1) throw new NotSupportedException("Changing or mixed sample descriptions are not normalized.");
                if (track.Entries.Any(e => e.Type is "encv" or "enca" or "enct") || track.Tables.Any(b => b.Type is "senc" or "saiz" or "saio"))
                    throw new NotSupportedException("Encrypted/protected sample entries are not normalized.");
                int references = References(track.MediaInfo);
                foreach (Box entry in track.Entries)
                {
                    byte[] head = Prefix(entry, 8); ushort reference = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6));
                    if (reference == 0 || reference > references) throw new InvalidDataException("Sample entry references an unavailable data source.");
                    if (entry.Type == "hev1") ValidateVideo(track.Tables, entry);
                }
            }
            token.ThrowIfCancellationRequested(); return patches;
        }
        int References(List<Box> mediaInfo)
        {
            Box dinf = One(mediaInfo, "dinf"); Box dref = One(Children(dinf.Start, dinf.End), "dref");
            byte[] header = Prefix(dref, 8); var refs = Children(dref.Start + 8, dref.End);
            if (U32(header, 0) != 0 || U32(header, 4) != refs.Count || refs.Count is < 1 or > 64)
                throw new InvalidDataException("Invalid local data-reference table.");
            foreach (Box reference in refs)
                if (reference.Type is not ("url " or "alis") || reference.Length != 4 || U32(Prefix(reference, 4), 0) != 1)
                    throw new NotSupportedException("External/non-self-contained data references are not normalized.");
            return refs.Count;
        }
        void ValidateVideo(List<Box> tables, Box entry)
        {
            byte[] visual = Prefix(entry, 78); uint width = BinaryPrimitives.ReadUInt16BigEndian(visual.AsSpan(24)), height = BinaryPrimitives.ReadUInt16BigEndian(visual.AsSpan(26));
            if (width is 0 or > 8192 || height is 0 or > 8192 || width * height > 16_777_216)
                throw new InvalidDataException("Invalid/oversized HEVC video dimensions.");
            var extensions = Children(entry.Start + 78, entry.End, true);
            if (extensions.Any(b => b.Type == "sinf")) throw new NotSupportedException("Protected HEVC descriptions are not normalized.");
            Box hvcc = One(extensions, "hvcC"); if (hvcc.Length > 1_048_576) throw new NotSupportedException("HEVC configuration exceeds 1 MiB.");
            int prefix = Configuration(Payload(hvcc));
            byte[] sizes = Payload(One(tables, "stsz")); Need(sizes, 0, 12);
            uint fixedSize = U32(sizes, 4), count = U32(sizes, 8);
            if (count is < 1 or > MaximumSamples || count > MaximumSamples - sampleCount) throw new NotSupportedException("HEVC sample count exceeds normalization limits.");
            if (U32(sizes, 0) != 0 || sizes.Length != 12L + (fixedSize == 0 ? count * 4L : 0)) throw new InvalidDataException("Invalid HEVC sample-size table.");
            sampleCount += (int)count;
            Box[] offsetBoxes = tables.Where(b => b.Type is "stco" or "co64").ToArray();
            if (offsetBoxes.Length != 1) throw new InvalidDataException("HEVC requires exactly one chunk-offset table.");
            byte[] offsets = Payload(offsetBoxes[0]); Need(offsets, 0, 8); uint chunks = U32(offsets, 4); int stride = offsetBoxes[0].Type == "co64" ? 8 : 4;
            if (U32(offsets, 0) != 0 || chunks < 1 || chunks > count || offsets.Length != 8L + chunks * (long)stride)
                throw new InvalidDataException("Invalid HEVC chunk-offset table.");
            byte[] maps = Payload(One(tables, "stsc")); Need(maps, 0, 8); uint mapCount = U32(maps, 4);
            if (U32(maps, 0) != 0 || mapCount < 1 || mapCount > chunks || maps.Length != 8L + mapCount * 12L)
                throw new InvalidDataException("Invalid HEVC sample-to-chunk table.");
            var mapping = new ChunkMap[mapCount];
            for (int i = 0; i < mapping.Length; i++)
            {
                int at = 8 + i * 12; var map = new ChunkMap(U32(maps, at), U32(maps, at + 4), U32(maps, at + 8));
                if (map.First == 0 || map.First > chunks || i == 0 && map.First != 1 || i > 0 && map.First <= mapping[i - 1].First || map.Samples == 0 || map.Samples > count || map.Description == 0)
                    throw new InvalidDataException("Invalid HEVC chunk mapping run.");
                if (map.Description != 1) throw new NotSupportedException("Changing sample descriptions are not normalized."); mapping[i] = map;
            }
            var extents = new List<(long Start, long End)>((int)chunks); uint sample = 0; int run = 0;
            for (uint chunk = 1; chunk <= chunks; chunk++)
            {
                token.ThrowIfCancellationRequested(); if (run + 1 < mapping.Length && chunk >= mapping[run + 1].First) run++;
                if (mapping[run].Samples > count - sample) throw new InvalidDataException("Chunk mapping exceeds available samples.");
                int at = 8 + ((int)chunk - 1) * stride; ulong raw = stride == 8 ? U64(offsets, at) : U32(offsets, at);
                if (raw > (ulong)FileLength) throw new InvalidDataException("HEVC chunk offset exceeds the file."); long position = (long)raw, beginning = position;
                for (uint n = 0; n < mapping[run].Samples; n++, sample++)
                {
                    uint size = fixedSize == 0 ? U32(sizes, 12 + (int)sample * 4) : fixedSize;
                    if (size == 0) throw new InvalidDataException("Empty HEVC access unit.");
                    if (size > 67_108_864) throw new NotSupportedException("HEVC access unit exceeds 64 MiB.");
                    if (!InsideMedia(position, size)) throw new InvalidDataException("HEVC sample lies outside local mdat payloads.");
                    ValidateSample(position, size, prefix); position += size;
                }
                extents.Add((beginning, position));
            }
            if (sample != count) throw new InvalidDataException("Chunk mapping does not cover every HEVC sample.");
            extents.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 1; i < extents.Count; i++)
                if (extents[i].Start < extents[i - 1].End) throw new InvalidDataException("Overlapping HEVC chunk extents.");
        }
        int Configuration(byte[] data)
        {
            if (data.Length < 23 || data[0] != 1 || (data[13] & 0xf0) != 0xf0 || (data[15] & 0xfc) != 0xfc
                || (data[16] & 0xfc) != 0xfc || (data[17] & 0xf8) != 0xf8 || (data[18] & 0xf8) != 0xf8)
                throw new InvalidDataException("Invalid HEVC decoder configuration/reserved fields.");
            int prefix = (data[21] & 3) + 1; if (prefix == 3) throw new NotSupportedException("Three-byte HEVC NAL lengths are not normalized.");
            int cursor = 23, total = 0; var required = new HashSet<int>();
            for (int array = 0; array < data[22]; array++)
            {
                token.ThrowIfCancellationRequested(); Need(data, cursor, 3); byte descriptor = data[cursor++];
                if ((descriptor & 0x40) != 0) throw new InvalidDataException("Invalid hvcC array reserved field.");
                if ((descriptor & 0x80) == 0) throw new NotSupportedException("Incomplete HEVC parameter arrays cannot become hvc1.");
                int type = descriptor & 63, count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cursor)); cursor += 2;
                total += count; if (total > 1024) throw new NotSupportedException("HEVC configuration exceeds 1024 NAL units.");
                if (type is 32 or 33 or 34 && count > 0) required.Add(type);
                for (int n = 0; n < count; n++)
                {
                    Need(data, cursor, 2); int size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cursor)); cursor += 2; Need(data, cursor, size);
                    if (size < 2 || HeaderType(data[cursor], data[cursor + 1]) != type) throw new InvalidDataException("Invalid HEVC configuration NAL type/length."); cursor += size;
                }
            }
            if (cursor != data.Length) throw new InvalidDataException("Trailing or truncated HEVC configuration data.");
            if (required.Count != 3) throw new NotSupportedException("hvc1 normalization requires complete VPS, SPS and PPS arrays."); return prefix;
        }
        void ValidateSample(long position, uint count, int prefix)
        {
            long end = position + count; Span<byte> header = stackalloc byte[6]; bool codedPicture = false;
            while (position < end)
            {
                token.ThrowIfCancellationRequested(); if (++nalCount > 4_000_000) throw new NotSupportedException("HEVC NAL-count normalization limit exceeded.");
                if (end - position < prefix + 2) throw new InvalidDataException("Truncated HEVC NAL length/header.");
                input.Position = position; input.ReadExactly(header[..(prefix + 2)]); uint size = 0;
                for (int i = 0; i < prefix; i++) size = (size << 8) | header[i];
                if (size < 2 || size > end - position - prefix) throw new InvalidDataException("HEVC NAL exceeds its sample.");
                int type = HeaderType(header[prefix], header[prefix + 1]);
                if (type is 32 or 33 or 34) throw new NotSupportedException("In-band VPS/SPS/PPS must remain hev1; no header-only normalization was written.");
                if (type is 48 or 49) throw new NotSupportedException("HEVC extractors/aggregators are not normalized.");
                codedPicture |= type <= 31; position += prefix + size;
            }
            if (!codedPicture) throw new NotSupportedException("A HEVC sample without a coded picture is not normalized.");
        }
        static int HeaderType(byte first, byte second)
        {
            if ((first & 128) != 0 || (second & 7) == 0) throw new InvalidDataException("Invalid HEVC NAL header.");
            if (((first & 1) << 5 | second >> 3) != 0) throw new NotSupportedException("Multilayer HEVC is not normalized.");
            return (first >> 1) & 63;
        }
        bool InsideMedia(long start, uint length)
        {
            int low = 0, high = mediaRanges.Count;
            while (low < high) { int middle = low + (high - low) / 2; if (mediaRanges[middle].Start <= start) low = middle + 1; else high = middle; }
            return low > 0 && start >= mediaRanges[low - 1].Start && start <= mediaRanges[low - 1].End && length <= mediaRanges[low - 1].End - start;
        }
        List<Box> Children(long start, long end, bool quickTimeTerminator = false)
        {
            var result = new List<Box>();
            for (long cursor = start; cursor < end;)
            {
                token.ThrowIfCancellationRequested(); if (++boxes > 100_000) throw new NotSupportedException("ISO metadata exceeds 100,000 boxes.");
                if (quickTimeTerminator && end - cursor == 4 && U32(Read(cursor, 4), 0) == 0) break;
                if (cursor < 0 || end > FileLength || end - cursor < 8) throw new InvalidDataException("Truncated ISO box header.");
                byte[] header = Read(cursor, 8); ulong size = U32(header, 0); int width = 8;
                if (size == 1) { if (end - cursor < 16) throw new InvalidDataException("Truncated extended ISO box header."); size = U64(Read(cursor + 8, 8), 0); width = 16; }
                if (size == 0) size = (ulong)(end - cursor);
                if (size < (ulong)width || size > (ulong)(end - cursor)) throw new InvalidDataException("ISO box exceeds parent/file bounds.");
                long next = cursor + (long)size; result.Add(new(Encoding.ASCII.GetString(header, 4, 4), cursor, cursor + width, next)); cursor = next;
            }
            return result;
        }
        static Box One(List<Box> boxes, string name)
        {
            var matches = boxes.Where(b => b.Type == name).Take(2).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Missing or duplicate ISO " + name + " box."); return matches[0];
        }
        byte[] Prefix(Box box, int count) { if (box.Length < count) throw new InvalidDataException("Truncated ISO " + box.Type + " metadata."); return Read(box.Start, count); }
        byte[] Payload(Box box)
        {
            if (box.Length > MaximumMetadataBytes) throw new NotSupportedException("ISO metadata atom exceeds 64 MiB."); return Read(box.Start, (int)box.Length);
        }
        byte[] Read(long position, int count)
        {
            token.ThrowIfCancellationRequested();
            if (position < 0 || count < 0 || position > FileLength || count > FileLength - position) throw new InvalidDataException("ISO metadata read exceeds file bounds.");
            metadataRead += count; if (metadataRead > MaximumMetadataBytes) throw new NotSupportedException("Normalization metadata reads exceed 64 MiB.");
            byte[] bytes = new byte[count]; input.Position = position;
            try { input.ReadExactly(bytes); } catch (EndOfStreamException e) { throw new InvalidDataException("Truncated ISO metadata.", e); } return bytes;
        }
        static void Need(byte[] data, int position, int count)
        { if (position < 0 || count < 0 || position > data.Length || count > data.Length - position) throw new InvalidDataException("Truncated HEVC metadata."); }
        static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));
        static ulong U64(byte[] data, int at) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at, 8));
    }
}
