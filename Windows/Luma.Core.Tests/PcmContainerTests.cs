using System.Buffers.Binary;
using System.Text;
using Luma.Core.Audio;

namespace Luma.Core.Tests;

/// <summary>Independent byte-level AIFF/AIFC/AU sample goldens, without an OS or external codec.</summary>
public static class PcmContainerTests
{
    public static void Run(string fixturesRoot)
    {
        int vectors = 0;
        foreach (int depth in new[] { 8, 16, 24, 32 })
        {
            int minimum = depth == 32 ? int.MinValue : -(1 << (depth - 1));
            int maximum = depth == 32 ? int.MaxValue : (1 << (depth - 1)) - 1;
            int[] expected = [minimum, maximum, -1, 0, 1, 93];
            foreach (int channels in new[] { 1, 2 })
            {
                Verify(Aiff(null, depth, channels, Pcm(expected, depth)), expected, depth, channels, 44100, "AIFF signed big-endian extrema"); vectors++;
                foreach (string codec in new[] { "NONE", "twos", "sowt" })
                {
                    Verify(Aiff(codec, depth, channels, Pcm(expected, depth, codec == "sowt")), expected, depth, channels, 44100, "AIFC " + codec); vectors++;
                }
                uint encoding = depth switch { 8 => 2, 16 => 3, 24 => 4, _ => 5 };
                Verify(Au(encoding, channels, Pcm(expected, depth)), expected, depth, channels, 8000, "AU signed big-endian extrema"); vectors++;
                Verify(Au(encoding, channels, Pcm(expected, depth), true), expected, depth, channels, 8000, "AU unknown data size"); vectors++;
            }
        }
        foreach ((string codec, uint auEncoding, byte[] raw, int[] expected) in new[] {
            ("ulaw", 1u, new byte[] { 255, 127, 128, 0, 254, 126 }, new[] { 0, 0, 32124, -32124, 8, -8 }),
            ("alaw", 27u, new byte[] { 213, 85, 170, 42, 212, 84 }, new[] { 8, -8, 32256, -32256, 24, -24 }) })
        {
            foreach (int channels in new[] { 1, 2 })
            {
                Verify(Aiff(codec, 16, channels, raw), expected, 16, channels, 44100, "AIFC G.711 exact golden"); vectors++;
                Verify(Aiff(codec.ToUpperInvariant(), 8, channels, raw), expected, 16, channels, 44100, "AIFC uppercase G.711 metadata variant"); vectors++;
                Verify(Au(auEncoding, channels, raw), expected, 16, channels, 8000, "AU G.711 exact golden"); vectors++;
            }
        }
        // SSND precedes COMM; one odd unknown chunk, SSND offset and disk-alignment tail are not audio.
        Verify(Aiff(null, 24, 1, [255, 255, 255, 0, 0, 1], soundFirst: true, soundOffset: 3), [-1, 1], 24, 1, 44100, "AIFF chunk order/odd padding/SSND offset");
        Verify(Aiff("NONE", 16, 1, [255, 255, 0, 1], soundOffset: 4, blockSize: 8, tail: 4), [-1, 1], 16, 1, 44100, "AIFF disk-block tail ignored");
        int[] multichannel = [-32768, -3, -2, -1, 0, 1, 2, 32767];
        Verify(Aiff(null, 16, 8, Pcm(multichannel, 16)), multichannel, 16, 8, 44100, "AIFF eight-channel interleaving");
        Verify(Au(3, 8, Pcm(multichannel, 16)), multichannel, 16, 8, 8000, "AU eight-channel interleaving");
        Verify(Aiff(null, 16, 1, [255, 255], rate: [0x3f, 0xff, 0x80, 0, 0, 0, 0, 0, 0, 0]), [-1], 16, 1, 1, "AIFF exact one-Hz boundary");
        Verify(Aiff(null, 16, 1, [0, 1], rate: [0x40, 0x12, 0xff, 0xff, 0xf0, 0, 0, 0, 0, 0]), [1], 16, 1, 1048575, "AIFF exact upper rate boundary");
        byte[] nonzeroSpan = new byte[3 + Aiff(null, 8, 1, [128, 127]).Length];
        Aiff(null, 8, 1, [128, 127]).CopyTo(nonzeroSpan, 3);
        Check(PcmContainerDecoder.Decode(nonzeroSpan.AsSpan(3)).Samples.SequenceEqual(new[] { -128, 127 }), "AIFF nonzero-offset input span");
        MalformedTests();
        Console.WriteLine($"PASS: {vectors} exact original AIFF/AIFC/AU PCM8/16/24/32 and G.711 vectors, signed extrema/endian/channel ordering, source-depth WAVE round-trips");
        Console.WriteLine("PASS: AIFF chunk order/odd padding/SSND offset/block tail, AU annotation/unknown length, strict 80-bit integer rates, malformed/truncated/unsupported bounds and cancellation");
    }

