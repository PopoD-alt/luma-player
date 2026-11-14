using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Luma.Core.Audio;

/// <summary>Original bounded RIFF/WAVE integer PCM, G.711 and ADPCM decoding. No platform or third-party decoder.</summary>
public static class WaveDecoder
{
    public const int InputLimit = 268_435_456, SampleLimit = 64_000_000;
    private static readonly int[] Steps = [7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767];
    private static readonly int[] IndexChanges = [-1,-1,-1,-1,2,4,6,8];

    public static AudioBuffer Decode(ReadOnlySpan<byte> source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > InputLimit) throw new InvalidDataException("WAVE input exceeds 256 MiB.");
        if (source.Length < 12 || !source[..4].SequenceEqual("RIFF"u8) || !source.Slice(8,4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF WAVE file.");
        long declaredEnd = (long)BinaryPrimitives.ReadUInt32LittleEndian(source[4..]) + 8;
        if (declaredEnd < 12 || declaredEnd > source.Length) throw new InvalidDataException("Truncated RIFF container.");
        int end = (int)declaredEnd, cursor = 12, chunkCount = 0;
        ReadOnlySpan<byte> format = default, audio = default, fact = default;
        bool hasFormat = false, hasAudio = false, hasFact = false;
        while (cursor < end)
        {
            if ((chunkCount++ & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (chunkCount > 1_000_000) throw new InvalidDataException("WAVE chunk count exceeds its limit.");
            if (end - cursor < 8) throw new InvalidDataException("Partial RIFF chunk header.");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(source[(cursor + 4)..]);
            if (length > end - cursor - 8) throw new InvalidDataException("RIFF chunk exceeds its parent.");
            var payload = source.Slice(cursor + 8, (int)length);
            var tag = source.Slice(cursor,4);
            if (!hasFormat && tag.SequenceEqual("fmt "u8)) { format = payload; hasFormat = true; }
            if (!hasAudio && tag.SequenceEqual("data"u8)) { audio = payload; hasAudio = true; }
            if (!hasFact && tag.SequenceEqual("fact"u8)) { fact = payload; hasFact = true; }
            cursor += 8 + (int)length + ((int)length & 1);
            if (cursor > end) throw new InvalidDataException("Missing RIFF chunk padding.");
        }
        if (!hasFormat || !hasAudio || format.Length < 16) throw new InvalidDataException("Missing WAVE format or samples.");
        if (hasFact && fact.Length < 4) throw new InvalidDataException("Truncated WAVE fact sample count.");
        int codec = U16(format,0), channels = U16(format,2), align = U16(format,12), depth = U16(format,14);
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
        if (channels is < 1 or > 8 || sampleRate is < 1 or > 1048575 || align == 0)
            throw new InvalidDataException("Invalid WAVE channels/rate/alignment.");
        int validDepth = depth;
        if (codec == 0xfffe)
        {
            ReadOnlySpan<byte> pcmGuid = [1,0,0,0,0,0,0x10,0,0x80,0,0,0xaa,0,0x38,0x9b,0x71];
            if (format.Length < 40 || U16(format,16) < 22 || format.Length < 18 + U16(format,16))
                throw new InvalidDataException("Truncated WAVE extensible format.");
            if (!format.Slice(24,16).SequenceEqual(pcmGuid)) throw new NotSupportedException("Only integer PCM WAVE extensible is implemented.");
            validDepth = U16(format,18);
            if (validDepth == 0) validDepth = depth;
            if (validDepth < 4 || validDepth > depth) throw new InvalidDataException("Invalid WAVE valid-bit depth.");
            codec = 1;
        }
        int[] samples;
        string name;
        switch (codec)
        {
            case 1:
                if (depth is not (8 or 16 or 24 or 32) || align != channels * depth / 8 || audio.Length % align != 0)
                    throw new NotSupportedException("Integer PCM WAVE requires aligned 8/16/24/32-bit containers.");
                int width = depth / 8, padding = depth - validDepth;
                if (audio.Length / width > SampleLimit) throw new InvalidDataException("PCM output exceeds 64 million samples.");
                samples = new int[audio.Length / width];
                for (int i = 0, offset = 0; i < samples.Length; i++, offset += width)
                {
                    if ((i & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int raw = depth switch {
                        8 => audio[offset] - 128,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(audio[offset..]),
                        24 => (audio[offset] | audio[offset+1] << 8 | audio[offset+2] << 16) << 8 >> 8,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(audio[offset..])
                    };
                    if (padding != 0 && ((long)raw & ((1L << padding) - 1)) != 0) throw new InvalidDataException("Nonzero unused PCM container bits.");
                    samples[i] = raw >> padding;
                }
                depth = validDepth; name = $"Luma original PCM {depth}-bit";
                break;
            case 6:
            case 7:
                if (depth != 8 || align != channels || audio.Length % channels != 0) throw new InvalidDataException("Invalid G.711 alignment.");
                if (audio.Length > SampleLimit) throw new InvalidDataException("G.711 output exceeds 64 million samples.");
                samples = new int[audio.Length];
                for (int i = 0; i < samples.Length; i++)
                {
                    if ((i & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    samples[i] = codec == 6 ? DecodeALaw(audio[i]) : DecodeMuLaw(audio[i]);
                }
                depth = 16; name = codec == 6 ? "Luma original G.711 A-law" : "Luma original G.711 μ-law";
                break;
            case 2:
                if (channels > 2 || depth != 4 || format.Length < 22) throw new InvalidDataException("Invalid Microsoft ADPCM format.");
                int extra = U16(format,16), perBlock = U16(format,18), count = U16(format,20);
                if (count is < 1 or > 256 || extra < 4 + count * 4 || format.Length < 18 + extra)
                    throw new InvalidDataException("Invalid Microsoft ADPCM coefficients/extension.");
                var coefficients = new (int First, int Second)[count];
                for (int i = 0; i < count; i++) coefficients[i] =
                    (BinaryPrimitives.ReadInt16LittleEndian(format[(22 + i * 4)..]), BinaryPrimitives.ReadInt16LittleEndian(format[(24 + i * 4)..]));
                int? frameCount = FactCount(fact);
                samples = MSAdpcmDecoder.Decode(audio, channels, align, perBlock, coefficients, frameCount, cancellationToken);
                depth = 16; name = "Luma original Microsoft ADPCM";
                break;
            case 0x11:
                samples = DecodeIma(audio, format, fact, channels, align, depth, cancellationToken);
                depth = 16; name = "Luma original IMA ADPCM";
                break;
            default:
                throw new NotSupportedException($"WAVE codec {codec} is not implemented by Luma.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (samples.Length == 0) throw new InvalidDataException("Empty WAVE audio.");
        return new AudioBuffer((int)sampleRate, channels, depth, samples, name);
    }

    private static int[] DecodeIma(ReadOnlySpan<byte> audio, ReadOnlySpan<byte> format, ReadOnlySpan<byte> fact,
        int channels, int align, int depth, CancellationToken cancellationToken)
    {
        if (channels > 2 || depth != 4 || format.Length < 20 || align < channels * 4 || audio.Length % align != 0)
            throw new InvalidDataException("Invalid IMA ADPCM blocks.");
        int extra = U16(format,16), declared = U16(format,18), expected = (align - channels * 4) * 2 / channels + 1;
        if (extra < 2 || format.Length < 18 + extra || declared != expected || (align - channels * 4) % (channels * 4) != 0)
            throw new InvalidDataException("IMA block sample count/extension mismatch.");
        long totalFrames = (long)(audio.Length / align) * expected;
        if (totalFrames > SampleLimit / channels) throw new InvalidDataException("IMA output exceeds 64 million samples.");
        int? factFrames = FactCount(fact);
        long frames = factFrames ?? totalFrames;
        if (frames > totalFrames) throw new InvalidDataException("IMA fact exceeds decoded frames.");
        int[] result = new int[(int)frames * channels]; int written = 0;
        for (int start = 0; start < audio.Length; start += align)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] predictors = new int[channels], indices = new int[channels];
            int[][] decoded = new int[channels][];
            for (int ch = 0; ch < channels; ch++)
            {
                int header = start + ch * 4;
                predictors[ch] = BinaryPrimitives.ReadInt16LittleEndian(audio[header..]); indices[ch] = audio[header+2];
                if (indices[ch] > 88 || audio[header+3] != 0) throw new InvalidDataException("Invalid IMA predictor header.");
                decoded[ch] = new int[expected]; decoded[ch][0] = predictors[ch];
            }
            int cursor = start + channels * 4, frame = 1;
            while (cursor < start + align)
            {
                if (((cursor-start) & 0x7fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int ch = 0; ch < channels; ch++)
                    for (int packed = 0; packed < 4; packed++)
                    {
                        int value = audio[cursor++];
                        for (int half = 0; half < 2; half++)
                        {
                            int nibble = half == 0 ? value & 15 : value >> 4, step = Steps[indices[ch]], delta = step >> 3;
                            if ((nibble & 1) != 0) delta += step >> 2;
                            if ((nibble & 2) != 0) delta += step >> 1;
                            if ((nibble & 4) != 0) delta += step;
                            predictors[ch] = Math.Clamp(predictors[ch] + ((nibble & 8) == 0 ? delta : -delta), short.MinValue, short.MaxValue);
                            indices[ch] = Math.Clamp(indices[ch] + IndexChanges[nibble & 7], 0, 88);
                            decoded[ch][frame + packed * 2 + half] = predictors[ch];
                        }
                    }
                frame += 8;
            }
            for (int i = 0; i < expected; i++)
                for (int ch = 0; ch < channels; ch++) if (written < result.Length) result[written++] = decoded[ch][i];
        }
        return result;
    }

    private static int U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static int? FactCount(ReadOnlySpan<byte> fact)
    {
        if (fact.IsEmpty) return null;
        if (fact.Length < 4) throw new InvalidDataException("Truncated WAVE fact sample count.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(fact);
        if (count > int.MaxValue) throw new InvalidDataException("WAVE fact sample count exceeds bounded PCM output.");
        return (int)count;
    }
    private static int DecodeMuLaw(byte value)
    {
        int code = value ^ 0xff, magnitude = (((code & 15) << 3) + 132) << ((code >> 4) & 7);
        return (code & 128) == 0 ? magnitude - 132 : 132 - magnitude;
    }
    private static int DecodeALaw(byte value)
    {
        int code = value ^ 0x55, exponent = code >> 4 & 7, magnitude = (code & 15) << 4;
        magnitude += exponent == 0 ? 8 : 264;
        if (exponent > 1) magnitude <<= exponent - 1;
        return (code & 128) == 0 ? -magnitude : magnitude;
    }
}
