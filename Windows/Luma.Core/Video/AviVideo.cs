using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Luma.Core.Video;

/// <summary>Original bounded RIFF AVI reader. Top-down straight RGBA8 output; video only.
/// Ported from Luma's own Swift algorithms, without a native compressed-codec dependency.</summary>
public sealed class AviVideo
{
    private const int MaximumInput = 536_870_912;
    private readonly byte[] data;
    private readonly Slice[] frames;
    private readonly int bits;
    private readonly uint compression;
    private readonly bool bottomUp;
    private readonly (byte R, byte G, byte B)[] palette;
    private readonly object cacheLock = new();
    private MSVideo1Decoder.Frame? cached;
    private int cachedIndex = -1;
    private readonly HashSet<int> keyframes = new();
    public int Width { get; }
    public int Height { get; }
    public int FrameCount => frames.Length;
    public double FramesPerSecond { get; }
    public double Duration => FrameCount / FramesPerSecond;
    public string CodecName { get; }
    private bool IsVideo1 => IsVideo1Compression(compression);
    private static bool IsVideo1Compression(uint value) => value is 0x4d415243 or 0x6d617263 or 0x4356534d or 0x6376736d or 0x4d414857 or 0x6d616877;

    private AviVideo(byte[] source, Slice[] frames, int width, int height, int bits, uint compression,
        bool bottomUp, double fps, (byte, byte, byte)[] palette)
    {
        data = source; this.frames = frames; Width = width; Height = height; this.bits = bits;
        this.compression = compression; this.bottomUp = bottomUp; FramesPerSecond = fps; this.palette = palette;
        CodecName = IsVideo1 ? $"Luma Microsoft Video 1 · {bits}-bit · video only"
            : compression is 0x47504a4d or 0x4745504a ? "Luma baseline MJPEG · video only"
            : compression == 1 ? "Luma RLE8 AVI · video only" : "Luma RGB AVI · video only";
    }

