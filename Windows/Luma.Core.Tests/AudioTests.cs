using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Luma.Core.Audio;

namespace Luma.Core.Tests;

/// <summary>Platform-independent exact checks using Luma's original synthetic fixtures; no native or external decoder.</summary>
public static class AudioTests
{
    public static void Run(string fixturesRoot)
    {
        PcmTests();
        byte[] imaBytes = File.ReadAllBytes(Path.Combine(fixturesRoot, "Luma-IMA.wav"));
        AudioBuffer ima = WaveDecoder.Decode(imaBytes);
        Check(ima.SampleRate == 8000 && ima.Channels == 1 && ima.BitsPerSample == 16 && ima.Samples.Length == 505 * 64, "IMA metadata/count");
        Check(ima.Samples.Take(505).SequenceEqual(Enumerable.Range(0,505)), "IMA mono independent ramp golden");
        RoundTrip(ima);
        AudioBuffer stereo = WaveDecoder.Decode(File.ReadAllBytes(Path.Combine(fixturesRoot, "Luma-IMA-Stereo.wav")));
        Check(stereo.Channels == 2 && stereo.Samples.SequenceEqual(Enumerable.Range(0,9).SelectMany(i => new[] {i,100-i})), "IMA stereo channel grouping/nibble order");
        RoundTrip(stereo);
        AudioBuffer alaw = WaveDecoder.Decode(File.ReadAllBytes(Path.Combine(fixturesRoot, "Luma-ALaw.wav")));
        AudioBuffer mulaw = WaveDecoder.Decode(File.ReadAllBytes(Path.Combine(fixturesRoot, "Luma-MuLaw.wav")));
        Check(alaw.Samples.Take(4).SequenceEqual(new[] {8,-8,32256,-32256}), "G.711 A-law independent golden");
        Check(mulaw.Samples.Take(4).SequenceEqual(new[] {0,0,32124,-32124}), "G.711 mu-law independent golden");
        RoundTrip(alaw); RoundTrip(mulaw);
        int msCount = MsAdpcmTests(Path.Combine(fixturesRoot, "ms-adpcm"));
        WaveMalformedTests(imaBytes);
        int flacCount = FlacTests(Path.Combine(fixturesRoot, "flac"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        RejectCancellation(() => WaveDecoder.Decode(imaBytes, cancelled.Token));
        byte[] flac = File.ReadAllBytes(Path.Combine(fixturesRoot, "flac", "constant-8.flac"));
        RejectCancellation(() => FlacDecoder.Decode(flac, cancelled.Token));
        RejectCancellation(() => MSAdpcmDecoder.Decode([0,16,0,0,0,0,0],1,7,2,MSAdpcmDecoder.StandardCoefficients,cancellationToken:cancelled.Token));
        Console.WriteLine($"PASS: original Windows WAVE PCM8/16/24/32, G.711, IMA, {msCount} exact Microsoft ADPCM and {flacCount} exact FLAC goldens; lossless source-depth WAVE round-trips");
        Console.WriteLine("PASS: original Windows audio malformed/truncated inputs, CRC8/CRC16/MD5, predictor/format limits and cancellation");
    }

    private static void PcmTests()
    {
        foreach (int depth in new[] {8,16,24,32})
        {
            int minimum = depth == 32 ? int.MinValue : -(1 << (depth-1));
            int maximum = depth == 32 ? int.MaxValue : (1 << (depth-1))-1;
            int[] expected = [minimum,minimum+1,-1,0,1,maximum-1,maximum];
            byte[] raw = new byte[expected.Length * depth / 8];
            for (int i = 0; i < expected.Length; i++)
            {
                long sample = depth == 8 ? expected[i] + 128 : expected[i];
                for (int j = 0; j < depth / 8; j++) raw[i * depth / 8 + j] = (byte)(sample >> (8*j));
            }
            AudioBuffer decoded = WaveDecoder.Decode(Wave(Format(1,1,8000,depth/8,depth),raw));
            Check(decoded.Samples.SequenceEqual(expected) && decoded.BitsPerSample == depth, $"Unscaled signed PCM{depth} extrema");
            RoundTrip(decoded);
        }
        foreach (int depth in new[] {4,7,12,20,31})
        {
            int[] expected = [-(1 << (depth-1)),-1,0,1,(1 << (depth-1))-1];
            RoundTrip(new AudioBuffer(44100,1,depth,expected,"synthetic source-depth golden"));
        }
        RoundTrip(new AudioBuffer(48000,6,16,Enumerable.Range(0,24).Select(x=>x-12).ToArray(),"six-channel original golden"));
        foreach (int rate in new[] {1,1000,655350,1048575})
            RoundTrip(new AudioBuffer(rate,1,16,[-1,0,1],"FLAC STREAMINFO rate precision"));
        AudioBuffer single24 = new(8000,1,24,[-1],"odd-byte padded golden");
        byte[] odd = single24.ToWave();
        Check(odd.Length == 48 && odd[^1] == 0, "Odd-length PCM RIFF pad");
        RoundTrip(single24);
        Reject(() => new AudioBuffer(44100,1,4,[8],"out of range").ToWave(), "PCM source-depth range");
        Console.WriteLine("PASS: PCM extrema and non-byte4/7/12/20/31-bit/six-channel extensible WAVE sample/depth preservation");
    }

    private static int MsAdpcmTests(string root)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"manifest.json")));
        int count = 0;
        foreach (JsonElement vector in manifest.RootElement.GetProperty("vectors").EnumerateArray())
        {
            string name = vector.GetProperty("name").GetString()!;
            int[] expected = IntArray(vector.GetProperty("expected_samples"));
            var decoded = WaveDecoder.Decode(File.ReadAllBytes(Path.Combine(root,name+".wav")));
            Check(decoded.Samples.SequenceEqual(expected) && decoded.Channels == vector.GetProperty("channels").GetInt32() &&
                decoded.SampleRate == vector.GetProperty("sample_rate").GetInt32() && decoded.BitsPerSample == 16, "MS ADPCM WAVE golden: " + name);
            var pairs = vector.GetProperty("coefficients").EnumerateArray().Select(p=>(p[0].GetInt32(),p[1].GetInt32())).ToArray();
            byte[] blocks = File.ReadAllBytes(Path.Combine(root,name+".blocks"));
            int[] direct = MSAdpcmDecoder.Decode(blocks,decoded.Channels,vector.GetProperty("block_align").GetInt32(),
                vector.GetProperty("samples_per_block").GetInt32(),pairs,vector.GetProperty("sample_count").GetInt32());
            Check(direct.SequenceEqual(expected), "Direct MS ADPCM sample golden: " + name);
            RoundTrip(decoded); count++;
        }
        byte[] header = [0,16,0,100,0,84,0];
        Check(MSAdpcmDecoder.Decode(header,1,7,2,MSAdpcmDecoder.StandardCoefficients).SequenceEqual(new[] {84,100}), "MS ADPCM seed order");
        for (int length = 1; length < 7; length++)
        {
            byte[] shortHeader = header[..length];
            Reject(() => MSAdpcmDecoder.Decode(shortHeader,1,7,2,MSAdpcmDecoder.StandardCoefficients), "MS truncated seed header");
        }
        byte[] invalid = (byte[])header.Clone(); invalid[0] = 7;
        Reject(() => MSAdpcmDecoder.Decode(invalid,1,7,2,MSAdpcmDecoder.StandardCoefficients), "MS undeclared predictor");
        Reject(() => MSAdpcmDecoder.Decode(header,1,7,3,MSAdpcmDecoder.StandardCoefficients), "MS spp alignment");
        Reject(() => MSAdpcmDecoder.Decode(header,1,7,2,[]), "MS empty coefficients");
        Reject(() => MSAdpcmDecoder.Decode(header,1,7,2,[(32768,0)]), "MS coefficient range");
        Reject(() => MSAdpcmDecoder.Decode(header,1,7,2,MSAdpcmDecoder.StandardCoefficients,3), "MS fact exceeds available");
        byte[] explosive = new byte[256]; new byte[]{0,255,127,0,0,0,0}.CopyTo(explosive,0); Array.Fill(explosive,(byte)0x77,7,249);
        Reject(() => MSAdpcmDecoder.Decode(explosive,1,256,500,MSAdpcmDecoder.StandardCoefficients), "MS adaptive arithmetic overflow");
        byte[] custom = [255,16,0,231,255,75,0,0];
        var table = Enumerable.Repeat((256,0),255).Append((0,256)).ToArray();
        Check(MSAdpcmDecoder.Decode(custom,1,256,500,table).SequenceEqual(new[] {75,-25,75,-25}), "MS custom predictor255");
        return count;
    }

    private static int FlacTests(string root)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"manifest.json")));
        int count = 0;
        foreach (JsonElement vector in manifest.RootElement.EnumerateArray())
        {
            string name = vector.GetProperty("file").GetString()!;
            var decoded = FlacDecoder.Decode(File.ReadAllBytes(Path.Combine(root,name)));
            Check(decoded.Samples.SequenceEqual(IntArray(vector.GetProperty("samples"))) &&
                decoded.SampleRate == vector.GetProperty("sampleRate").GetInt32() && decoded.Channels == vector.GetProperty("channels").GetInt32() &&
                decoded.BitsPerSample == vector.GetProperty("bitsPerSample").GetInt32(), "FLAC exact metadata/samples: " + name);
            RoundTrip(decoded); count++;
        }
        byte[] constant = File.ReadAllBytes(Path.Combine(root,"constant-8.flac"));
        for (int length = 0; length < constant.Length; length++)
        {
            byte[] truncated = constant[..length]; Reject(()=>FlacDecoder.Decode(truncated), "FLAC truncation " + length);
        }
        int start = FlacAudioStart(constant);
        void Corrupt(int offset, byte xor, string label)
        {
            byte[] corrupt = (byte[])constant.Clone(); corrupt[offset] ^= xor;
            Reject(()=>FlacDecoder.Decode(corrupt),label);
        }
        Corrupt(start+6,1,"FLAC header CRC8"); Corrupt(constant.Length-1,1,"FLAC frame CRC16"); Corrupt(26,1,"FLAC MD5");
        Corrupt(4,127,"FLAC forbidden metadata"); Corrupt(start+7,128,"FLAC reserved subframe bit");
        byte[] invalid = (byte[])constant.Clone(); invalid[21] |= 15; Array.Fill(invalid,(byte)255,22,4);
        Reject(()=>FlacDecoder.Decode(invalid),"FLAC declared output resource bound");
        Reject(()=>FlacDecoder.Decode(constant.Concat(new byte[]{0}).ToArray()),"FLAC trailing garbage");
        byte[] padded = new byte[constant.Length+3]; constant.CopyTo(padded,3);
        Check(FlacDecoder.Decode(padded.AsSpan(3)).Samples.SequenceEqual(FlacDecoder.Decode(constant).Samples),"FLAC nonzero-offset span");
        foreach ((string file,int byteOffset,byte mask,string label) in new[] {
            ("fixed-order-1.flac",10,(byte)0xc0,"reserved Rice residual coding"),
            ("lpc-order2.flac",12,(byte)8,"negative LPC shift") })
        {
            byte[] corrupt = File.ReadAllBytes(Path.Combine(root,file)); corrupt[FlacAudioStart(corrupt)+byteOffset] |= mask;
            Reject(()=>FlacDecoder.Decode(corrupt),"FLAC " + label);
        }
        Console.WriteLine($"PASS: {count} exact original FLAC streams, depths4–32, fixed/LPC/Rice, stereo decorrelation, CRC/MD5, all {constant.Length} truncations");
        return count;
    }

    private static void WaveMalformedTests(byte[] ima)
    {
        Reject(()=>WaveDecoder.Decode(ima[..20]),"Truncated WAVE");
        byte[] bad = (byte[])ima.Clone(); int audio = FindChunk(bad,"data"); bad[audio+2] = 89;
        Reject(()=>WaveDecoder.Decode(bad),"IMA invalid step index");
        bad = (byte[])ima.Clone(); audio = FindChunk(bad,"data"); bad[audio+3] = 1;
        Reject(()=>WaveDecoder.Decode(bad),"IMA reserved header byte");
        byte[] wrongFact = Wave(Format(0x11,1,8000,8,4,new byte[]{9,0}),new byte[]{0,0,0,0,0x11,0x11,0x11,0x11},new byte[]{10,0,0,0});
        Reject(()=>WaveDecoder.Decode(wrongFact),"IMA fact beyond available");
        byte[] trimmed = Wave(Format(0x11,1,8000,8,4,new byte[]{9,0}),new byte[]{0,0,0,0,0x11,0x11,0x11,0x11},new byte[]{4,0,0,0});
        Check(WaveDecoder.Decode(trimmed).Samples.SequenceEqual(new[]{0,1,2,3}),"IMA fact trims frames");
        Reject(()=>WaveDecoder.Decode(Wave(Format(0x11,1,8000,8,4,new byte[]{9,0}),new byte[]{0,0,0,0,0x11,0x11,0x11,0x11},[])),"Empty fact chunk");
        byte[] hugeChunk = (byte[])ima.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(hugeChunk.AsSpan(16),uint.MaxValue);
        Reject(()=>WaveDecoder.Decode(hugeChunk),"RIFF child overflow");
        byte[] pcm = new AudioBuffer(44100,1,12,[1,2,3],"validbits").ToWave(); pcm[FindChunk(pcm,"data")] |= 1;
        Reject(()=>WaveDecoder.Decode(pcm),"Extensible unused bits must be zero");
    }

    private static void RoundTrip(AudioBuffer original)
    {
        byte[] wave = original.ToWave();
        Check(BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(4)) == wave.Length-8,"RIFF size/padding");
        AudioBuffer decoded = WaveDecoder.Decode(wave);
        Check(original.SampleRate == decoded.SampleRate && original.Channels == decoded.Channels && original.BitsPerSample == decoded.BitsPerSample &&
            original.Samples.SequenceEqual(decoded.Samples),"PCM WAVE lossless source-depth roundtrip: " + original.CodecName);
        Check(Math.Abs(original.Duration - decoded.Duration) < 1e-12,"Audio duration roundtrip");
    }
    private static int[] IntArray(JsonElement array) => array.EnumerateArray().Select(s=>s.GetInt32()).ToArray();
    private static void Check(bool condition,string label) { if(!condition) throw new Exception("Audio test failed: " + label); }
    private static void Reject(Action action,string label)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (NotSupportedException) { return; }
        throw new Exception("Accepted malformed audio: " + label);
    }
    private static void RejectCancellation(Action action)
    {
        try { action(); } catch (OperationCanceledException) { return; }
        throw new Exception("Audio cancellation was not propagated");
    }
    private static int FlacAudioStart(byte[] data)
    {
        int offset=4;
        while(true) { bool last=(data[offset]&128)!=0; int size=data[offset+1]<<16|data[offset+2]<<8|data[offset+3]; offset+=4+size; if(last)return offset; }
    }
    private static int FindChunk(byte[] wave,string name)
    {
        int offset=12;
        while(offset<wave.Length) { int size=(int)BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(offset+4)); if(Encoding.ASCII.GetString(wave,offset,4)==name)return offset+8; offset+=8+size+(size&1); }
        throw new Exception("Test fixture chunk missing: " + name);
    }
    private static byte[] Format(int tag,int channels,int rate,int alignment,int bits,byte[]? extra=null)
    {
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
        writer.Write((ushort)tag);writer.Write((ushort)channels);writer.Write(rate);writer.Write(rate*alignment);writer.Write((ushort)alignment);writer.Write((ushort)bits);
        if(extra is not null) { writer.Write((ushort)extra.Length);writer.Write(extra); }
        return stream.ToArray();
    }
    private static byte[] Wave(byte[] format,byte[] audio,byte[]? fact=null)
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write("RIFF"u8);writer.Write(0);writer.Write("WAVE"u8);
        void Chunk(string tag,byte[] data) { writer.Write(Encoding.ASCII.GetBytes(tag));writer.Write(data.Length);writer.Write(data);if((data.Length&1)!=0)writer.Write((byte)0); }
        Chunk("fmt ",format);if(fact is not null)Chunk("fact",fact);Chunk("data",audio);
        writer.Flush();byte[] result=stream.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),(uint)(result.Length-8));return result;
    }
}
