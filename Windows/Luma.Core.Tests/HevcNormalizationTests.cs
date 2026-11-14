using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Luma.Core.Containers;

namespace Luma.Core.Tests;

public static class HevcNormalizationTests
{
    public static void Run(string fixtures)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(fixtures, "Luma-HEVC10-AAC51.mp4"));
        var entries = Entries(original); var entry = entries.Single(e => e.Type == "hev1");
        byte[] normalized = Convert(original);
        Check(normalized.Length == original.Length && entries.Count == Entries(normalized).Count, "Whole-file length/track descriptions retained");
        byte[] expected = (byte[])original.Clone(); "hvc1"u8.CopyTo(expected.AsSpan(entry.Header + 4, 4));
        Check(expected.SequenceEqual(normalized), "Only the four-byte sample-entry type may change");
        var unpatched = (byte[])normalized.Clone(); "hev1"u8.CopyTo(unpatched.AsSpan(entry.Header + 4, 4));
        Check(SHA256.HashData(unpatched).SequenceEqual(SHA256.HashData(original)), "Packet/timing/audio/caption/metadata bytes unchanged");
        using (var input = new MemoryStream(normalized))
        using (var output = new MemoryStream())
        { input.Position = 11; Check(!HevcSampleEntryNormalizer.Normalize(input, output) && input.Position == 11 && output.Length == 0, "Already-hvc1 is a position-preserving empty-output no-op"); }
        using (var input = File.OpenRead(Path.Combine(fixtures, "Luma-Demo.mp4")))
        using (var output = new MemoryStream())
            Check(!HevcSampleEntryNormalizer.Normalize(input, output) && output.Length == 0, "AVC is not copied or retagged");
        var export = Environment.GetEnvironmentVariable("LUMA_WINDOWS_MEDIA_EXPORT");
        if (!string.IsNullOrWhiteSpace(export))
        { Directory.CreateDirectory(export); File.WriteAllBytes(Path.Combine(export, "Luma-normalized-hevc10-aac51.mp4"), normalized); }
        Console.WriteLine("PASS: original hev1 Main10/AAC5.1 MP4 normalizes to hvc1 with exact whole-file equality except the sample-entry fourcc; audio, text, timing and encoded packets untouched");

        foreach (int prefix in new[] { 1, 2, 4 })
        {
            byte[] source = Synthetic(prefix); byte[] result = Convert(source);
            var video = Entries(source).Single(); byte[] wanted = (byte[])source.Clone(); "hvc1"u8.CopyTo(wanted.AsSpan(video.Header + 4, 4));
            Check(result.SequenceEqual(wanted), "NAL prefix width and co64 chunk mapping are retained");
        }
        byte[] valid = Synthetic(4); Box hvc = Configuration(valid), table = Table(valid);
        byte[] bad = (byte[])valid.Clone(); Box mdat = Boxes(valid, 0, valid.Length).Single(b => b.Type == "mdat");
        bad[mdat.Start + 4] = 0x40; Reject<NotSupportedException>(bad); // in-band VPS, no rewriting allowed
        bad = (byte[])valid.Clone(); bad[hvc.Start + 23] &= 0x7f; Reject<NotSupportedException>(bad);
        bad = (byte[])valid.Clone(); int pps = LastArray(bad, hvc); bad[pps] = 0x80 | 39; bad[pps + 5] = 39 << 1; Reject<NotSupportedException>(bad);
        bad = (byte[])valid.Clone(); bad[hvc.Start + 21] = (byte)((bad[hvc.Start + 21] & 0xfc) | 2); Reject<NotSupportedException>(bad);
        bad = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt16BigEndian(bad.AsSpan(hvc.Start + 26, 2), ushort.MaxValue); Reject<InvalidDataException>(bad);
        bad = (byte[])valid.Clone(); bad[mdat.Start + 4] |= 0x80; Reject<InvalidDataException>(bad);
        bad = (byte[])valid.Clone(); bad[mdat.Start + 5] = 0; Reject<InvalidDataException>(bad);
        bad = (byte[])valid.Clone(); bad[mdat.Start + 4] |= 1; Reject<NotSupportedException>(bad);
        bad = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(mdat.Start, 4), uint.MaxValue); Reject<InvalidDataException>(bad);
        bad = (byte[])valid.Clone(); Box sizes = Child(valid, table, "stsz"); BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(sizes.Start + 8, 4), uint.MaxValue); Reject<NotSupportedException>(bad);
        bad = (byte[])valid.Clone(); Box offsets = Child(valid, table, "co64"); BinaryPrimitives.WriteUInt64BigEndian(bad.AsSpan(offsets.Start + 8, 8), ulong.MaxValue); Reject<InvalidDataException>(bad);
        bad = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt64BigEndian(bad.AsSpan(offsets.Start + 16, 8), (ulong)mdat.Start); Reject<InvalidDataException>(bad);
        bad = (byte[])valid.Clone(); Box map = Child(valid, table, "stsc"); BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(map.Start + 8, 4), 0); Reject<InvalidDataException>(bad);
        bad = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(map.Start + 16, 4), 2); Reject<NotSupportedException>(bad);
        bad = (byte[])valid.Clone(); Box reference = References(valid).Single(); BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(reference.Start, 4), 0); Reject<NotSupportedException>(bad);
        bad = Synthetic(4, true); Box second = Entries(bad)[1]; "encv"u8.CopyTo(bad.AsSpan(second.Header + 4, 4)); Reject<NotSupportedException>(bad);
        Reject<NotSupportedException>(DuplicateDescription(valid));
        Reject<NotSupportedException>(valid.Concat(BoxBytes("moof", [])).ToArray());
        Reject<NotSupportedException>(valid.Concat(BoxBytes("pssh", [])).ToArray());
        Reject<InvalidDataException>(valid[..^1]); Reject<InvalidDataException>(valid.Concat(new byte[] { 0 }).ToArray());
        using (var input = new MemoryStream(valid))
        using (var occupied = new MemoryStream())
        { occupied.WriteByte(7); Reject<NotSupportedException>(() => HevcSampleEntryNormalizer.Normalize(input, occupied)); Check(occupied.ToArray().SequenceEqual(new byte[] { 7 }), "Existing output preserved"); }
        using (var same = new MemoryStream(valid))
        { Reject<NotSupportedException>(() => HevcSampleEntryNormalizer.Normalize(same, same)); Check(same.ToArray().SequenceEqual(valid), "Aliased input/output preserved"); }
        using (var input = new MemoryStream(valid))
        using (var output = new MemoryStream())
        { Reject<OperationCanceledException>(() => HevcSampleEntryNormalizer.Normalize(input, output, new CancellationToken(true))); Check(output.Length == 0, "Pre-cancel preserves empty output"); }
        using (var cancel = new CancellationTokenSource())
        using (var input = new CancelReadStream(valid, cancel))
        using (var output = new MemoryStream())
        { Reject<OperationCanceledException>(() => HevcSampleEntryNormalizer.Normalize(input, output, cancel.Token)); Check(output.Length == 0 && input.Position == 0, "Validation cancellation preserves output/input position"); }
        using (var cancel = new CancellationTokenSource())
        using (var input = new MemoryStream(valid))
        using (var output = new CancelWriteStream(cancel))
        { Reject<OperationCanceledException>(() => HevcSampleEntryNormalizer.Normalize(input, output, cancel.Token)); Check(output.Length == 0 && output.Position == 0, "Copy cancellation removes partial output"); }
        using (var input = new MemoryStream(valid))
        using (var output = new FailedWriteStream())
        { Reject<IOException>(() => HevcSampleEntryNormalizer.Normalize(input, output)); Check(output.Length == 0 && output.Position == 0, "Copy failure removes partial output"); }
        Console.WriteLine("PASS: 1/2/4-byte NAL prefixes; unsafe in-band/incomplete/missing/layered/encrypted/fragmented/external/changing descriptions reject before writing; malformed configuration, extents, lacing-free sample tables and cancellation/copy rollback bounds");
    }

    static byte[] Convert(byte[] source)
    {
        using var input = new MemoryStream(source); using var output = new MemoryStream(); input.Position = 7;
        Check(HevcSampleEntryNormalizer.Normalize(input, output) && input.Position == 7 && output.Position == output.Length, "Successful normalize preserves source position and completes output"); return output.ToArray();
    }
    static byte[] Synthetic(int prefix, bool twoTracks = false)
    {
        byte[] h = new byte[23]; h[0] = 1; h[1] = 1; h[12] = 90; h[13] = 0xf0; h[15] = 0xfc; h[16] = 0xfd; h[17] = h[18] = 0xf8; h[21] = (byte)(prefix - 1); h[22] = 3;
        var configuration = new List<byte>(h);
        foreach (int type in new[] { 32, 33, 34 }) configuration.AddRange(new byte[] { (byte)(0x80 | type), 0, 1, 0, 2, (byte)(type << 1), 1 });
        byte[] Nal(byte seed)
        { byte[] data = new byte[prefix + 3]; data[prefix - 1] = 3; data[prefix] = 0x26; data[prefix + 1] = 1; data[prefix + 2] = seed; return data; }
        Mp4Sample[] packets = [new(Nal(1), 0, 0, 40_000_000, true), new(Nal(2), 40_000_000, 40_000_000, 40_000_000, false), new(Nal(3), 80_000_000, 80_000_000, 40_000_000, false)];
        var track = new Mp4Track(1, "V_MPEGH/ISO/HEVC", configuration.ToArray(), 32, 24, 0, 0, packets);
        using var output = new MemoryStream(); IsoMediaMuxer.Write(output, twoTracks ? [track, track with { Id = 2 }] : [track]); byte[] file = output.ToArray();
        Box first = Entries(file).First(); "hev1"u8.CopyTo(file.AsSpan(first.Header + 4, 4)); return file;
    }
    static void Reject<T>(byte[] source) where T : Exception
    {
        using var input = new MemoryStream(source); using var output = new MemoryStream();
        Reject<T>(() => HevcSampleEntryNormalizer.Normalize(input, output)); Check(output.Length == 0, "Rejected input wrote output");
    }
    static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name + " from HEVC normalization."); }
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("HEVC normalization test: " + message); }
    readonly record struct Box(string Type, int Header, int Start, int End);
    static List<Box> Boxes(byte[] data, int start, int end)
    {
        var boxes = new List<Box>(); while (start < end)
        {
            Check(end - start >= 8, "Test fixture box bound"); uint size = U32(data, start); int header = 8;
            if (size == 1) { size = checked((uint)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(start + 8))); header = 16; }
            if (size == 0) size = (uint)(end - start);
            Check(size >= header && size <= end - start, "Test fixture box extent"); boxes.Add(new(Encoding.ASCII.GetString(data, start + 4, 4), start, start + header, start + (int)size)); start += (int)size;
        }
        return boxes;
    }
    static Box Child(byte[] data, Box parent, string name) => Boxes(data, parent.Start, parent.End).Single(b => b.Type == name);
    static Box Movie(byte[] data) => Boxes(data, 0, data.Length).Single(b => b.Type == "moov");
    static Box Track(byte[] data) { Box m = Movie(data); return Boxes(data, m.Start, m.End).First(b => b.Type == "trak"); }
    static Box Table(byte[] data) => Child(data, Child(data, Child(data, Track(data), "mdia"), "minf"), "stbl");
    static List<Box> Entries(byte[] data)
    {
        Box movie = Movie(data); var output = new List<Box>();
        foreach (Box track in Boxes(data, movie.Start, movie.End).Where(b => b.Type == "trak"))
        { Box table = Child(data, Child(data, Child(data, track, "mdia"), "minf"), "stbl"), descriptions = Child(data, table, "stsd"); output.AddRange(Boxes(data, descriptions.Start + 8, descriptions.End)); }
        return output;
    }
    static Box Configuration(byte[] data) { Box e = Entries(data).First(); return Boxes(data, e.Start + 78, e.End).Single(b => b.Type == "hvcC"); }
    static List<Box> References(byte[] data)
    { Box minf = Child(data, Child(data, Track(data), "mdia"), "minf"), dref = Child(data, Child(data, minf, "dinf"), "dref"); return Boxes(data, dref.Start + 8, dref.End); }
    static int LastArray(byte[] data, Box hvc)
    {
        int cursor = hvc.Start + 23, last = cursor;
        for (int i = 0; i < data[hvc.Start + 22]; i++)
        { last = cursor; int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cursor + 1)); cursor += 3; for (int n = 0; n < count; n++) { int size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cursor)); cursor += 2 + size; } }
        return last;
    }
    static byte[] DuplicateDescription(byte[] data)
    {
        Box movie = Movie(data), track = Track(data), mdia = Child(data, track, "mdia"), minf = Child(data, mdia, "minf"), table = Child(data, minf, "stbl"), stsd = Child(data, table, "stsd"), entry = Entries(data).Single();
        byte[] copy = new byte[data.Length + entry.End - entry.Header]; data.AsSpan(0, stsd.End).CopyTo(copy); data.AsSpan(entry.Header, entry.End - entry.Header).CopyTo(copy.AsSpan(stsd.End)); data.AsSpan(stsd.End).CopyTo(copy.AsSpan(stsd.End + entry.End - entry.Header));
        foreach (Box box in new[] { movie, track, mdia, minf, table, stsd }) BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(box.Header, 4), checked(U32(data, box.Header) + (uint)(entry.End - entry.Header)));
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(stsd.Start + 4, 4), 2); return copy;
    }
    static byte[] BoxBytes(string name, byte[] payload)
    { byte[] bytes = new byte[payload.Length + 8]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length); Encoding.ASCII.GetBytes(name).CopyTo(bytes, 4); payload.CopyTo(bytes, 8); return bytes; }
    static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));
    sealed class CancelReadStream(byte[] data, CancellationTokenSource cancellation) : MemoryStream(data)
    {
        int reads;
        public override int Read(Span<byte> buffer) { int count = base.Read(buffer); if (++reads == 4) cancellation.Cancel(); return count; }
    }
    sealed class CancelWriteStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { base.Write(buffer, offset, count); cancellation.Cancel(); }
    }
    sealed class FailedWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { base.Write(buffer, offset, Math.Min(3, count)); throw new IOException("Original synthetic output failure"); }
    }
}