    static void MalformedTests()
    {
        byte[] aiff = Aiff(null, 16, 1, [255, 255, 0, 1]);
        byte[] aifc = Aiff("sowt", 16, 1, [255, 255, 1, 0]);
        byte[] au = Au(3, 1, [255, 255, 0, 1]);
        foreach (byte[] full in new[] { aiff, aifc, au })
            for (int length = 0; length < full.Length; length++)
            {
                byte[] partial = full[..length]; Reject(() => PcmContainerDecoder.Decode(partial), "Every truncated header/data boundary " + length);
            }
        Reject(() => PcmContainerDecoder.Decode(Aiff("fl32", 32, 1, [0, 0, 0, 0])), "AIFC float remains unsupported");
        Reject(() => PcmContainerDecoder.Decode(Aiff("ima4", 16, 1, [0, 0])), "AIFC compressed codec is not mislabeled PCM");
        Reject(() => PcmContainerDecoder.Decode(Aiff("NONE", 16, 1, [0, 0], version: false)), "AIFC requires format version");
        Reject(() => PcmContainerDecoder.Decode(Aiff(null, 12, 1, [0, 0])), "AIFF unsupported PCM depth");
        Reject(() => PcmContainerDecoder.Decode(Aiff(null, 16, 0, [0, 0])), "AIFF zero channels");
        Reject(() => PcmContainerDecoder.Decode(Aiff(null, 16, 9, new byte[18])), "AIFF channel limit");
        Reject(() => PcmContainerDecoder.Decode(Aiff(null, 16, 1, [0, 0], frames: 64_000_001)), "AIFF decoded resource count before allocation");
        Reject(() => PcmContainerDecoder.Decode(Aiff(null, 16, 1, [0, 0], frames: 2)), "AIFF declared frames exceed SSND");
        Reject(() => PcmContainerDecoder.Decode(Aiff(null, 16, 1, [0, 0], frames: 0)), "AIFF empty data");
        Reject(() => PcmContainerDecoder.Decode(Aiff(null, 16, 1, [0, 0], tail: 1)), "AIFF unexplained extra audio");
        Reject(() => PcmContainerDecoder.Decode(Aiff(null, 16, 1, [0, 0], blockSize: 4, tail: 4)), "AIFF excessive block tail");
        foreach (byte[] rate in new byte[][] {
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], // zero
            [0xc0, 0x0e, 0xac, 0x44, 0, 0, 0, 0, 0, 0], // negative
            [0x7f, 0xff, 0x80, 0, 0, 0, 0, 0, 0, 0], // infinity
            [0x7f, 0xff, 0xc0, 0, 0, 0, 0, 0, 0, 0], // NaN
            [0x40, 0x0e, 0x2c, 0x44, 0, 0, 0, 0, 0, 0], // unnormalized
            [0x40, 0x0e, 0xac, 0x44, 0, 0, 0, 0, 0, 1], // fractional
            [0x40, 0x13, 0x80, 0, 0, 0, 0, 0, 0, 0] }) // 1048576 Hz
            Reject(() => PcmContainerDecoder.Decode(Aiff(null, 16, 1, [0, 0], rate: rate)), "Invalid or unsupported extended sample rate");
        void Corrupt(byte[] source, int offset, uint value, string label)
        {
            byte[] invalid = (byte[])source.Clone(); Put32(invalid, offset, value); Reject(() => PcmContainerDecoder.Decode(invalid), label);
        }
        Corrupt(aiff, Find(aiff, "SSND") + 8, uint.MaxValue, "SSND offset overflow");
        Corrupt(aifc, Find(aifc, "FVER") + 8, 0, "Unknown AIFC version");
        Corrupt(aiff, Find(aiff, "COMM") + 4, uint.MaxValue, "AIFF chunk length overflow");
        byte[] badName = (byte[])aifc.Clone(); badName[Find(badName, "COMM") + 8 + 22] = 255;
        Reject(() => PcmContainerDecoder.Decode(badName), "Truncated AIFF compression name");
        Reject(() => PcmContainerDecoder.Decode(Form("AIFF", Chunk("COMM", new byte[18]), Chunk("COMM", new byte[18]), Chunk("SSND", new byte[10]))), "Duplicate AIFF COMM");
        Reject(() => PcmContainerDecoder.Decode(Form("AIFF", Chunk("COMM", new byte[18]), Chunk("SSND", new byte[10]), Chunk("SSND", new byte[10]))), "Duplicate AIFF SSND");
        Reject(() => PcmContainerDecoder.Decode(Form("AIFC", Chunk("FVER", [0xa2, 0x80, 0x51, 0x40]), Chunk("FVER", [0xa2, 0x80, 0x51, 0x40]), Chunk("COMM", new byte[24]), Chunk("SSND", new byte[10]))), "Duplicate AIFC version");
        Reject(() => PcmContainerDecoder.Decode(Form("AIFF", Chunk("SSND", new byte[10]))), "Missing AIFF Common chunk");
        Reject(() => PcmContainerDecoder.Decode(Form("AIFF", Chunk("COMM", new byte[18]))), "Missing AIFF Sound Data chunk");
        byte[] missingPad = Aiff(null, 8, 1, [1]); Array.Resize(ref missingPad, missingPad.Length - 1); Put32(missingPad, 4, (uint)(missingPad.Length - 8));
        Reject(() => PcmContainerDecoder.Decode(missingPad), "Missing odd-chunk padding");
        Corrupt(au, 4, 23, "AU offset inside header"); Corrupt(au, 4, uint.MaxValue, "AU offset overflow");
        Corrupt(au, 8, 5, "AU declared audio exceeds file"); Corrupt(au, 8, 2, "AU unexplained trailing audio");
        Corrupt(au, 12, 6, "AU float remains unsupported"); Corrupt(au, 12, 23, "AU ADPCM remains unsupported");
        Corrupt(au, 16, 0, "AU zero rate"); Corrupt(au, 16, 1048576, "AU rate cap");
        Corrupt(au, 20, 0, "AU zero channels"); Corrupt(au, 20, 9, "AU channel cap");
        Reject(() => PcmContainerDecoder.Decode(Au(3, 2, [0, 0])), "AU incomplete channel frame");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        foreach (byte[] bytes in new[] { aiff, aifc, au })
        {
            try { PcmContainerDecoder.Decode(bytes, cancelled.Token); throw new Exception("Cancelled audio parsing ran."); }
            catch (OperationCanceledException) { }
        }
    }

    static void Verify(byte[] source, int[] expected, int depth, int channels, int rate, string label)
    {
        AudioBuffer result = PcmContainerDecoder.Decode(source);
        Check(result.BitsPerSample == depth && result.Channels == channels && result.SampleRate == rate && result.Samples.SequenceEqual(expected), label);
        var wave = WaveDecoder.Decode(result.ToWave());
        Check(wave.Samples.SequenceEqual(expected) && wave.BitsPerSample == depth && wave.Channels == channels && wave.SampleRate == rate, label + " source-depth WAVE export");
    }
    static byte[] Aiff(string? codec, int depth, int channels, byte[] data, bool soundFirst = false, int soundOffset = 0, uint blockSize = 0, int tail = 0, uint? frames = null, byte[]? rate = null, bool version = true)
    {
        byte[] common = new byte[codec is null ? 18 : 24]; Put16(common, 0, channels);
        int width = codec is "ulaw" or "ULAW" or "alaw" or "ALAW" ? 1 : (depth + 7) / 8;
        Put32(common, 2, frames ?? (uint)(data.Length / Math.Max(1, channels * width))); Put16(common, 6, depth);
        (rate ?? new byte[] { 0x40, 0x0e, 0xac, 0x44, 0, 0, 0, 0, 0, 0 }).CopyTo(common, 8);
        if (codec is not null) Encoding.ASCII.GetBytes(codec).CopyTo(common, 18); // Empty, even-sized compressionName pstring.
        byte[] sound = new byte[8 + soundOffset + data.Length + tail]; Put32(sound, 0, (uint)soundOffset); Put32(sound, 4, blockSize); data.CopyTo(sound, 8 + soundOffset);
        List<byte[]> chunks = [Chunk("ANNO", [65, 66, 67])];
        if (codec is not null && version) chunks.Add(Chunk("FVER", [0xa2, 0x80, 0x51, 0x40]));
        if (soundFirst) chunks.Add(Chunk("SSND", sound));
        chunks.Add(Chunk("COMM", common));
        if (!soundFirst) chunks.Add(Chunk("SSND", sound));
        return Form(codec is null ? "AIFF" : "AIFC", chunks.ToArray());
    }
    static byte[] Au(uint encoding, int channels, byte[] data, bool unknownSize = false)
    {
        byte[] result = new byte[32 + data.Length]; ".snd"u8.CopyTo(result); Put32(result, 4, 32); Put32(result, 8, unknownSize ? uint.MaxValue : (uint)data.Length);
        Put32(result, 12, encoding); Put32(result, 16, 8000); Put32(result, 20, (uint)channels);
        Encoding.ASCII.GetBytes("note1234").CopyTo(result, 24); data.CopyTo(result, 32); return result;
    }
    static byte[] Form(string format, params byte[][] chunks)
    {
        byte[] result = new byte[12 + chunks.Sum(c => c.Length)]; "FORM"u8.CopyTo(result); Put32(result, 4, (uint)(result.Length - 8)); Encoding.ASCII.GetBytes(format).CopyTo(result, 8);
        int cursor = 12; foreach (byte[] chunk in chunks) { chunk.CopyTo(result, cursor); cursor += chunk.Length; } return result;
    }
    static byte[] Chunk(string name, byte[] payload)
    {
        byte[] result = new byte[8 + payload.Length + (payload.Length & 1)]; Encoding.ASCII.GetBytes(name).CopyTo(result, 0); Put32(result, 4, (uint)payload.Length); payload.CopyTo(result, 8); return result;
    }
    static byte[] Pcm(int[] samples, int depth, bool little = false)
    {
        int width = depth / 8; byte[] result = new byte[samples.Length * width];
        for (int i = 0; i < samples.Length; i++) for (int j = 0; j < width; j++) result[i * width + j] = unchecked((byte)(samples[i] >> (little ? 8 * j : 8 * (width - 1 - j))));
        return result;
    }
    static int Find(byte[] data, string tag)
    {
        for (int cursor = 12; cursor < data.Length; cursor += 8 + (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 4)) + (int)(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 4)) & 1))
            if (data.AsSpan(cursor, 4).SequenceEqual(Encoding.ASCII.GetBytes(tag))) return cursor;
        throw new Exception("Test chunk not found: " + tag);
    }
    static void Put16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)value);
    static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action, string label)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new Exception("Expected audio container rejection: " + label);
    }
}
