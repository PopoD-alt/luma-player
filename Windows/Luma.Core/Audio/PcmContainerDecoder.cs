using System.Buffers.Binary;
using System.Text;

namespace Luma.Core.Audio;

/// <summary>
/// Original bounded AIFF/AIFC and Sun AU parsing and integer/G.711 reconstruction.
/// Apple AIFF-C specification (08/26/91), and Oracle au(5):
/// https://www.mmsp.ece.mcgill.ca/Documents/AudioFormats/AIFF/Docs/AIFF-C.9.26.91.pdf
/// https://docs.oracle.com/cd/E88353_01/html/E37852/au-5.html
/// </summary>
public static class PcmContainerDecoder
{
    public const int InputLimit = 268_435_456, SampleLimit = 64_000_000;

    public static AudioBuffer Decode(ReadOnlySpan<byte> source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > InputLimit) throw new NotSupportedException("Audio input exceeds 256 MiB.");
        if (source.Length >= 4 && source[..4].SequenceEqual(".snd"u8)) return DecodeAu(source, cancellationToken);
        if (source.Length >= 12 && source[..4].SequenceEqual("FORM"u8)) return DecodeAiff(source, cancellationToken);
        throw new InvalidDataException("Not an AIFF, AIFC or Sun AU audio file.");
    }

    static AudioBuffer DecodeAiff(ReadOnlySpan<byte> source, CancellationToken token)
    {
        bool aifc = source.Slice(8, 4).SequenceEqual("AIFC"u8);
        if (!aifc && !source.Slice(8, 4).SequenceEqual("AIFF"u8)) throw new NotSupportedException("Unsupported IFF audio form.");
        long declaredEnd = (long)U32(source, 4) + 8;
        if (declaredEnd != source.Length) throw new InvalidDataException("Truncated AIFF FORM or data outside its declared bounds.");
        ReadOnlySpan<byte> common = default, sound = default;
        bool hasCommon = false, hasSound = false, hasVersion = false;
        int cursor = 12, chunks = 0;
        while (cursor < source.Length)
        {
            if ((chunks++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (chunks > 1_000_000) throw new NotSupportedException("AIFF chunk-count limit exceeded.");
            if (source.Length - cursor < 8) throw new InvalidDataException("Truncated AIFF chunk header.");
            uint length = U32(source, cursor + 4);
            if (length > source.Length - cursor - 8) throw new InvalidDataException("AIFF chunk exceeds its FORM.");
            var tag = source.Slice(cursor, 4); var payload = source.Slice(cursor + 8, (int)length);
            if (tag.SequenceEqual("COMM"u8))
            {
                if (hasCommon) throw new InvalidDataException("Duplicate AIFF Common chunk.");
                hasCommon = true; common = payload;
            }
            else if (tag.SequenceEqual("SSND"u8))
            {
                if (hasSound) throw new InvalidDataException("Duplicate AIFF Sound Data chunk.");
                hasSound = true; sound = payload;
            }
            else if (aifc && tag.SequenceEqual("FVER"u8))
            {
                if (hasVersion || payload.Length != 4 || U32(payload, 0) != 0xa2805140)
                    throw new InvalidDataException("Invalid or duplicate AIFF-C format version.");
                hasVersion = true;
            }
            cursor += 8 + (int)length + ((int)length & 1);
            if (cursor > source.Length) throw new InvalidDataException("Missing AIFF chunk padding.");
        }
        if (!hasCommon || common.Length < 18 || !hasSound || sound.Length < 8 || (aifc && !hasVersion))
            throw new InvalidDataException("Missing or truncated AIFF Common, Sound Data or AIFF-C Version chunk.");
        int channels = U16(common, 0), depth = U16(common, 6), rate = ExtendedRate(common.Slice(8, 10));
        ValidateMetadata(rate, channels);
        uint frames = U32(common, 2);
        if (frames == 0 || frames > SampleLimit / channels) throw new NotSupportedException("AIFF frame count exceeds its bounded nonempty PCM output.");
        string codec = "NONE";
        if (aifc)
        {
            if (common.Length < 24) throw new InvalidDataException("Truncated AIFF-C compression type or name.");
            codec = Encoding.ASCII.GetString(common.Slice(18, 4));
            int nameSize = common[22] + 1;
            if (22 + nameSize + (nameSize & 1) > common.Length) throw new InvalidDataException("Truncated AIFF-C compression name.");
        }
        int companding = codec is "ulaw" or "ULAW" ? 1 : codec is "alaw" or "ALAW" ? 2 : 0;
        bool littleEndian = codec == "sowt";
        if (companding == 0 && codec is not ("NONE" or "twos" or "sowt")) throw new NotSupportedException("AIFF-C compression " + codec + " is not implemented.");
        if (companding != 0 ? depth is not (8 or 16) : depth is not (8 or 16 or 24 or 32))
            throw new NotSupportedException("AIFF PCM requires 8/16/24/32-bit samples; G.711 metadata requires 8/16 bits.");
        int count = checked((int)frames * channels), width = companding != 0 ? 1 : depth / 8;
        long expected = (long)count * width;
        uint offset = U32(sound, 0), blockSize = U32(sound, 4);
        if (offset > sound.Length - 8 || expected > sound.Length - 8 - offset) throw new InvalidDataException("AIFF sample frames exceed the Sound Data chunk.");
        long trailing = sound.Length - 8L - offset - expected;
        if ((blockSize == 0 && trailing != 0) || (blockSize != 0 && trailing >= blockSize))
            throw new InvalidDataException("AIFF has unexplained sample data beyond its declared frames.");
        int[] samples = DecodeSamples(sound.Slice(8 + (int)offset, (int)expected), count, depth, littleEndian, companding, token);
        return new AudioBuffer(rate, channels, companding == 0 ? depth : 16, samples,
            companding == 0 ? $"Luma original {(aifc ? "AIFC" : "AIFF")} PCM {depth}-bit" : $"Luma original AIFC G.711 {(companding == 1 ? "μ-law" : "A-law")}");
    }

    static AudioBuffer DecodeAu(ReadOnlySpan<byte> source, CancellationToken token)
    {
        if (source.Length < 24) throw new InvalidDataException("Truncated Sun AU header.");
        uint offset = U32(source, 4), length = U32(source, 8), encoding = U32(source, 12), sampleRate = U32(source, 16), channelCount = U32(source, 20);
        if (offset < 24 || offset > source.Length) throw new InvalidDataException("Invalid Sun AU data offset.");
        if (length == uint.MaxValue) length = (uint)(source.Length - offset);
        if (length != source.Length - offset) throw new InvalidDataException("Sun AU data size disagrees with file bounds.");
        if (sampleRate is < 1 or > 1048575 || channelCount is < 1 or > 8) throw new InvalidDataException("Invalid Sun AU sample rate or channel count.");
        int channels = (int)channelCount, rate = (int)sampleRate;
        int companding = encoding == 1 ? 1 : encoding == 27 ? 2 : 0;
        int depth = encoding switch { 1 or 27 => 16, 2 => 8, 3 => 16, 4 => 24, 5 => 32, _ => throw new NotSupportedException("Sun AU encoding " + encoding + " is not implemented.") };
        int width = companding == 0 ? depth / 8 : 1;
        if (length == 0 || length % (channels * width) != 0) throw new InvalidDataException("Sun AU data does not contain complete nonempty sample frames.");
        if (length / width > SampleLimit) throw new NotSupportedException("Sun AU output exceeds 64 million samples.");
        int count = (int)length / width;
        int[] samples = DecodeSamples(source.Slice((int)offset, (int)length), count, depth, false, companding, token);
        return new AudioBuffer(rate, channels, depth, samples, companding == 0 ? $"Luma original AU PCM {depth}-bit" : $"Luma original AU G.711 {(companding == 1 ? "μ-law" : "A-law")}");
    }

    static int[] DecodeSamples(ReadOnlySpan<byte> data, int count, int depth, bool littleEndian, int companding, CancellationToken token)
    {
        int[] samples = new int[count]; int width = companding == 0 ? depth / 8 : 1;
        for (int i = 0, cursor = 0; i < count; i++, cursor += width)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            if (companding != 0)
            {
                int value = data[cursor];
                if (companding == 1)
                {
                    value = (~value) & 255; int magnitude = (((value & 15) << 3) + 132) << ((value >> 4) & 7);
                    samples[i] = (value & 128) != 0 ? 132 - magnitude : magnitude - 132;
                }
                else
                {
                    value ^= 85; int exponent = (value >> 4) & 7, magnitude = (value & 15) << 4;
                    magnitude = exponent == 0 ? magnitude + 8 : (magnitude + 264) << (exponent - 1);
                    samples[i] = (value & 128) != 0 ? magnitude : -magnitude;
                }
                continue;
            }
            uint raw = 0;
            for (int j = 0; j < width; j++) raw |= (uint)data[cursor + j] << (littleEndian ? j * 8 : (width - 1 - j) * 8);
            samples[i] = depth == 32 ? unchecked((int)raw) : (int)(raw << (32 - depth)) >> (32 - depth);
        }
        token.ThrowIfCancellationRequested();
        return samples;
    }

    static int ExtendedRate(ReadOnlySpan<byte> data)
    {
        int signExponent = U16(data, 0), exponent = signExponent & 32767; ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(data[2..]);
        if ((signExponent & 32768) != 0 || exponent is 0 or 32767 || (mantissa & (1UL << 63)) == 0)
            throw new InvalidDataException("AIFF sample rate must be positive, finite and normalized.");
        int power = exponent - 16383;
        if (power is < 0 or > 19) throw new NotSupportedException("AIFF sample rate is outside the supported integer-Hz range.");
        int shift = 63 - power;
        if ((mantissa & ((1UL << shift) - 1)) != 0) throw new NotSupportedException("Fractional AIFF sample rates are not implemented.");
        return (int)(mantissa >> shift);
    }

    static void ValidateMetadata(int rate, int channels)
    {
        if (rate is < 1 or > 1048575 || channels is < 1 or > 8) throw new InvalidDataException("Invalid AIFF sample rate or channel count.");
    }
    static int U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
