using Luma.Core.Containers;

namespace Luma.Core.Tests;

internal static class HevcIssueFixtures
{
    public static void Run(string fixtures)
    {
        // Original minimal AAC-LC raw_data_block: zero spectral bands in one
        // center SCE, two CPEs and one LFE, then ID_END. No copied codec payload.
        string Ics() => Bits(210,8) + new string('0',14);
        string bits = Bits(0,3)+Bits(0,4)+Ics()
            + Bits(1,3)+Bits(0,4)+"0"+Ics()+Ics()
            + Bits(1,3)+Bits(1,4)+"0"+Ics()+Ics()
            + Bits(3,3)+Bits(0,4)+Ics()+Bits(7,3);
        bits=bits.PadRight((bits.Length+7)/8*8,'0');
        var packet=Enumerable.Range(0,bits.Length/8).Select(i=>Convert.ToByte(bits.Substring(i*8,8),2)).ToArray();
        var samples=Enumerable.Range(0,96).Select(i=>new Mp4Sample(packet,Time(i),Time(i),Time(i+1)-Time(i),true)).ToArray();
        var track=new Mp4Track(1,"A_AAC",[0x11,0xb0],0,0,48000,6,samples);
        using var output=new MemoryStream();IsoMediaMuxer.Write(output,[track]);output.Position=0;
        var info=Mp4Inspector.Inspect(output);
        if(info.AudioTracks.Single().Channels!=6||info.AudioTracks.Single().SampleRate!=48000||info.AacChannels!=6)throw new Exception("AAC5.1 metadata was not retained");
        try { using var invalid=new MemoryStream();IsoMediaMuxer.Write(invalid,[track with { CodecPrivate=[0x11,0xb4] }]);throw new Exception("960-sample AAC accepted"); } catch(NotSupportedException) { }
        var export=Environment.GetEnvironmentVariable("LUMA_WINDOWS_MEDIA_EXPORT");
        if(!string.IsNullOrWhiteSpace(export)) { Directory.CreateDirectory(export);File.WriteAllBytes(Path.Combine(export,"Luma-AAC51-Silence.m4a"),output.ToArray()); }
        string matching=Path.Combine(fixtures,"Luma-HEVC10-AAC51.mp4");
        using(var input=File.OpenRead(matching))
        {
            var actual=Mp4Inspector.Inspect(input);
            if(actual.VideoTracks.Single() is not {SampleEntry:"hev1",Codec:"HEVC",Width:1920,Height:804,BitDepth:10,Profile:2}
                ||actual.AudioTracks.Single() is not {Channels:6,SampleRate:48000,AacObjectType:2})throw new Exception("Reported HEVC/AAC5.1 feature fixture metadata mismatch");
        }
        Console.WriteLine("PASS: original AAC-LC5.1 silence bitstream fixture, six-channel ASC/sample-entry retention and960-sample configuration rejection; native decoding validated separately");
    }
    static string Bits(int value,int count)=>Convert.ToString(value,2).PadLeft(count,'0');
    static long Time(int index)=>(long)((Int128)index*1024*1_000_000_000/48000);
}
