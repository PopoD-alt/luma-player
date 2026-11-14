using System.Buffers.Binary;
using System.Text;
using Luma.Core.Containers;

namespace Luma.Core.Tests;

public static class Mp4InspectionTests
{
    public static void Run(string fixturesRoot)
    {
        foreach (string entry in new[] { "hev1", "hvc1" })
        foreach (bool wide in new[] { false, true })
        foreach (string textEntry in new[] { "tx3g", "text" })
        {
            byte[] bytes = Fixture(entry, textEntry, wide, true, false).Bytes;
            using var stream = new MemoryStream(bytes); stream.Position = 3;
            var info = Mp4Inspector.Inspect(stream);
            Check(stream.Position == 3 && stream.CanRead, "Inspector changed position or closed caller stream");
            Check(info.SubtitleWarnings.Count == 0, "Valid caption data unexpectedly produced warnings");
            Check(info.VideoCodec == "HEVC" && info.BitDepth == 10 && info.VideoTracks.Single().Profile == 2 && info.VideoTracks[0].SampleEntry == entry, "HEVC Main10/hvcC metadata");
            Check(info.VideoTracks[0].Width == 1920 && info.VideoTracks[0].Height == 804, "Visible sample dimensions were rounded to coded block geometry");
            Check(info.AacChannels == 6 && info.AudioTracks[0].Channels == 6 && info.AudioTracks[0].SampleEntryChannels == 2 && info.AudioTracks[0].SampleRate == 48000 && info.AudioTracks[0].AacObjectType == 2, "AAC5.1 ASC must override legacy stereo sample-entry field");
            var text = info.SubtitleTracks.Single(); Check(text.SampleEntry == textEntry && text.Language == "eng", "MP4 text sample-entry/handler/language");
            Check(text.Cues.Count == 2 && text.Cues[0].Text == "Hello <Naruto>" && text.Cues[1].Text == "2 < 3 and 4 > 1", "MP4 plain text/recognized markup/literal comparisons");
            Near(text.Cues[0].Start, .5); Near(text.Cues[0].End, 1.5); Near(text.Cues[1].Start, 1.25); Near(text.Cues[1].End, 2);
            var metadataOnly = Mp4Inspector.Inspect(stream, false); Check(metadataOnly.AacChannels == 6 && metadataOnly.SubtitleTracks[0].Cues.Count == 0, "Metadata-only inspection read/extracted subtitle data");
        }
        using (var stream = new MemoryStream(Fixture("hev1", "tx3g", true, false, false).Bytes))
        {
            var cues = Mp4Inspector.Inspect(stream).SubtitleTracks.Single().Cues;
            Near(cues[0].Start, .25); Near(cues[0].End, 1.25); Near(cues[1].Start, 1); Near(cues[1].End, 2);
        }
        using (var stream = new MemoryStream(Fixture("hev1", "tx3g", true, true, true).Bytes))
        {
            var cues = Mp4Inspector.Inspect(stream).SubtitleTracks.Single().Cues;
            Near(cues[0].Start, .5); Near(cues[1].Start, 1.25); // Version1 elst has64-bit duration/media-time.
        }
        byte[] negativeCts = Fixture("hev1", "tx3g", true, false, false).Bytes;
        Put32(negativeCts, Find(negativeCts, "ctts") + 16, uint.MaxValue - 249); // Signed -250ms.
        using (var stream = new MemoryStream(negativeCts)) { var cue = Mp4Inspector.Inspect(stream).SubtitleTracks[0].Cues[0]; Near(cue.Start, 0); Near(cue.End, .75); }
        using (var sparse = SparseFixture())
        {
            sparse.Position = 7; var info = Mp4Inspector.Inspect(sparse);
            Check(sparse.Length > uint.MaxValue && sparse.Position == 7 && sparse.TotalRead < 32_000 && info.SubtitleTracks[0].Cues.Count == 2, "Large movie/co64 inspection read full media or lost seekability");
        }
        foreach ((string name, string codec, int depth) in new[] { ("Luma-Demo.mp4", "AVC", 8), ("Luma-HEVC8.mov", "HEVC", 8), ("Luma-HEVC10.mov", "HEVC", 10) })
        {
            string path = Path.Combine(fixturesRoot, name); if (!File.Exists(path)) throw new FileNotFoundException("Missing MP4 inspection native fixture", path);
            using var input = File.OpenRead(path); var info = Mp4Inspector.Inspect(input, false);
            Check(info.VideoCodec == codec && info.BitDepth == depth, "Existing actual compressed fixture metadata: " + name);
        }
        Malformed();
        Console.WriteLine("PASS: seek-based MP4 AVC/HEVC Main/Main10 hev1/hvc1 metadata, effective AAC48k5.1 ASC over stereo entry, actual native fixtures, caller stream preservation and >4GiB sparse/co64 reads");
        Console.WriteLine("PASS: original tx3g/QuickTime text UTF8, stts/ctts/stsc/stsz/stco/co64, common movie/media edits and clipping, markup/literal text, bounded caption warnings preserving A/V, strict external-reference/structure/IO/cancellation rejection");
    }

