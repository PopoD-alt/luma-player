using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Luma.Core.Video;

/// <summary>Portable port of Luma's original T.81 baseline sequential Huffman
/// JPEG implementation: entropy decode, dequantization and separable IDCT.
/// Explicit tables and one interleaved scan required; no native JPEG dependency.</summary>
internal static class BaselineJpegDecoder
{
    internal sealed record Image(int Width, int Height, byte[] Rgba);
    private static readonly int[] Zigzag = [0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63];
    private sealed class Component(int id, int h, int v, int quantizer)
    {
        internal readonly int Id = id, H = h, V = v, Quantizer = quantizer;
        internal int DcTable, AcTable, Predictor, Stride;
        internal byte[] Plane = [];
    }
    private sealed class Huffman
    {
        private readonly Dictionary<int, int> entries = new();
        internal Huffman(int[] counts, byte[] values)
        {
            int code = 0, index = 0;
            for (int length = 1; length <= 16; length++)
            {
                if (code + counts[length - 1] >= (1 << length)) throw new InvalidDataException("Oversubscribed/all-ones JPEG Huffman table.");
                for (int i = 0; i < counts[length - 1]; i++) entries[(length << 16) | code++] = values[index++];
                code <<= 1;
            }
        }
        internal int Symbol(Entropy reader)
        {
            int code = 0;
            for (int length = 1; length <= 16; length++)
            {
                code = (code << 1) | reader.Bits(1);
                if (entries.TryGetValue((length << 16) | code, out int symbol)) return symbol;
            }
            throw new InvalidDataException("Invalid JPEG Huffman code.");
        }
    }
    private sealed class Entropy(byte[] bytes, int position)
    {
        internal int Position = position;
        private int value, remaining;
        internal int Bits(int count)
        {
            if (count < 0 || count > 16) throw new InvalidDataException("Invalid JPEG bit count.");
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                if (remaining == 0)
                {
                    if (Position >= bytes.Length) throw new InvalidDataException("Truncated JPEG entropy.");
                    value = bytes[Position++];
                    if (value == 255)
                    {
                        if (Position >= bytes.Length || bytes[Position++] != 0) throw new InvalidDataException("Unexpected JPEG entropy marker.");
                    }
                    remaining = 8;
                }
                result = (result << 1) | (value >> --remaining & 1);
            }
            return result;
        }
        internal int Marker()
        {
            if (remaining > 0 && (value & ((1 << remaining) - 1)) != (1 << remaining) - 1) throw new InvalidDataException("JPEG entropy padding must contain ones.");
            remaining = 0;
            if (Position >= bytes.Length || bytes[Position] != 255) throw new InvalidDataException("JPEG marker missing.");
            while (Position < bytes.Length && bytes[Position] == 255) Position++;
            if (Position >= bytes.Length) throw new InvalidDataException("JPEG marker truncated.");
            return bytes[Position++];
        }
        internal int Signed(int count)
        {
            if (count == 0) return 0;
            int value = Bits(count); return value < (1 << (count - 1)) ? value - ((1 << count) - 1) : value;
        }
    }
    private static readonly double[,] Basis = MakeBasis();
    private static double[,] MakeBasis()
    {
        var basis = new double[8,8];
        for (int x = 0; x < 8; x++) for (int frequency = 0; frequency < 8; frequency++)
            basis[x,frequency] = (frequency == 0 ? 1 / Math.Sqrt(2) : 1) * Math.Cos((2 * x + 1) * frequency * Math.PI / 16) / 2;
        return basis;
    }
    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
    internal static Image Decode(ReadOnlySpan<byte> packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (packet.Length > 67_108_864) throw new NotSupportedException("JPEG frame exceeds 64 MiB.");
        byte[] bytes = packet.ToArray();
        if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216) throw new InvalidDataException("JPEG SOI missing.");
        int position = 2, width = 0, height = 0, restartInterval = 0;
        var components = new List<Component>(); var quantizers = new Dictionary<int,int[]>(); var tables = new Dictionary<int,Huffman>();
        int? adobe = null;
        int U16(int offset)
        {
            if (offset < 0 || offset >= bytes.Length - 1) throw new InvalidDataException("Truncated JPEG integer.");
            return bytes[offset] << 8 | bytes[offset + 1];
        }
        while (position < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if (bytes[position] != 255) throw new InvalidDataException("JPEG segment marker missing.");
            while (position < bytes.Length && bytes[position] == 255) position++;
            if (position >= bytes.Length) throw new InvalidDataException("JPEG marker truncated.");
            int marker = bytes[position++];
            if (marker == 0xd9 || marker == 0xd8 || marker is >= 0xd0 and <= 0xd7) throw new InvalidDataException("Unexpected JPEG marker before scan.");
            int length = U16(position);
            if (length < 2 || length > bytes.Length - position) throw new InvalidDataException("JPEG segment exceeds packet.");
            int start = position + 2, end = position + length; position = end;
            switch (marker)
            {
                case 0xdb:
                    for (int cursor = start; cursor < end;)
                    {
                        int descriptor = bytes[cursor++];
                        if ((descriptor >> 4) != 0 || (descriptor & 15) > 3 || end - cursor < 64) throw new NotSupportedException("JPEG requires baseline 8-bit quantization tables.");
                        int[] table = new int[64];
                        for (int index = 0; index < 64; index++)
                        {
                            int value = bytes[cursor + index]; if (value == 0) throw new InvalidDataException("Zero JPEG quantizer.");
                            table[Zigzag[index]] = value;
                        }
                        quantizers[descriptor & 15] = table; cursor += 64;
                    }
                    break;
                case 0xc4:
                    for (int cursor = start; cursor < end;)
                    {
                        if (end - cursor < 17) throw new InvalidDataException("Truncated JPEG Huffman header.");
                        int descriptor = bytes[cursor++];
                        if ((descriptor >> 4) > 1 || (descriptor & 15) > 3) throw new InvalidDataException("Invalid JPEG Huffman class.");
                        int[] counts = new int[16]; for (int index = 0; index < 16; index++) counts[index] = bytes[cursor++];
                        int total = counts.Sum();
                        if (total == 0 || total > 256 || total > end - cursor) throw new InvalidDataException("Invalid JPEG Huffman symbol count.");
                        tables[descriptor] = new Huffman(counts, bytes.AsSpan(cursor,total).ToArray()); cursor += total;
                    }
                    break;
                case 0xc0:
                    if (components.Count != 0 || end - start < 6 || bytes[start] != 8) throw new NotSupportedException("Only 8-bit baseline sequential JPEG is implemented.");
                    height = U16(start + 1); width = U16(start + 3); int componentCount = bytes[start + 5];
                    if ((componentCount != 1 && componentCount != 3) || end - start != 6 + componentCount * 3
                        || width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 16_777_216)
                        throw new NotSupportedException("JPEG dimensions/color layout exceed this decoder's subset.");
                    for (int index = 0; index < componentCount; index++)
                    {
                        int offset = start + 6 + index * 3, id = bytes[offset], h = bytes[offset + 1] >> 4, v = bytes[offset + 1] & 15, quantizer = bytes[offset + 2];
                        if (h < 1 || h > 4 || v < 1 || v > 4 || quantizer > 3 || components.Any(c => c.Id == id)) throw new InvalidDataException("Invalid JPEG component.");
                        components.Add(new(id,h,v,quantizer));
                    }
                    if (componentCount == 1 && (components[0].H != 1 || components[0].V != 1)) throw new NotSupportedException("Grayscale JPEG requires 1x1 sampling.");
                    break;
                case 0xdd:
                    if (end - start != 2) throw new InvalidDataException("Invalid JPEG restart interval.");
                    restartInterval = U16(start); break;
                case 0xee:
                    if (end - start >= 12 && Encoding.ASCII.GetString(bytes,start,5) == "Adobe") adobe = bytes[start + 11];
                    break;
                case 0xda:
                    if (components.Count == 0 || end <= start) throw new InvalidDataException("JPEG scan precedes frame.");
                    int count = bytes[start];
                    if (count != components.Count || end - start != 1 + count * 2 + 3 || bytes[end - 3] != 0 || bytes[end - 2] != 63 || bytes[end - 1] != 0)
                        throw new NotSupportedException("Non-interleaved/progressive JPEG scans are not implemented.");
                    var order = new List<int>();
                    for (int index = 0; index < count; index++)
                    {
                        int id = bytes[start + 1 + index * 2], selectors = bytes[start + 2 + index * 2], component = components.FindIndex(c => c.Id == id);
                        if (component < 0 || order.Contains(component) || (selectors >> 4) > 3 || (selectors & 15) > 3) throw new InvalidDataException("Invalid JPEG scan component.");
                        components[component].DcTable = selectors >> 4; components[component].AcTable = selectors & 15; order.Add(component);
                    }
                    int maxH = components.Max(c => c.H), maxV = components.Max(c => c.V);
                    if (components.Sum(c => c.H * c.V) > 10 || components.Any(c => maxH % c.H != 0 || maxV % c.V != 0)) throw new NotSupportedException("Unsupported JPEG sampling factors.");
                    int columns = (width + maxH * 8 - 1) / (maxH * 8), rows = (height + maxV * 8 - 1) / (maxV * 8);
                    foreach (var component in components)
                    {
                        component.Stride = columns * component.H * 8;
                        component.Plane = new byte[component.Stride * rows * component.V * 8];
                    }
                    var reader = new Entropy(bytes,end); int restartNumber = 0;
                    for (int mcu = 0; mcu < columns * rows; mcu++)
                    {
                        if ((mcu & 127) == 0) token.ThrowIfCancellationRequested();
                        if (restartInterval > 0 && mcu > 0 && mcu % restartInterval == 0)
                        {
                            if (reader.Marker() != 0xd0 + restartNumber) throw new InvalidDataException("Invalid JPEG restart sequence.");
                            restartNumber = (restartNumber + 1) & 7; foreach (var component in components) component.Predictor = 0;
                        }
                        foreach (int index in order)
                        {
                            var component = components[index];
                            if (!quantizers.TryGetValue(component.Quantizer,out int[]? quantizer) || !tables.TryGetValue(component.DcTable,out Huffman? dc) || !tables.TryGetValue(16 | component.AcTable,out Huffman? ac))
                                throw new NotSupportedException("JPEG is missing explicit quantization/Huffman tables.");
                            for (int by = 0; by < component.V; by++) for (int bx = 0; bx < component.H; bx++)
                            {
                                int[] coefficients = new int[64]; int category = dc.Symbol(reader);
                                if (category > 11) throw new InvalidDataException("JPEG DC magnitude exceeds baseline.");
                                component.Predictor += reader.Signed(category);
                                if (Math.Abs(component.Predictor) > 32767) throw new InvalidDataException("JPEG DC predictor exceeds bound.");
                                coefficients[0] = component.Predictor * quantizer[0]; int cursor = 1;
                                while (cursor < 64)
                                {
                                    int symbol = ac.Symbol(reader), run = symbol >> 4, magnitude = symbol & 15;
                                    if (symbol == 0) break;
                                    if (symbol == 0xf0) { cursor += 16; if (cursor > 64) throw new InvalidDataException("JPEG zero run exceeds block."); continue; }
                                    if (magnitude == 0 || magnitude > 10) throw new InvalidDataException("JPEG AC magnitude exceeds baseline.");
                                    cursor += run; if (cursor >= 64) throw new InvalidDataException("JPEG AC run exceeds block.");
                                    int natural = Zigzag[cursor++]; coefficients[natural] = reader.Signed(magnitude) * quantizer[natural];
                                }
                                byte[] block = Idct(coefficients);
                                int x = (mcu % columns * component.H + bx) * 8, y = (mcu / columns * component.V + by) * 8;
                                for (int dy = 0; dy < 8; dy++) Array.Copy(block,dy * 8,component.Plane,(y + dy) * component.Stride + x,8);
                            }
                        }
                    }
                    if (reader.Marker() != 0xd9 || reader.Position != bytes.Length) throw new NotSupportedException("Multiple JPEG scans/fields or trailing data are not implemented.");
                    if (adobe.HasValue && adobe != 0 && adobe != 1) throw new NotSupportedException("JPEG color transform is not implemented.");
                    bool rgb = adobe == 0 || components.Select(c => c.Id).SequenceEqual([82,71,66]);
                    byte[] output = new byte[width * height * 4];
                    for (int y = 0; y < height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        for (int x = 0; x < width; x++)
                        {
                            double Sample(int index) { var c = components[index]; return c.Plane[(y * c.V / maxV) * c.Stride + x * c.H / maxH]; }
                            double first = Sample(0), r, g, b;
                            if (components.Count == 1) r = g = b = first;
                            else if (rgb) { r = first; g = Sample(1); b = Sample(2); }
                            else { double cb = Sample(1) - 128, cr = Sample(2) - 128; r = first + 1.402 * cr; g = first - 0.344136 * cb - 0.714136 * cr; b = first + 1.772 * cb; }
                            int target = (y * width + x) * 4; output[target] = Byte(r); output[target + 1] = Byte(g); output[target + 2] = Byte(b); output[target + 3] = 255;
                        }
                    }
                    token.ThrowIfCancellationRequested(); return new(width,height,output);
                default:
                    if (marker is >= 0xe0 and <= 0xef || marker == 0xfe) break;
                    throw new NotSupportedException("JPEG coding marker is not baseline sequential Huffman coding.");
            }
        }
        throw new InvalidDataException("JPEG scan missing.");
    }
    private static byte[] Idct(int[] coefficients)
    {
        if (coefficients.Skip(1).All(c => c == 0)) return Enumerable.Repeat(Byte(coefficients[0] / 8.0 + 128),64).ToArray();
        double[] intermediate = new double[64];
        for (int y = 0; y < 8; y++) for (int u = 0; u < 8; u++) for (int v = 0; v < 8; v++) intermediate[y * 8 + u] += Basis[y,v] * coefficients[v * 8 + u];
        byte[] result = new byte[64];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            double sum = 128; for (int u = 0; u < 8; u++) sum += intermediate[y * 8 + u] * Basis[x,u];
            result[y * 8 + x] = Byte(sum);
        }
        return result;
    }
}