    public static AviVideo Open(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data); cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > MaximumInput) throw new NotSupportedException("AVI input exceeds the 512 MiB limit.");
        // Own an immutable snapshot: caller mutations cannot corrupt seek checkpoints.
        byte[] source = (byte[])data.Clone();
        var reader = new RiffReader(source, cancellationToken);
        if (reader.FourCC(0) != "RIFF" || reader.FourCC(8) != "AVI ") throw new InvalidDataException("Not a RIFF AVI file.");
        uint declared = reader.U32(4);
        if (declared > source.Length - 8 || declared < 4) throw new InvalidDataException("AVI RIFF extent exceeds input.");
        int end = (int)declared + 8;
        if (end != source.Length) throw new NotSupportedException("Multiple RIFF/OpenDML AVIX segments or trailing AVI data are not supported.");
        var top = reader.Chunks(12, end);
        var header = top.FirstOrDefault(c => c.Id == "LIST" && c.Length >= 4 && reader.FourCC(c.Offset) == "hdrl");
        var movie = top.FirstOrDefault(c => c.Id == "LIST" && c.Length >= 4 && reader.FourCC(c.Offset) == "movi");
        if (header.Length == 0 || movie.Length == 0) throw new InvalidDataException("AVI header/movie lists are missing.");
        var streams = reader.Chunks(header.Offset + 4, header.End).Where(c => c.Id == "LIST" && c.Length >= 4 && reader.FourCC(c.Offset) == "strl").ToArray();
        Slice stream = default, format = default; int selected = -1;
        for (int index = 0; index < streams.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunks = reader.Chunks(streams[index].Offset + 4, streams[index].End);
            var sh = chunks.FirstOrDefault(c => c.Id == "strh"); var sf = chunks.FirstOrDefault(c => c.Id == "strf");
            if (sh.Length >= 48 && reader.FourCC(sh.Offset) == "vids")
            {
                if (selected >= 0) throw new NotSupportedException("Multiple AVI video streams are not supported.");
                selected = index; stream = new(sh.Offset, sh.Length); format = new(sf.Offset, sf.Length);
            }
        }
        if (selected < 0 || format.Length < 40) throw new InvalidDataException("AVI video stream/bitmap header is missing.");
        int width = reader.I32(format.Offset + 4), signedHeight = reader.I32(format.Offset + 8);
        int bits = reader.U16(format.Offset + 14); uint compression = reader.U32(format.Offset + 16);
        uint scale = reader.U32(stream.Offset + 20), rate = reader.U32(stream.Offset + 24);
        bool video1 = IsVideo1Compression(compression);
        if (reader.U16(format.Offset + 12) != 1) throw new InvalidDataException("AVI bitmap planes must be one.");
        if (!((compression == 0 && (bits == 24 || bits == 32)) || (compression == 1 && bits == 8)
            || compression is 0x47504a4d or 0x4745504a || (video1 && (bits == 8 || bits == 16))))
            throw new NotSupportedException("AVI supports RGB24/32, complete RLE8, baseline MJPEG and Microsoft Video 1 only; video only.");
        if (width <= 0 || width > 8192 || signedHeight == 0 || signedHeight < -8192 || signedHeight > 8192 || scale == 0 || rate == 0)
            throw new InvalidDataException("AVI dimensions or frame rate are invalid.");
        int height = Math.Abs(signedHeight);
        if ((long)width * height > 16_777_216) throw new NotSupportedException("AVI frame exceeds 16 million pixels.");
        uint headerSizeValue = reader.U32(format.Offset);
        if (headerSizeValue < 40 || headerSizeValue > format.Length) throw new InvalidDataException("AVI bitmap header size is invalid.");
        int headerSize = (int)headerSizeValue;
        if (video1 && (signedHeight < 0 || width % 4 != 0 || height % 4 != 0))
            throw new NotSupportedException("Microsoft Video 1 requires bottom-up dimensions that are multiples of four.");
        var palette = Array.Empty<(byte, byte, byte)>();
        if (compression == 1 || (video1 && bits == 8))
        {
            uint declaredColors = reader.U32(format.Offset + 32); int colors = declaredColors == 0 ? 256 : checked((int)Math.Min(declaredColors, 257u));
            if (signedHeight < 0 || colors > 256 || format.Length - headerSize < colors * 4) throw new InvalidDataException("AVI indexed palette or orientation is invalid.");
            palette = new (byte, byte, byte)[colors];
            for (int index = 0; index < colors; index++)
            {
                int offset = format.Offset + headerSize + index * 4;
                palette[index] = (source[offset + 2], source[offset + 1], source[offset]);
            }
        }
        if (selected > 99) throw new NotSupportedException("AVI stream indices above 99 are unsupported.");
        string prefix = selected.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        int stride = checked(((width * bits + 31) / 32) * 4);
        var frames = new List<Slice>();
        void Collect(int start, int limit, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 16) throw new InvalidDataException("AVI record nesting exceeds the limit.");
            foreach (var chunk in reader.Chunks(start, limit))
            {
                if (chunk.Id == prefix + "db" || chunk.Id == prefix + "dc")
                {
                    if (chunk.Length < (compression == 0 ? stride * height : 2)) throw new InvalidDataException("Truncated AVI frame.");
                    if (chunk.Length > (video1 ? 33_554_432 : 67_108_864)) throw new NotSupportedException("Compressed AVI frame exceeds its size limit.");
                    frames.Add(new(chunk.Offset, chunk.Length));
                    if (frames.Count > 1_000_000) throw new NotSupportedException("AVI frame-count limit exceeded.");
                }
                else if ((compression == 1 || video1) && chunk.Id == prefix + "pc") throw new NotSupportedException("Dynamic AVI palette-change chunks are not implemented.");
                else if (chunk.Id == "LIST")
                {
                    if (chunk.Length < 4) throw new InvalidDataException("AVI record list is truncated.");
                    if (reader.FourCC(chunk.Offset) == "rec ") Collect(chunk.Offset + 4, chunk.End, depth + 1);
                }
            }
        }
        Collect(movie.Offset + 4, movie.End, 0); cancellationToken.ThrowIfCancellationRequested();
        if (frames.Count == 0) throw new InvalidDataException("AVI contains no video frames.");
        return new AviVideo(source, frames.ToArray(), width, height, bits, compression, signedHeight > 0, (double)rate / scale, palette);
    }

    public byte[] DecodeFrame(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((uint)index >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (IsVideo1) return DecodeVideo1(index, cancellationToken);
        Slice frame = frames[index];
        if (compression is 0x47504a4d or 0x4745504a)
        {
            var result = BaselineJpegDecoder.Decode(data.AsSpan(frame.Offset, frame.Length), cancellationToken);
            if (result.Width != Width || result.Height != Height) throw new NotSupportedException("MJPEG fields/dimension changes are not implemented.");
            return result.Rgba;
        }
        int stride = ((Width * bits + 31) / 32) * 4, pixelWidth = bits / 8;
        byte[]? indices = compression == 1 ? DecodeRle(frame, cancellationToken) : null;
        byte[] rgba = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            if ((y & 7) == 0) cancellationToken.ThrowIfCancellationRequested();
            int sourceY = bottomUp ? Height - 1 - y : y;
            for (int x = 0; x < Width; x++)
            {
                int output = (y * Width + x) * 4;
                if (indices is not null)
                {
                    var color = palette[indices[sourceY * Width + x]];
                    rgba[output] = color.R; rgba[output + 1] = color.G; rgba[output + 2] = color.B;
                }
                else
                {
                    int source = frame.Offset + sourceY * stride + x * pixelWidth;
                    rgba[output] = data[source + 2]; rgba[output + 1] = data[source + 1]; rgba[output + 2] = data[source];
                }
                rgba[output + 3] = 255;
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); return rgba;
    }

    private byte[] DecodeVideo1(int target, CancellationToken token)
    {
        MSVideo1Decoder.Frame? previous; int checkpoint, start;
        lock (cacheLock)
        {
            if (cachedIndex == target && cached is not null) return (byte[])cached.Rgba.Clone();
            previous = null; checkpoint = cachedIndex; start = 0;
            foreach (int key in keyframes) if (key <= target && key > start) start = key;
            if (checkpoint < target && checkpoint >= start && cached is not null) { previous = cached; start = checkpoint + 1; }
        }
        var discovered = new List<int>();
        for (int index = start; index <= target; index++)
        {
            token.ThrowIfCancellationRequested(); var slice = frames[index];
            previous = MSVideo1Decoder.Decode(Width, Height, bits, data.AsSpan(slice.Offset, slice.Length), palette, previous, token);
            if (previous.IsKeyframe) discovered.Add(index);
        }
        token.ThrowIfCancellationRequested();
        if (previous is null) throw new InvalidDataException("Video 1 replay produced no frame.");
        lock (cacheLock) { cached = previous; cachedIndex = target; foreach (int key in discovered) keyframes.Add(key); }
        return (byte[])previous.Rgba.Clone();
    }

    private byte[] DecodeRle(Slice frame, CancellationToken token)
    {
        byte[] indices = new byte[Width * Height]; int cursor = frame.Offset, x = 0, y = 0, written = 0, operations = 0;
        void Validate(byte value) { if (value >= palette.Length) throw new InvalidDataException("RLE8 palette index exceeds supplied palette."); }
        while (cursor < frame.End)
        {
            if ((operations++ & 255) == 0) token.ThrowIfCancellationRequested();
            if (frame.End - cursor < 2) throw new InvalidDataException("RLE8 opcode is truncated.");
            int count = data[cursor++]; byte value = data[cursor++];
            if (count != 0)
            {
                Validate(value); if (y >= Height || count > Width - x) throw new InvalidDataException("RLE8 run exceeds scanline.");
                Array.Fill(indices, value, y * Width + x, count); x += count; written += count;
            }
            else if (value == 0)
            {
                if (x != Width || y >= Height) throw new NotSupportedException("RLE8 delta/partial frames are not implemented.");
                x = 0; y++;
            }
            else if (value == 1)
            {
                if (written != Width * Height) throw new NotSupportedException("RLE8 requires complete keyframes.");
                if (cursor != frame.End) throw new InvalidDataException("Unexpected RLE8 trailer.");
                return indices;
            }
            else if (value == 2) throw new NotSupportedException("RLE8 delta/partial frames are not implemented.");
            else
            {
                int size = value, padded = size + (size & 1);
                if (y >= Height || size > Width - x || padded > frame.End - cursor) throw new InvalidDataException("RLE8 literal exceeds scanline or packet.");
                for (int offset = 0; offset < size; offset++) { byte color = data[cursor + offset]; Validate(color); indices[y * Width + x + offset] = color; }
                cursor += padded; x += size; written += size;
            }
        }
        throw new InvalidDataException("RLE8 end-of-bitmap marker is missing.");
    }

    private readonly record struct Slice(int Offset, int Length) { public int End => Offset + Length; }
    private readonly record struct Chunk(string Id, int Offset, int Length) { public int End => Offset + Length; }
    private sealed class RiffReader(byte[] bytes, CancellationToken token)
    {
        private int chunkCount;
        private void Require(int offset, int length) { if (offset < 0 || length < 0 || offset > bytes.Length || length > bytes.Length - offset) throw new InvalidDataException("Truncated RIFF data."); }
        public int U16(int offset) { Require(offset, 2); return bytes[offset] | bytes[offset + 1] << 8; }
        public uint U32(int offset) { Require(offset, 4); return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
        public int I32(int offset) => unchecked((int)U32(offset));
        public string FourCC(int offset) { Require(offset, 4); return Encoding.ASCII.GetString(bytes, offset, 4); }
        public List<Chunk> Chunks(int start, int end)
        {
            Require(start, end - start); var chunks = new List<Chunk>();
            for (int offset = start; offset < end;)
            {
                if ((chunkCount++ & 1023) == 0) token.ThrowIfCancellationRequested();
                if (chunkCount > 1_000_000) throw new NotSupportedException("RIFF chunk-count limit exceeded.");
                if (end - offset < 8) throw new InvalidDataException("Partial RIFF chunk header.");
                uint size = U32(offset + 4);
                if (size > end - offset - 8) throw new InvalidDataException("RIFF chunk exceeds its parent.");
                chunks.Add(new(FourCC(offset), offset + 8, (int)size));
                offset += 8 + (int)size + ((int)size & 1);
                if (offset > end) throw new InvalidDataException("RIFF odd-size padding is missing.");
            }
            return chunks;
        }
    }
}
