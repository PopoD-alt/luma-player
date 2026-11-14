using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Luma.Core.Audio;

/// <summary>Original Microsoft ADPCM reconstruction ported from Luma's Swift implementation.</summary>
public static class MSAdpcmDecoder
{
    public static readonly (int First, int Second)[] StandardCoefficients =
        [(256,0), (512,-256), (0,0), (192,64), (240,0), (460,-208), (392,-232)];
    private static readonly long[] Adaptation = [230,230,230,230,307,409,512,614,768,614,512,409,307,230,230,230];

    // Format: Microsoft Multimedia Data Standards Update, rev3 (1994), pp22–26:
    // https://elm-chan.org/junk/adpcm/RIFF_NEW.pdf . Quantizer constants are
    // standardized data: https://openal.org/pipermail/openal/2014-July/000182.html .
    public static int[] Decode(ReadOnlySpan<byte> blocks, int channels, int blockAlign, int samplesPerBlock,
        IReadOnlyList<(int First, int Second)> coefficients, int? sampleCount = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channels is not (1 or 2)) throw new NotSupportedException("Microsoft ADPCM supports mono/stereo only.");
        int header = 7 * channels;
        if (blocks.Length > WaveDecoder.InputLimit) throw new InvalidDataException("Microsoft ADPCM input exceeds 256 MiB.");
        if (blockAlign < header || blockAlign > 65535 || samplesPerBlock < 2 || samplesPerBlock > 65535 ||
            samplesPerBlock != 2 + (blockAlign - header) * 2 / channels)
            throw new InvalidDataException("Microsoft ADPCM block alignment and SamplesPerBlock disagree.");
        if (coefficients.Count is < 1 or > 256) throw new InvalidDataException("Invalid Microsoft ADPCM coefficient count.");
        foreach (var pair in coefficients)
            if (pair.First is < short.MinValue or > short.MaxValue || pair.Second is < short.MinValue or > short.MaxValue)
                throw new InvalidDataException("Microsoft ADPCM coefficient exceeds signed 16-bit range.");
        if (blocks.IsEmpty) throw new InvalidDataException("Empty Microsoft ADPCM blocks.");
        int complete = blocks.Length / blockAlign, remainder = blocks.Length % blockAlign;
        if (remainder != 0 && remainder < header) throw new InvalidDataException("Truncated Microsoft ADPCM final header.");
        long availableFrames = (long)complete * samplesPerBlock + (remainder == 0 ? 0 : 2 + (remainder - header) * 2 / channels);
        if (availableFrames > WaveDecoder.SampleLimit / channels) throw new InvalidDataException("Microsoft ADPCM output exceeds 64 million scalar samples.");
        long requested = sampleCount ?? availableFrames;
        if (requested < 0 || requested > availableFrames) throw new InvalidDataException("Microsoft ADPCM fact count exceeds available frames.");
        int[] output = new int[(int)requested * channels];
        int written = 0, nibbles = 0;
        for (int offset = 0; offset < blocks.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(blockAlign, blocks.Length - offset);
            var states = new ChannelState[channels];
            for (int channel = 0; channel < channels; channel++)
            {
                int predictor = blocks[offset + channel];
                if (predictor >= coefficients.Count) throw new InvalidDataException("Microsoft ADPCM predictor exceeds the declared table.");
                var pair = coefficients[predictor];
                states[channel] = new ChannelState(pair.First, pair.Second,
                    BinaryPrimitives.ReadInt16LittleEndian(blocks[(offset + channels + channel * 2)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(blocks[(offset + channels * 3 + channel * 2)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(blocks[(offset + channels * 5 + channel * 2)..]));
            }
            foreach (var state in states) if (written < output.Length) output[written++] = (int)state.Oldest;
            foreach (var state in states) if (written < output.Length) output[written++] = (int)state.Newest;
            for (int cursor = offset + header; cursor < offset + length; cursor++)
                for (int half = 0; half < 2; half++)
                {
                    if ((nibbles++ & 0xfff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int nibble = half == 0 ? blocks[cursor] >> 4 : blocks[cursor] & 15;
                    int sample = states[channels == 1 ? 0 : half].Advance(nibble);
                    if (written < output.Length) output[written++] = sample;
                }
            offset += length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private sealed class ChannelState(int first, int second, long delta, long newest, long oldest)
    {
        public long Newest = newest, Oldest = oldest;
        private long delta = delta;
        public int Advance(int nibble)
        {
            if (delta > long.MaxValue / 768) throw new InvalidDataException("Microsoft ADPCM adaptive scale exceeds exact 64-bit arithmetic.");
            long predicted = (Newest * first + Oldest * second) >> 8;
            long correction = (nibble < 8 ? nibble : nibble - 16) * delta;
            int sample = (int)Math.Clamp(predicted + correction, short.MinValue, short.MaxValue);
            Oldest = Newest; Newest = sample;
            delta = Math.Max(16, (delta * Adaptation[nibble]) >> 8);
            return sample;
        }
    }
}
