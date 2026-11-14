using System;
using System.IO;
using System.Threading;

namespace Luma.Core.Video;

/// <summary>Port of Luma's original 8/16-bit block decoder. Syntax reference:
/// https://multimedia.cx/video1.txt ; RGB555 masks follow Microsoft documentation.</summary>
internal static class MSVideo1Decoder
{
    internal sealed record Frame(int Width, int Height, int Depth, ushort[] Pixels, byte[] Rgba, bool IsKeyframe);
    internal static Frame Decode(int width, int height, int depth, ReadOnlySpan<byte> packet,
        (byte R, byte G, byte B)[] palette, Frame? previous, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 16_777_216 || packet.Length > 33_554_432)
            throw new NotSupportedException("Microsoft Video 1 exceeds dimension/packet bounds.");
        if (width % 4 != 0 || height % 4 != 0 || (depth != 8 && depth != 16)) throw new NotSupportedException("Microsoft Video 1 requires 8/16-bit complete four-pixel blocks.");
        if (depth == 8 && (palette.Length == 0 || palette.Length > 256)) throw new InvalidDataException("Microsoft Video 1 palette is missing.");
        if (previous is not null && (previous.Width != width || previous.Height != height || previous.Depth != depth)) throw new InvalidDataException("Microsoft Video 1 predecessor format differs.");
        ushort[] pixels = previous is null ? new ushort[width * height] : (ushort[])previous.Pixels.Clone();
        // Span cannot be captured by local functions; the small bounded packet is
        // copied once to keep the block reader independent of caller storage.
        byte[] bytes = packet.ToArray(); int cursor = 0, block = 0, operations = 0, blocksWide = width / 4, blockCount = width / 4 * (height / 4);
        bool dependent = false;
        byte ReadByte() { if (cursor >= bytes.Length) throw new InvalidDataException("Truncated Microsoft Video 1 block."); return bytes[cursor++]; }
        ushort ReadColor() { int low = ReadByte(); return (ushort)(depth == 8 ? low : low | ReadByte() << 8); }
        void Paint(ushort[] colors, ushort? flags, bool quadrants)
        {
            int blockX = block % blocksWide * 4, blockY = block / blocksWide * 4;
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                {
                    int pair = quadrants ? (row / 2 * 2 + column / 2) * 2 : 0;
                    int choice = flags.HasValue ? ((flags.Value & (1 << (row * 4 + column))) != 0 ? 0 : 1) : 0;
                    pixels[(height - 1 - blockY - row) * width + blockX + column] = (ushort)(colors[pair + choice] & 0x7fff);
                }
        }
        while (block < blockCount)
        {
            if ((operations++ & 255) == 0) token.ThrowIfCancellationRequested();
            byte low = ReadByte(), high = ReadByte();
            if (high is >= 0x84 and <= 0x87)
            {
                int count = (high - 0x84) * 256 + low;
                if (count > blockCount - block) throw new InvalidDataException("Video 1 skip exceeds its block grid.");
                if (count > 0) { if (previous is null) throw new NotSupportedException("Video 1 delta requires its predecessor."); dependent = true; block += count; }
                continue;
            }
            if (high < 0x80)
            {
                ushort first = ReadColor(), second = ReadColor(); bool quadrants = depth == 16 && (first & 0x8000) != 0;
                ushort[] colors = new ushort[quadrants ? 8 : 2]; colors[0] = first; colors[1] = second;
                for (int index = 2; index < colors.Length; index++) colors[index] = ReadColor();
                Paint(colors, (ushort)(low | high << 8), quadrants);
            }
            else if (depth == 8 && high >= 0x90)
            {
                ushort[] colors = new ushort[8]; for (int index = 0; index < 8; index++) colors[index] = ReadColor();
                Paint(colors, (ushort)(low | high << 8), true);
            }
            else Paint([(ushort)(depth == 8 ? low : low | high << 8)], null, false);
            block++;
        }
        if (cursor != bytes.Length && !(bytes.Length - cursor == 2 && bytes[cursor] == 0 && bytes[cursor + 1] == 0)) throw new InvalidDataException("Unexpected Video 1 packet trailer.");
        byte[] rgba = new byte[width * height * 4];
        static byte Expand(int value) => (byte)((value << 3) | (value >> 2));
        for (int index = 0; index < pixels.Length; index++)
        {
            if ((index & 65535) == 0) token.ThrowIfCancellationRequested();
            int color = pixels[index], output = index * 4;
            if (depth == 8)
            {
                if (color >= palette.Length) throw new InvalidDataException("Video 1 palette index exceeds supplied palette.");
                rgba[output] = palette[color].R; rgba[output + 1] = palette[color].G; rgba[output + 2] = palette[color].B;
            }
            else { rgba[output] = Expand(color >> 10 & 31); rgba[output + 1] = Expand(color >> 5 & 31); rgba[output + 2] = Expand(color & 31); }
            rgba[output + 3] = 255;
        }
        token.ThrowIfCancellationRequested(); return new(width, height, depth, pixels, rgba, !dependent);
    }
}