    static void Malformed()
    {
        byte[] valid = Fixture("hev1", "tx3g", true, true, false).Bytes;
        void BadCaption(string tag, int relative, uint value)
        {
            byte[] data = (byte[])valid.Clone(); Put32(data, Find(data, tag) + relative, value); CaptionWarning(data);
        }
        BadCaption("stsz", 12, 100001); BadCaption("stsz", 16, 1_048_577); BadCaption("stts", 12, 3); BadCaption("stsc", 12, 0); BadCaption("stsc", 20, 2);
        BadCaption("co64", 8, 3); BadCaption("elst", 8, 129);
        byte[] badMetadata = (byte[])valid.Clone(); Put32(badMetadata, Find(badMetadata, "mdhd") + 16, 0); Reject(badMetadata);
        byte[] badOffset = (byte[])valid.Clone(); Put64(badOffset, Find(badOffset, "co64") + 12, 0); CaptionWarning(badOffset);
        badOffset = (byte[])valid.Clone(); Put64(badOffset, Find(badOffset, "co64") + 12, ulong.MaxValue); CaptionWarning(badOffset);
        badOffset = (byte[])valid.Clone(); Put64(badOffset, Find(badOffset, "co64") + 12, long.MaxValue - 1); CaptionWarning(badOffset);
        byte[] wrongVersion = (byte[])valid.Clone(); wrongVersion[Find(wrongVersion, "ctts") + 4] = 2; CaptionWarning(wrongVersion);
        byte[] wrongDescriptor = (byte[])valid.Clone(); wrongDescriptor[Find(wrongDescriptor, "esds") + 9] = 255; Reject(wrongDescriptor);
        byte[] wrongText = (byte[])valid.Clone(); int textStart = Find(wrongText, "mdat") + 4; wrongText[textStart] = 255; wrongText[textStart + 1] = 255; CaptionWarning(wrongText);
        wrongText = (byte[])valid.Clone(); wrongText[textStart + 2] = 255; CaptionWarning(wrongText);
        CaptionWarning(CaptionFirst(wrongText));
        byte[] malformedTiming = (byte[])valid.Clone(); Put32(malformedTiming, Find(malformedTiming, "stts") + 12, 3);
        byte[] extraTrack = MovieChildren(valid).Last(b => b.AsSpan(4,4).SequenceEqual("trak"u8)); Put32(extraTrack, Find(extraTrack,"tkhd")+16,4);
        using (var mixed = new MemoryStream(CaptionFirst(malformedTiming, extraTrack)))
        {
            var info = Mp4Inspector.Inspect(mixed);
            Check(info.VideoCodec=="HEVC"&&info.AacChannels==6&&info.SubtitleWarnings.Count==1&&info.SubtitleTracks.Count==1&&info.SubtitleTracks[0].Id==4&&info.SubtitleTracks[0].Cues.Count==2,"A malformed earlier caption track poisoned valid A/V/later captions");
        }
        byte[] unsupported = (byte[])valid.Clone(); Encoding.ASCII.GetBytes("wvtt").CopyTo(unsupported, Find(unsupported, "tx3g")); CaptionWarning(unsupported);
        CaptionWarning(valid.Concat(Box("moof", [])).ToArray());
        using (var fragmented = new MemoryStream(valid.Concat(Box("moof", [])).ToArray())) Check(Mp4Inspector.Inspect(fragmented, false).AacChannels == 6, "Metadata-only fragmented inspection was blocked by optional captions");
        byte[] external = Fixture("hev1", "tx3g", true, true, false, external: true).Bytes;
        foreach (bool extract in new[] { false, true })
        foreach (bool fragmented in new[] { false, true })
        foreach (bool unsupportedEntry in new[] { false, true })
        {
            byte[] source = (byte[])external.Clone(); if(unsupportedEntry) Encoding.ASCII.GetBytes("wvtt").CopyTo(source,Find(source,"tx3g"));
            using var input = new MemoryStream(fragmented ? source.Concat(Box("moof", [])).ToArray() : source);
            try { Mp4Inspector.Inspect(input, extract); throw new Exception("External data reference bypassed security rejection"); }
            catch (Mp4ExternalDataReferenceException) { }
        }
        byte[] externalAlias=(byte[])external.Clone();Encoding.ASCII.GetBytes("alis").CopyTo(externalAlias,Find(externalAlias,"url "));
        using(var input=new MemoryStream(externalAlias))try{Mp4Inspector.Inspect(input);throw new Exception("External QuickTime alias accepted");}catch(Mp4ExternalDataReferenceException){}
        byte[] rateEdit = (byte[])valid.Clone(); Put32(rateEdit, Find(rateEdit, "elst") + 20, 0); CaptionWarning(rateEdit);
        byte[] badStructure = (byte[])valid.Clone(); Put32(badStructure, Find(badStructure, "co64") - 4, uint.MaxValue); Reject(badStructure);
        badStructure = (byte[])valid.Clone(); Put32(badStructure, Find(badStructure, "url ") - 4, uint.MaxValue); Reject(badStructure);
        Reject(valid.Concat(Box("moov", [])).ToArray());
        foreach (int cut in new[] { 0, 1, 7, 15, valid.Length - 1 }) Reject(valid[..cut]);
        byte[] hugeBox = new byte[16]; Put32(hugeBox, 0, 1); "moov"u8.CopyTo(hugeBox.AsSpan(4)); Put64(hugeBox, 8, ulong.MaxValue); Reject(hugeBox);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using (var input = new MemoryStream(valid))
        {
            try { Mp4Inspector.Inspect(input, true, cancelled.Token); throw new Exception("MP4 inspection cancellation ignored"); } catch (OperationCanceledException) { }
        }
        using var nonSeek = new NonSeeking(valid); try { Mp4Inspector.Inspect(nonSeek); throw new Exception("Nonseekable inspection accepted"); } catch (NotSupportedException) { }
        using var position = new MemoryStream(badStructure); position.Position = 5;
        try { Mp4Inspector.Inspect(position); throw new Exception("Bad structure accepted"); } catch (InvalidDataException) { Check(position.Position == 5, "Failure did not restore caller position"); }
        using var captionIo = new CaptionIoFailure(valid, textStart);
        try { Mp4Inspector.Inspect(captionIo); throw new Exception("Caption IO failure was swallowed"); } catch (IOException error) when (error.Message == "Synthetic caption IO failure") { }
    }

