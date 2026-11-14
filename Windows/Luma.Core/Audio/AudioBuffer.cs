using System;
using System.Buffers.Binary;
using System.IO;

namespace Luma.Core.Audio;

/// <summary>Signed, unscaled, interleaved PCM samples at the decoded source depth.</summary>
public sealed record AudioBuffer(int SampleRate, int Channels, int BitsPerSample, int[] Samples, string CodecName)
{
    public double Duration => (double)Samples.Length / Channels / SampleRate;

    /// <summary>Lossless integer WAVE; non-byte depths use valid bits in an extensible PCM container.</summary>
    public byte[] ToWave()
    {
        if (SampleRate is < 1 or > 1048575 || Channels is < 1 or > 8 || BitsPerSample is < 4 or > 32 || Samples.Length % Channels != 0)
            throw new InvalidDataException("Invalid PCM buffer metadata.");
        int containerBits = (BitsPerSample + 7) / 8 * 8, width = containerBits / 8;
        bool extended = Channels > 2 || containerBits != BitsPerSample;
        int formatSize = extended ? 40 : 16;
        long payloadLong = (long)Samples.Length * width;
        if (payloadLong > int.MaxValue - 128) throw new InvalidDataException("PCM WAVE exceeds the bounded output size.");
        int payload = (int)payloadLong, dataStart = 28 + formatSize;
        byte[] result = new byte[dataStart + payload + (payload & 1)];
        "RIFF"u8.CopyTo(result); Put32(result, 4, (uint)(result.Length - 8));
        "WAVEfmt "u8.CopyTo(result.AsSpan(8)); Put32(result, 16, (uint)formatSize);
        Put16(result, 20, extended ? 0xfffe : 1); Put16(result, 22, Channels); Put32(result, 24, (uint)SampleRate);
        Put32(result, 28, checked((uint)(SampleRate * Channels * width))); Put16(result, 32, Channels * width); Put16(result, 34, containerBits);
        if (extended)
        {
            int[] masks = [0x4, 0x3, 0x7, 0x33, 0x37, 0x3f, 0x70f, 0x63f];
            Put16(result, 36, 22); Put16(result, 38, BitsPerSample); Put32(result, 40, (uint)masks[Channels - 1]);
            new byte[] { 1,0,0,0,0,0,0x10,0,0x80,0,0,0xaa,0,0x38,0x9b,0x71 }.CopyTo(result, 44);
        }
        "data"u8.CopyTo(result.AsSpan(dataStart - 8)); Put32(result, dataStart - 4, (uint)payload);
        long minimum = -(1L << (BitsPerSample - 1)), maximum = (1L << (BitsPerSample - 1)) - 1;
        int shift = containerBits - BitsPerSample, cursor = dataStart;
        foreach (int sample in Samples)
        {
            if (sample < minimum || sample > maximum) throw new InvalidDataException("PCM sample exceeds its declared source depth.");
            long aligned = (long)sample << shift;
            if (containerBits == 8) aligned += 128;
            for (int i = 0; i < width; i++) result[cursor++] = (byte)(aligned >> (i * 8));
        }
        return result;
    }

    private static void Put16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), checked((ushort)value));
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
}
