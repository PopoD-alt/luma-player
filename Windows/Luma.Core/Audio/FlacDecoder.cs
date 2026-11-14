using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Luma.Core.Audio;

/// <summary>
/// Original native-FLAC parser and sample reconstruction, ported from Luma's
/// original Swift implementation derived from RFC 9639. No platform FLAC
/// decoder, libFLAC, VLC, FFmpeg, or other codec library is called.
/// Returned samples are signed, unscaled values at the declared source depth.
/// </summary>
public static class FlacDecoder
{
    public const int InputLimit = 268_435_456;
    public const int SampleLimit = 64_000_000;

    public static AudioBuffer Decode(ReadOnlySpan<byte> source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > InputLimit) throw Capacity("compressed input exceeds 256 MiB");
        if (source.Length < 4 || !source[..4].SequenceEqual("fLaC"u8))
            throw new NotSupportedException("The original FLAC decoder requires a native .flac stream; Ogg/MP4 FLAC and ID3 wrappers are not implemented.");
        var bits = new Bits(source, 32, cancellationToken);
        StreamInfo? info = null;
        int metadataCount = 0;
        while (true)
        {
            if ((metadataCount & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
            bool last = bits.Read(1) != 0;
            int type = (int)bits.Read(7), length = (int)bits.Read(24);
            if (type == 127) throw Malformed("forbidden metadata type");
            if (metadataCount == 0 && type != 0) throw Malformed("STREAMINFO must be first");
            if (length > bits.Remaining / 8) throw Malformed("truncated metadata");
            if (++metadataCount > 100_000) throw Capacity("too many metadata blocks");
            if (type == 0)
            {
                if (info is not null || length != 34) throw Malformed("duplicate or incorrectly sized STREAMINFO");
                int minBlock = (int)bits.Read(16), maxBlock = (int)bits.Read(16);
                int minFrame = (int)bits.Read(24), maxFrame = (int)bits.Read(24);
                int rate = (int)bits.Read(20), channels = (int)bits.Read(3) + 1;
                int depth = (int)bits.Read(5) + 1;
                ulong total = bits.Read(36);
                var md5 = new byte[16];
                for (int index = 0; index < md5.Length; index++) md5[index] = (byte)bits.Read(8);
                if (minBlock < 16 || minBlock > maxBlock || depth < 4 || depth > 32 || rate <= 0 ||
                    (maxFrame != 0 && minFrame > maxFrame)) throw Malformed("invalid STREAMINFO limits");
                if (total > (ulong)(SampleLimit / channels)) throw Capacity("declared output exceeds 64 million scalar samples");
                info = new StreamInfo(minBlock, maxBlock, minFrame, maxFrame, rate, channels, depth, total, md5);
            }
            else
            {
                // Ancillary metadata never executes applications or fetches URLs.
                bits.Skip((long)length * 8);
            }
            if (last) break;
        }
        if (info is null) throw Malformed("missing STREAMINFO");

        var output = info.Total > 0 ? new List<int>((int)info.Total * info.Channels) : new List<int>();
        ulong frames = 0, sampleFrames = 0;
        bool? strategy = null;
        int? previousBlock = null, firstBlock = null;
        bool verifyMd5 = false;
        foreach (byte value in info.Md5) verifyMd5 |= value != 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        while (bits.Remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frameStart = (int)(bits.Position / 8);
            if (previousBlock is int previous && previous < info.MinBlock)
                throw Malformed("nonfinal block smaller than STREAMINFO minimum");
            if (previousBlock is int previousFixed && strategy == false && previousFixed != firstBlock)
                throw Malformed("fixed block size changed before final frame");
            FrameHeader header = ReadFrameHeader(ref bits, info);
            if (strategy.HasValue && strategy.Value != header.Variable) throw Malformed("blocking strategy changed");
            strategy = header.Variable;
            if (header.Number != (header.Variable ? sampleFrames : frames)) throw Malformed("frame/sample number is not sequential");
            if (header.Block > info.MaxBlock) throw Malformed("block exceeds STREAMINFO maximum");
            if (header.Block > (SampleLimit - output.Count) / info.Channels) throw Capacity("decoded output exceeds 64 million scalar samples");
            if (info.Total > 0 && (ulong)header.Block > info.Total - Math.Min(info.Total, sampleFrames))
                throw Malformed("frames exceed declared sample count");
            firstBlock ??= header.Block;
            if (!header.Variable && header.Block > firstBlock.Value) throw Malformed("fixed block grew");
            var channels = new long[info.Channels][];
            for (int channel = 0; channel < info.Channels; channel++)
            {
                bool isSide = (header.Assignment == 8 && channel == 1) ||
                    (header.Assignment == 9 && channel == 0) || (header.Assignment == 10 && channel == 1);
                channels[channel] = ReadSubframe(ref bits, header.Block, info.Depth + (isSide ? 1 : 0));
            }
            int padding = (int)((8 - bits.Position % 8) % 8);
            if (bits.Read(padding) != 0) throw Malformed("nonzero frame padding");
            int footerStart = (int)(bits.Position / 8);
            ushort expectedCrc = (ushort)bits.Read(16);
            if (Crc16(source.Slice(frameStart, footerStart - frameStart), cancellationToken) != expectedCrc)
                throw Malformed("frame CRC16 mismatch");
            int frameSize = (int)(bits.Position / 8) - frameStart;
            if ((info.MinFrame != 0 && frameSize < info.MinFrame) || (info.MaxFrame != 0 && frameSize > info.MaxFrame))
                throw Malformed("frame size differs from STREAMINFO");
            long lower = -(1L << (info.Depth - 1)), upper = (1L << (info.Depth - 1)) - 1;
            int sampleBytes = (info.Depth + 7) / 8;
            byte[]? md5Bytes = verifyMd5 ? new byte[header.Block * info.Channels * sampleBytes] : null;
            int md5Offset = 0;
            for (int index = 0; index < header.Block; index++)
            {
                if ((index & 0x1fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (header.Assignment == 8) channels[1][index] = channels[0][index] - channels[1][index];
                else if (header.Assignment == 9) channels[0][index] += channels[1][index];
                else if (header.Assignment == 10)
                {
                    long side = channels[1][index];
                    long doubledMid = channels[0][index] * 2 + (side & 1);
                    channels[0][index] = (doubledMid + side) >> 1;
                    channels[1][index] = (doubledMid - side) >> 1;
                }
                for (int channel = 0; channel < info.Channels; channel++)
                {
                    long sample = channels[channel][index];
                    if (sample < lower || sample > upper) throw Malformed("decoded sample exceeds declared bit depth");
                    output.Add((int)sample);
                    if (md5Bytes is not null)
                    {
                        // FLAC MD5 uses signed extension to a whole byte, not
                        // WAVE's least-significant padding of a 12/20-bit source.
                        for (int byteIndex = 0; byteIndex < sampleBytes; byteIndex++)
                            md5Bytes[md5Offset++] = unchecked((byte)(sample >> (byteIndex * 8)));
                    }
                }
            }
            if (md5Bytes is not null) hash.AppendData(md5Bytes);
            previousBlock = header.Block;
            frames++;
            sampleFrames += (ulong)header.Block;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (frames == 0 || output.Count == 0) throw Malformed("no audio frames");
        if (info.Total != 0 && sampleFrames != info.Total) throw Malformed("sample count does not match STREAMINFO");
        if (verifyMd5 && !hash.GetHashAndReset().AsSpan().SequenceEqual(info.Md5)) throw Malformed("decoded audio MD5 mismatch");
        return new AudioBuffer(info.SampleRate, info.Channels, info.Depth, output.ToArray(), "Luma original FLAC");
    }

    private sealed record StreamInfo(int MinBlock, int MaxBlock, int MinFrame, int MaxFrame,
        int SampleRate, int Channels, int Depth, ulong Total, byte[] Md5);
    private readonly record struct FrameHeader(bool Variable, int Block, int Assignment, ulong Number);

    private static FrameHeader ReadFrameHeader(ref Bits bits, StreamInfo info)
    {
        if (bits.Position % 8 != 0) throw Malformed("unaligned frame");
        int start = (int)(bits.Position / 8);
        if (bits.Read(15) != 0x7ffc) throw Malformed("frame sync missing");
        bool variable = bits.Read(1) != 0;
        int blockCode = (int)bits.Read(4), rateCode = (int)bits.Read(4);
        int assignment = (int)bits.Read(4), depthCode = (int)bits.Read(3);
        if (bits.Read(1) != 0) throw Malformed("reserved frame bit is nonzero");
        if (blockCode == 0 || rateCode == 15 || assignment > 10 || depthCode == 3)
            throw new NotSupportedException("Reserved FLAC frame parameters are not supported.");
        ulong number = ReadCodedNumber(ref bits, variable);
        int block = blockCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 144 << blockCode,
            6 => (int)bits.Read(8) + 1,
            7 => (int)bits.Read(16) + 1,
            _ => 1 << blockCode
        };
        if (block > 65535) throw Malformed("forbidden block size 65536");
        int rate = rateCode switch
        {
            0 => info.SampleRate,
            >= 1 and <= 11 => SampleRates[rateCode],
            12 => (int)bits.Read(8) * 1000,
            13 => (int)bits.Read(16),
            _ => (int)bits.Read(16) * 10
        };
        int depth = depthCode switch { 0 => info.Depth, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => 0 };
        int channels = assignment < 8 ? assignment + 1 : 2;
        if (rate != info.SampleRate || depth != info.Depth || channels != info.Channels)
            throw new NotSupportedException("FLAC streams that change sample rate, bit depth, or channel count are not supported.");
        int end = (int)(bits.Position / 8);
        byte expected = (byte)bits.Read(8);
        if (Crc8(bits.Data.Slice(start, end - start)) != expected) throw Malformed("header CRC8 mismatch");
        return new FrameHeader(variable, block, assignment, number);
    }

    private static readonly int[] SampleRates = { 0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };

    private static ulong ReadCodedNumber(ref Bits bits, bool variable)
    {
        byte first = (byte)bits.Read(8);
        if ((first & 0x80) == 0) return first;
        int length = 0, mask = 0x80;
        while (mask != 0 && (first & mask) != 0) { length++; mask >>= 1; }
        if (length < 2 || length > (variable ? 7 : 6)) throw Malformed("invalid coded number prefix");
        ulong value = (ulong)(first & (0x7f >> length));
        for (int index = 1; index < length; index++)
        {
            ulong next = bits.Read(8);
            if ((next & 0xc0) != 0x80) throw Malformed("invalid coded number continuation");
            value = (value << 6) | (next & 0x3f);
        }
        ulong minimum = length switch { 2 => 0x80, 3 => 0x800, 4 => 0x10000, 5 => 0x200000, 6 => 0x4000000, _ => 0x80000000 };
        if (value < minimum || value >= (1UL << (variable ? 36 : 31))) throw Malformed("overlong or out-of-range coded number");
        return value;
    }

    private static long[] ReadSubframe(ref Bits bits, int count, int depth)
    {
        bits.CancellationToken.ThrowIfCancellationRequested();
        if (bits.Read(1) != 0) throw Malformed("reserved subframe bit is nonzero");
        int type = (int)bits.Read(6);
        bool hasWasted = bits.Read(1) != 0;
        int wasted = hasWasted ? (int)bits.Unary((ulong)(depth - 2)) + 1 : 0;
        int precision = depth - wasted;
        if (precision <= 0) throw Malformed("wasted bits exhaust sample depth");
        var samples = new long[count];
        if (type == 0)
        {
            Array.Fill(samples, bits.Signed(precision));
        }
        else if (type == 1)
        {
            for (int index = 0; index < count; index++)
            {
                if ((index & 0xffff) == 0) bits.CancellationToken.ThrowIfCancellationRequested();
                samples[index] = bits.Signed(precision);
            }
        }
        else if ((type >= 8 && type <= 12) || (type >= 32 && type <= 63))
        {
            int order = type < 32 ? type - 8 : type - 31;
            if (order >= count) throw Malformed("predictor order is not smaller than block");
            for (int index = 0; index < order; index++) samples[index] = bits.Signed(precision);
            long[] coefficients;
            int shift;
            if (type < 32)
            {
                coefficients = order switch
                {
                    0 => Array.Empty<long>(), 1 => new long[] { 1 }, 2 => new long[] { 2, -1 },
                    3 => new long[] { 3, -3, 1 }, _ => new long[] { 4, -6, 4, -1 }
                };
                shift = 0;
            }
            else
            {
                int code = (int)bits.Read(4);
                if (code == 15) throw Malformed("forbidden LPC coefficient precision");
                shift = (int)bits.Signed(5);
                if (shift < 0) throw Malformed("forbidden negative LPC shift");
                coefficients = new long[order];
                for (int index = 0; index < order; index++) coefficients[index] = bits.Signed(code + 1);
            }
            long[] residuals = ReadResidual(ref bits, count, order);
            long lower = -(1L << (precision - 1)), upper = (1L << (precision - 1)) - 1;
            for (int index = order; index < count; index++)
            {
                if (((index - order) & 0x3fff) == 0) bits.CancellationToken.ThrowIfCancellationRequested();
                long prediction = 0;
                // At most 33-bit samples, 15-bit coefficients and 32 terms:
                // every permitted product and accumulator fits signed Int64.
                for (int coefficient = 0; coefficient < order; coefficient++)
                    prediction += coefficients[coefficient] * samples[index - coefficient - 1];
                long sample = (prediction >> shift) + residuals[index - order];
                if (sample < lower || sample > upper) throw Malformed("predictor reconstructs an out-of-range sample");
                samples[index] = sample;
            }
        }
        else throw new NotSupportedException($"Reserved FLAC subframe type {type} is not implemented.");

        if (wasted > 0)
        {
            bits.CancellationToken.ThrowIfCancellationRequested();
            for (int index = 0; index < samples.Length; index++) samples[index] <<= wasted;
        }
        return samples;
    }

    private static long[] ReadResidual(ref Bits bits, int count, int order)
    {
        int method = (int)bits.Read(2);
        if (method > 1) throw new NotSupportedException("Reserved FLAC residual coding method is not implemented.");
        int partitionOrder = (int)bits.Read(4), partitions = 1 << partitionOrder;
        if (count % partitions != 0 || count / partitions <= order) throw Malformed("invalid residual partition geometry");
        int width = method == 0 ? 4 : 5, escape = (1 << width) - 1;
        var samples = new long[count - order];
        int destination = 0;
        for (int partition = 0; partition < partitions; partition++)
        {
            if ((partition & 0x3ff) == 0) bits.CancellationToken.ThrowIfCancellationRequested();
            int sampleCount = count / partitions - (partition == 0 ? order : 0);
            int parameter = (int)bits.Read(width);
            if (parameter == escape)
            {
                int rawBits = (int)bits.Read(5);
                for (int index = 0; index < sampleCount; index++)
                {
                    if ((index & 0xffff) == 0) bits.CancellationToken.ThrowIfCancellationRequested();
                    samples[destination++] = rawBits == 0 ? 0 : bits.Signed(rawBits);
                }
            }
            else
            {
                for (int index = 0; index < sampleCount; index++)
                {
                    if ((index & 0xffff) == 0) bits.CancellationToken.ThrowIfCancellationRequested();
                    ulong quotient = bits.Unary(0xffff_fffeUL >> parameter);
                    ulong folded = (quotient << parameter) | bits.Read(parameter);
                    if (folded > 0xffff_fffe) throw Malformed("residual exceeds signed 32-bit magnitude");
                    samples[destination++] = (folded & 1) == 0 ? (long)(folded >> 1) : -(long)(folded >> 1) - 1;
                }
            }
        }
        return samples;
    }

    private static byte Crc8(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = ((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1) & 0xff;
        }
        return (byte)crc;
    }

    private static ushort Crc16(ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        int crc = 0;
        for (int index = 0; index < data.Length; index++)
        {
            if ((index & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            crc ^= data[index] << 8;
            for (int bit = 0; bit < 8; bit++) crc = ((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1) & 0xffff;
        }
        return (ushort)crc;
    }

    private static InvalidDataException Malformed(string detail) => new("Malformed FLAC: " + detail);
    private static InvalidDataException Capacity(string detail) => new("FLAC resource limit: " + detail);

    /// <summary>MSB-first bounded reader. Long positions cover exactly 256 MiB.</summary>
    private ref struct Bits
    {
        public readonly ReadOnlySpan<byte> Data;
        public readonly CancellationToken CancellationToken;
        public long Position;
        public readonly long Remaining => (long)Data.Length * 8 - Position;

        public Bits(ReadOnlySpan<byte> data, long position, CancellationToken cancellationToken)
        {
            Data = data; Position = position; CancellationToken = cancellationToken;
        }
        public ulong Read(int count)
        {
            if (count < 0 || count > 36 || count > Remaining) throw Malformed("truncated bitstream");
            ulong result = 0;
            int todo = count;
            while (todo > 0)
            {
                int available = 8 - (int)(Position % 8), take = Math.Min(available, todo);
                int value = (Data[(int)(Position / 8)] >> (available - take)) & ((1 << take) - 1);
                result = (result << take) | (uint)value;
                Position += take;
                todo -= take;
            }
            return result;
        }
        public long Signed(int count)
        {
            if (count < 1 || count > 33) throw Malformed("invalid signed value width");
            ulong raw = Read(count);
            return (raw & (1UL << (count - 1))) == 0 ? (long)raw : (long)raw - (1L << count);
        }
        public void Skip(long count)
        {
            if (count < 0 || count > Remaining) throw Malformed("truncated metadata");
            Position += count;
        }
        public ulong Unary(ulong maximum)
        {
            ulong zeros = 0;
            while (Remaining > 0)
            {
                if ((Position & 0x7ffff) == 0) CancellationToken.ThrowIfCancellationRequested();
                if (Position % 8 == 0 && Data[(int)(Position / 8)] == 0)
                {
                    if (zeros > maximum || maximum - zeros < 8) throw Malformed("unary value exceeds limit");
                    zeros += 8;
                    Position += 8;
                }
                else if (Read(1) == 1) return zeros;
                else
                {
                    if (zeros >= maximum) throw Malformed("unary value exceeds limit");
                    zeros++;
                }
            }
            throw Malformed("unterminated unary value");
        }
    }
}
