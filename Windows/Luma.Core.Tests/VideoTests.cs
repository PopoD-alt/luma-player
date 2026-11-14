using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Luma.Core.Video;

namespace Luma.Core.Tests;

public static class VideoTests
{
    public static void Run(string fixturesRoot)
    {
        byte[][] colors = [[255,0,0,255],[0,255,0,255],[0,0,255,255]];
        foreach (string file in new[] { "Luma-RGB.avi", "Luma-RLE8.avi" })
        {
            var video = AviVideo.Open(File.ReadAllBytes(Path.Combine(fixturesRoot,file)));
            Check(video.Width == 64 && video.Height == 48 && video.FrameCount == 12 && video.FramesPerSecond == 2 && video.Duration == 6, "RGB/RLE fixture metadata");
            Check(video.CodecName.Contains("video only"), "Video-only decoder label");
            for (int index = 0; index < video.FrameCount; index++) AssertSolid(video.DecodeFrame(index),colors[index % 3]);
            RejectRange(() => video.DecodeFrame(-1)); RejectRange(() => video.DecodeFrame(video.FrameCount));
            Cancel(() => video.DecodeFrame(0,new CancellationToken(true)));
        }
        // Hand-authored DIB pixel vectors check top-down/bottom-up, BGR ordering,
        // scanline padding and RGB32's unused alpha byte independently of fixtures.
        var rgb24 = AviVideo.Open(Avi(1,2,24,"DIB ",[[255,0,0,99, 0,0,255,88]],compression:0));
        Equal(rgb24.DecodeFrame(0),[255,0,0,255, 0,0,255,255],"RGB24 padded bottom-up scanlines");
        var rgb32 = AviVideo.Open(Avi(1,-2,32,"DIB ",[[0,0,255,0, 255,0,0,17]],compression:0));
        Equal(rgb32.DecodeFrame(0),[255,0,0,255, 0,0,255,255],"RGB32 top-down/opaque output");
        (byte,byte,byte)[] palette = [(255,0,0),(0,255,0),(0,0,255)];
        var literal = AviVideo.Open(Avi(4,2,8,"MRLE",[[0,4,0,1,2,0,0,0, 4,1,0,0,0,1]],palette,1));
        byte[] literalExpected = Enumerable.Repeat(colors[1],4).SelectMany(c=>c).Concat(new byte[]{255,0,0,255,0,255,0,255,0,0,255,255,255,0,0,255}).ToArray();
        Equal(literal.DecodeFrame(0),literalExpected,"RLE8 absolute and encoded modes");
        Reject(() => AviVideo.Open(Avi(4,4,8,"MRLE",[[0,2,1,1,0,1]],palette,1)).DecodeFrame(0));
        Reject(() => AviVideo.Open(Avi(4,4,8,"MRLE",[[4,7,0,0,0,1]],palette,1)).DecodeFrame(0));
        Reject(() => AviVideo.Open(Avi(4,4,8,"MRLE",[[4,0,0,0,0,1]],palette,1)).DecodeFrame(0));
        Reject(() => AviVideo.Open(Avi(4,4,8,"MRLE",[[5,0,0,1]],palette,1)).DecodeFrame(0));
        Console.WriteLine("PASS: portable original RGB24/RGB32/RLE8 AVI, exact frames, scanline padding/orientation, absolute/runs, video-only metadata and explicit partial/delta rejection");

        string vectors = Path.Combine(fixturesRoot,"msvideo1");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(vectors,"manifest.json")));
        int width = manifest.RootElement.GetProperty("width").GetInt32(), height = manifest.RootElement.GetProperty("height").GetInt32();
        var vectorPalette = manifest.RootElement.GetProperty("palette").EnumerateArray().Select(p => ((byte)p[0].GetInt32(),(byte)p[1].GetInt32(),(byte)p[2].GetInt32())).ToArray();
        int exact = 0;
        foreach (var stream in manifest.RootElement.GetProperty("streams").EnumerateArray())
        {
            int depth = stream.GetProperty("bitDepth").GetInt32(); var entries = stream.GetProperty("frames").EnumerateArray().ToArray();
            byte[][] packets = entries.Select(f=>File.ReadAllBytes(Path.Combine(vectors,f.GetProperty("packet").GetString()!))).ToArray();
            byte[][] goldens = entries.Select(f=>File.ReadAllBytes(Path.Combine(vectors,f.GetProperty("rgba").GetString()!))).ToArray();
            var source = File.ReadAllBytes(Path.Combine(fixturesRoot,$"Luma-MSVideo1-{depth}.avi"));
            var video = AviVideo.Open(source); source[0] = 0; // Open must retain an immutable snapshot.
            Check(video.Width == width && video.Height == height && video.FrameCount == 12 && video.CodecName.Contains("video only"),"Video 1 AVI metadata");
            for (int index = 0; index < packets.Length; index++) { Equal(video.DecodeFrame(index),goldens[index],$"Video 1 {depth}-bit exact golden {index}"); exact++; }
            int[] order = [11,0,8,2,10,1,7,3,6,5,4,9];
            foreach (int index in order) Equal(video.DecodeFrame(index),goldens[index],"Video 1 deterministic backward/random seek");
            Parallel.ForEach(order.Concat(order),index=>Equal(video.DecodeFrame(index),goldens[index],"Video 1 concurrent seek"));
            byte[] mutable = video.DecodeFrame(0); mutable[0] ^= 255;
            Equal(video.DecodeFrame(0),goldens[0],"Caller output mutation corrupted a checkpoint");
            for (int index = 0; index < packets.Length; index++)
            {
                var alone = AviVideo.Open(Avi(width,height,depth,"MSVC",[packets[index]],vectorPalette));
                if (entries[index].GetProperty("keyframe").GetBoolean()) Equal(alone.DecodeFrame(0),goldens[index],"Independent keyframe");
                else Reject(()=>alone.DecodeFrame(0));
            }
        }
        foreach (string alias in new[]{"CRAM","cram","MSVC","msvc","WHAM","wham"})
            AssertSolid(AviVideo.Open(Avi(4,4,16,alias,[[0,0xfc,0,0]])).DecodeFrame(0),colors[0]);
        byte[] two = AviVideo.Open(Avi(4,4,16,"MSVC",[[1,0,0,0x7c,0x1f,0]])).DecodeFrame(0);
        byte[] expectedTwo = Enumerable.Repeat(colors[2],12).SelectMany(c=>c).Concat(colors[0]).Concat(Enumerable.Repeat(colors[2],3).SelectMany(c=>c)).ToArray();
        Equal(two,expectedTwo,"Hand-written Video 1 flag-bit/RGB555 orientation oracle");
        AssertSolid(AviVideo.Open(Avi(4,4,16,"MSVC",[[0,0x84,0,0xfc]])).DecodeFrame(0),colors[0]);
        var skipped = AviVideo.Open(Avi(4,4,16,"MSVC",[[1,0,0,0x7c,0x1f,0],[1,0x84]]));
        Equal(skipped.DecodeFrame(1),two,"Video 1 repeated delta");
        byte[] many = Enumerable.Repeat(new byte[]{0,0xfc},1024).SelectMany(p=>p).ToArray();
        var largeSkip = AviVideo.Open(Avi(128,128,16,"MSVC",[many,[255,0x87,0x1f,0x80]]));
        byte[] changed = largeSkip.DecodeFrame(1);
        Equal(changed.AsSpan(124*4,4).ToArray(),colors[2],"Video 1 multi-byte skip crossing block rows");
        foreach (byte[] packet in new byte[][]{[],[0],[1,0],[1,0,0,0],[0,0xfc,1],[0,0xfc,0,1],[0,0xfc,0,0,0,0],[2,0x84]})
            Reject(()=>AviVideo.Open(Avi(4,4,16,"MSVC",[packet])).DecodeFrame(0));
        Reject(()=>AviVideo.Open(Avi(5,4,16,"MSVC",[[0,0xfc]])));
        Reject(()=>AviVideo.Open(Avi(4,-4,16,"MSVC",[[0,0xfc]])));
        Reject(()=>AviVideo.Open(Avi(4,4,8,"MSVC",[[9,0x80]],[(0,0,0)])).DecodeFrame(0));
        Reject(()=>AviVideo.Open(Avi(4,4,16,"MSVC",[[0,0xfc]],paletteChange:true)));
        Console.WriteLine($"PASS: {exact} exact portable Microsoft Video 1 8/16-bit goldens, all aliases, key/delta/solid/two/eight-color blocks, immutable checkpoint, random/concurrent seeking and malformed bounds");

        byte[] gray = GrayJpeg(8,false);
        AssertSolid(AviVideo.Open(Avi(8,8,24,"MJPG",[gray])).DecodeFrame(0),[128,128,128,255]);
        AssertSolid(AviVideo.Open(Avi(16,8,24,"MJPG",[GrayJpeg(16,true)])).DecodeFrame(0),[128,128,128,255]);
        byte[] jpeg = File.ReadAllBytes(Path.Combine(fixturesRoot,"Luma-Baseline.jpg"));
        var mjpeg = AviVideo.Open(File.ReadAllBytes(Path.Combine(fixturesRoot,"Luma-MJPEG.avi")));
        Check(mjpeg.Width==127 && mjpeg.Height==95 && mjpeg.FrameCount==12,"MJPEG metadata");
        byte[] gradient = mjpeg.DecodeFrame(0); Equal(mjpeg.DecodeFrame(11),gradient,"MJPEG seek consistency");
        long error = 0; int maximum = 0;
        for(int y=0;y<95;y++) for(int x=0;x<127;x++)
        {
            int[] source = [x*255/126,y*255/94,(x+y)*255/220]; int offset=(y*127+x)*4;
            for(int channel=0;channel<3;channel++) { int delta=Math.Abs(gradient[offset+channel]-source[channel]); error+=delta; maximum=Math.Max(maximum,delta); }
            Check(gradient[offset+3]==255,"MJPEG output alpha");
        }
        double mean=(double)error/(127*95*3); Check(mean<5 && maximum<25,$"MJPEG original generated gradient differs: mean {mean}, max {maximum}");
        byte[] badPadding=(byte[])gray.Clone(); badPadding[^3]=0;
        Reject(()=>AviVideo.Open(Avi(8,8,24,"MJPG",[badPadding])).DecodeFrame(0));
        Reject(()=>AviVideo.Open(Avi(8,8,24,"MJPG",[GrayJpeg(8,false,false)])).DecodeFrame(0));
        byte[] progressive=(byte[])jpeg.Clone(); progressive[Marker(progressive,0xc0)+1]=0xc2;
        Reject(()=>AviVideo.Open(Avi(127,95,24,"MJPG",[progressive])).DecodeFrame(0));
        byte[] zeroQuantizer=(byte[])jpeg.Clone(); zeroQuantizer[Marker(zeroQuantizer,0xdb)+5]=0;
        Reject(()=>AviVideo.Open(Avi(127,95,24,"MJPG",[zeroQuantizer])).DecodeFrame(0));
        Reject(()=>AviVideo.Open(Avi(127,95,24,"MJPG",[jpeg[..^10]])).DecodeFrame(0));
        Reject(()=>AviVideo.Open(Avi(127,95,24,"MJPG",[jpeg.Concat(jpeg).ToArray()])).DecodeFrame(0));
        Console.WriteLine($"PASS: portable original baseline MJPEG, exact gray/restart bitstreams, generated gradient mean error {mean:F3}/max {maximum}, seek and explicit progressive/missing-table/padding/field rejection");

        Reject(()=>AviVideo.Open([0,1,2]));
        byte[] truncated=File.ReadAllBytes(Path.Combine(fixturesRoot,"Luma-RGB.avi"))[..^1]; Reject(()=>AviVideo.Open(truncated));
        Reject(()=>AviVideo.Open(Avi(8193,4,16,"MSVC",[[0,0xfc]])));
        Reject(()=>AviVideo.Open(Avi(8192,8192,16,"MSVC",[[0,0xfc]])));
        Reject(()=>AviVideo.Open(Avi(4,4,24,"XVID",[[0,0]],compression:0x44495658)));
        Cancel(()=>AviVideo.Open(File.ReadAllBytes(Path.Combine(fixturesRoot,"Luma-RGB.avi")),new CancellationToken(true)));
        Console.WriteLine("PASS: portable AVI malformed RIFF, dimension/pixel/codec bounds, cancellation and frame-index rejection");
    }

    private static void Check(bool value,string label) { if(!value) throw new InvalidOperationException(label); }
    private static void Equal(byte[] actual,byte[] expected,string label) => Check(actual.AsSpan().SequenceEqual(expected),label);
    private static void AssertSolid(byte[] rgba,byte[] expected)
    {
        Check(rgba.Length>0 && rgba.Length%4==0,"RGBA extent");
        for(int offset=0;offset<rgba.Length;offset+=4) Check(rgba.AsSpan(offset,4).SequenceEqual(expected),"Exact solid-frame RGBA");
    }
    private static void Reject(Action action)
    {
        try { action(); } catch(InvalidDataException) { return; } catch(NotSupportedException) { return; }
        throw new InvalidOperationException("Accepted malformed/unsupported AVI data");
    }
    private static void RejectRange(Action action) { try { action(); } catch(ArgumentOutOfRangeException) { return; } throw new InvalidOperationException("Accepted invalid frame index"); }
    private static void Cancel(Action action) { try { action(); } catch(OperationCanceledException) { return; } throw new InvalidOperationException("Ignored cancellation"); }
    private static int Marker(byte[] bytes,byte marker) { for(int i=0;i<bytes.Length-1;i++) if(bytes[i]==255 && bytes[i+1]==marker) return i; throw new InvalidOperationException("Fixture marker missing"); }
    private static byte[] Chunk(string id,byte[] body)
    {
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes(id)); writer.Write(body.Length); writer.Write(body); if((body.Length&1)!=0) writer.Write((byte)0); return stream.ToArray();
    }
    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(a=>a).ToArray();
    private static byte[] Avi(int width,int height,int depth,string codec,byte[][] packets,(byte R,byte G,byte B)[]? palette=null,uint? compression=null,bool paletteChange=false)
    {
        using var sh=new MemoryStream(); using(var w=new BinaryWriter(sh,Encoding.ASCII,true))
        {
            w.Write(Encoding.ASCII.GetBytes("vids"+codec)); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u); w.Write(1u); w.Write(2u); w.Write(0u); w.Write(packets.Length); w.Write(0u); w.Write(uint.MaxValue); w.Write(0u); w.Write(new byte[8]);
        }
        using var sf=new MemoryStream(); using(var w=new BinaryWriter(sf,Encoding.ASCII,true))
        {
            w.Write(40u); w.Write(width); w.Write(height); w.Write((ushort)1); w.Write((ushort)depth);
            w.Write(compression ?? BitConverter.ToUInt32(Encoding.ASCII.GetBytes(codec),0)); w.Write(0u); w.Write(0u); w.Write(0u); w.Write((uint)(palette?.Length??0)); w.Write(0u);
            if(palette is not null) foreach(var color in palette) { w.Write(color.B); w.Write(color.G); w.Write(color.R); w.Write((byte)0); }
        }
        byte[] headers=Chunk("LIST",Join(Encoding.ASCII.GetBytes("hdrl"),Chunk("LIST",Join(Encoding.ASCII.GetBytes("strl"),Chunk("strh",sh.ToArray()),Chunk("strf",sf.ToArray())))));
        byte[] movie=Chunk("LIST",Join(Encoding.ASCII.GetBytes("movi"),packets.SelectMany(p=>Chunk("00dc",p)).ToArray(),paletteChange?Chunk("00pc",new byte[4]):[]));
        byte[] body=Join(Encoding.ASCII.GetBytes("AVI "),headers,movie);
        using var output=new MemoryStream(); using var writer=new BinaryWriter(output); writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(body.Length); writer.Write(body); return output.ToArray();
    }
    private static byte[] GrayJpeg(int width,bool restart,bool includeTables=true)
    {
        static byte[] Segment(byte marker,byte[] body) => Join([255,marker,(byte)((body.Length+2)>>8),(byte)(body.Length+2)],body);
        byte[] quantizer=Segment(0xdb,Join([0],Enumerable.Repeat((byte)1,64).ToArray()));
        byte[] counts=Join([1],new byte[15]); byte[] huffman=Segment(0xc4,Join([0],counts,[0,16],counts,[0]));
        return Join([255,216],quantizer,Segment(0xc0,[8,0,8,0,(byte)width,1,1,0x11,0]),includeTables?huffman:[],
            restart?Segment(0xdd,[0,1]):[],Segment(0xda,[1,1,0,0,63,0]),restart?[0x3f,255,0xd0,0x3f]:[0x3f],[255,217]);
    }
}