    // Metadata-only fixture: configuration facts are real; dummy AVC/AAC media is never decoded.
    static (byte[] Bytes, byte[][] Samples) Fixture(string videoEntry, string textEntry, bool wide, bool edits, bool version1, bool external = false)
    {
        var samples = new[] { TextSample("<b>Hello</b> <Naruto>"), TextSample("2 < 3 and 4 > 1") };
        byte[] ftyp = Box("ftyp", Join(Encoding.ASCII.GetBytes("isom"), Be32(0), Encoding.ASCII.GetBytes("isom")));
        long[] offsets = [ftyp.Length + 8, ftyp.Length + 8 + samples[0].Length];
        byte[] moov = Movie(videoEntry, textEntry, wide, edits, version1, external, offsets, samples.Select(s => (uint)s.Length).ToArray());
        return (Join(ftyp, Box("mdat", Join(samples)), moov), samples);
    }
    static SparseStream SparseFixture()
    {
        byte[][] samples = [TextSample("<b>Hello</b> <Naruto>"), TextSample("2 < 3 and 4 > 1")];
        byte[] ftyp = Box("ftyp", Join(Encoding.ASCII.GetBytes("isom"), Be32(0), Encoding.ASCII.GetBytes("isom")));
        const long size = 5L * 1024 * 1024 * 1024; long data = (long)uint.MaxValue + 100;
        byte[] mdat = Join(Be32(1), Encoding.ASCII.GetBytes("mdat"), Be64((ulong)size));
        byte[] moov = Movie("hev1", "tx3g", true, true, false, false, [data, data + samples[0].Length], samples.Select(s => (uint)s.Length).ToArray());
        long moovPosition = ftyp.Length + size;
        return new SparseStream(moovPosition + moov.Length, [(0, ftyp), (ftyp.Length, mdat), (data, Join(samples)), (moovPosition, moov)]);
    }
    static byte[] Movie(string videoEntry, string textEntry, bool wide, bool edits, bool version1, bool external, long[] offsets, uint[] sizes)
    {
        byte[] mvhd = new byte[100]; Put32(mvhd, 12, 1000); Put32(mvhd, 16, 2500);
        byte[] visual = new byte[78]; Put16(visual, 6, 1); Put16(visual, 24, 1920); Put16(visual, 26, 804);
        byte[] hvcc = new byte[23]; hvcc[0] = 1; hvcc[1] = 2; hvcc[17] = 2; hvcc[18] = 2;
        byte[] video = Track(1, "vide", Box("stsd", Join(new byte[4], Be32(1), Box(videoEntry, Join(visual, Box("hvcC", hvcc))))), null, null);
        byte[] sound = new byte[28]; Put16(sound, 6, 1); Put16(sound, 16, 2); Put16(sound, 18, 16); Put32(sound, 24, (uint)48000 << 16);
        byte[] config = new byte[13]; config[0] = 0x40; config[1] = 0x15;
        byte[] decoder = Descriptor(4, Join(config, Descriptor(5, [0x11, 0xb0])));
        byte[] esds = Box("esds", Join(new byte[4], Descriptor(3, Join(new byte[] { 0, 2, 0 }, decoder, Descriptor(6, [2])))));
        byte[] audio = Track(2, "soun", Box("stsd", Join(new byte[4], Be32(1), Box("mp4a", Join(sound, esds)))), null, null);
        byte[] textHeader = new byte[textEntry == "tx3g" ? 38 : 52]; Put16(textHeader, 6, 1);
        byte[] stsd = Box("stsd", Join(new byte[4], Be32(1), Box(textEntry, textHeader)));
        byte[] stts = Box("stts", Join(new byte[4], Be32(1), Be32((uint)sizes.Length), Be32(1000)));
        byte[] ctts = Box("ctts", Join(new byte[] { 1, 0, 0, 0 }, Be32(2), Be32(1), Be32(250), Be32(1), Be32(0)));
        byte[] stsz = Box("stsz", Join(new byte[4], Be32(0), Be32((uint)sizes.Length), Join(sizes.Select(Be32).ToArray())));
        byte[] stsc = Box("stsc", Join(new byte[4], Be32(2), Be32(1), Be32(1), Be32(1), Be32(2), Be32(1), Be32(1)));
        byte[] chunks = Box(wide ? "co64" : "stco", Join(new byte[4], Be32((uint)offsets.Length), Join(offsets.Select(o => wide ? Be64((ulong)o) : Be32((uint)o)).ToArray())));
        byte[] dref = Box("dinf", Box("dref", Join(new byte[4], Be32(1), Box("url ", external ? Join(new byte[4], Encoding.ASCII.GetBytes("https://example.invalid/text\0")) : Be32(1)))));
        byte[]? editList = null;
        if (edits)
        {
            byte[] entries = version1 ? Join(Be64(500), Be64(ulong.MaxValue), Be32(65536), Be64(1500), Be64(250), Be32(65536)) : Join(Be32(500), Be32(uint.MaxValue), Be32(65536), Be32(1500), Be32(250), Be32(65536));
            editList = Box("edts", Box("elst", Join(new byte[] { (byte)(version1 ? 1 : 0), 0, 0, 0 }, Be32(2), entries)));
        }
        byte[] text = Track(3, "sbtl", Join(stsd, stts, ctts, stsz, stsc, chunks), dref, editList);
        return Box("moov", Join(Box("mvhd", mvhd), video, audio, text));
    }
    static byte[] Track(uint id, string handler, byte[] table, byte[]? dinf, byte[]? edit)
    {
        byte[] tkhd = new byte[84]; Put32(tkhd, 12, id);
        byte[] mdhd = new byte[24]; Put32(mdhd, 12, 1000); Put32(mdhd, 16, 2000); Put16(mdhd, 20, (5 << 10) | (14 << 5) | 7);
        byte[] hdlr = new byte[24]; Encoding.ASCII.GetBytes(handler).CopyTo(hdlr, 8);
        return Box("trak", Join(Box("tkhd", tkhd), edit ?? [], Box("mdia", Join(Box("mdhd", mdhd), Box("hdlr", hdlr), Box("minf", Join(dinf ?? [], Box("stbl", table)))))));
    }
    static byte[] TextSample(string text)
    {
        byte[] value = Encoding.UTF8.GetBytes(text), length = new byte[2]; Put16(length, 0, value.Length);
        return Join(length, value, Box("styl", [0, 0])); // Styles are present but not advertised/rendered.
    }
    static byte[] Descriptor(byte tag, byte[] data) { if (data.Length >= 128) throw new Exception("Test descriptor too long"); return Join([tag, (byte)data.Length], data); }
    static List<byte[]> MovieChildren(byte[] data)
    {
        int start=Find(data,"moov")-4,end=start+(int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start));var result=new List<byte[]>();
        for(int p=start+8;p<end;){int length=(int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));result.Add(data.AsSpan(p,length).ToArray());p+=length;}return result;
    }
    static byte[] CaptionFirst(byte[] data,byte[]? extraTrack=null)
    {
        int start=Find(data,"moov")-4,length=(int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start));var children=MovieChildren(data);
        var nonTracks=children.Where(b=>!b.AsSpan(4,4).SequenceEqual("trak"u8)).ToArray();var tracks=children.Where(b=>b.AsSpan(4,4).SequenceEqual("trak"u8)).Reverse().ToArray();
        var reordered=nonTracks.Concat(tracks).ToList();if(extraTrack is not null)reordered.Add(extraTrack);
        return Join(data[..start],Box("moov",Join(reordered.ToArray())),data[(start+length)..]);
    }
    static byte[] Box(string type, byte[] data) => Join(Be32((uint)(data.Length + 8)), Encoding.ASCII.GetBytes(type), data);
    static byte[] Join(params byte[][] arrays) { byte[] data = new byte[arrays.Sum(a => a.Length)]; int p = 0; foreach (var a in arrays) { a.CopyTo(data, p); p += a.Length; } return data; }
    static byte[] Be32(uint value) { byte[] data = new byte[4]; Put32(data, 0, value); return data; }
    static byte[] Be64(ulong value) { byte[] data = new byte[8]; Put64(data, 0, value); return data; }
    static void Put16(byte[] data, int p, int value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p), (ushort)value);
    static void Put32(byte[] data, int p, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(p), value);
    static void Put64(byte[] data, int p, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(p), value);
    static int Find(byte[] data, string name) { int index = data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(name)); if (index < 0) throw new Exception("Missing test box " + name); return index; }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Near(double actual, double expected) => Check(Math.Abs(actual - expected) < 1e-8, $"MP4 cue time {actual} != {expected}");
    static void Reject(byte[] data)
    {
        using var input = new MemoryStream(data);
        try { Mp4Inspector.Inspect(input); } catch (Exception e) when (e is InvalidDataException or NotSupportedException) { return; }
        throw new Exception("Malformed MP4 inspection input accepted");
    }
    static void CaptionWarning(byte[] data)
    {
        using var input = new MemoryStream(data); input.Position = 5;
        var info = Mp4Inspector.Inspect(input);
        Check(info.VideoCodec == "HEVC" && info.BitDepth == 10 && info.AacChannels == 6, "Optional caption failure blocked valid A/V metadata");
        Check(info.SubtitleTracks.Count == 0 && info.SubtitleWarnings.Count == 1 && input.Position == 5, "Caption failure was not exposed as a warning with skipped cues");
    }
    sealed class NonSeeking(byte[] data) : MemoryStream(data) { public override bool CanSeek => false; }
    sealed class CaptionIoFailure(byte[] data, long captionOffset) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer) => Position == captionOffset ? throw new IOException("Synthetic caption IO failure") : base.Read(buffer);
    }
    sealed class SparseStream(long length, List<(long Position, byte[] Data)> segments) : Stream
    {
        public long TotalRead { get; private set; }
        public override long Length => length;
        public override long Position { get; set; }
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, Length - Position); if (count < 0) throw new IOException("Bad sparse position");
            buffer[..count].Clear();
            foreach (var segment in segments)
            {
                long a = Math.Max(Position, segment.Position), b = Math.Min(Position + count, segment.Position + segment.Data.Length);
                if (b > a) segment.Data.AsSpan((int)(a - segment.Position), (int)(b - a)).CopyTo(buffer[(int)(a - Position)..]);
            }
            Position += count; TotalRead += count; return count;
        }
        public override long Seek(long offset, SeekOrigin origin) { Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => Length + offset }; return Position; }
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
