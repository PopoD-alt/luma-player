using System.Buffers.Binary;
using System.Text;

namespace Luma.Core.Containers;

public sealed record MediaPacket(byte[] Data, long TimestampNanoseconds, long? DurationNanoseconds, bool Keyframe);
public sealed record DemuxedTrack(ulong Id, string CodecId, byte[] CodecPrivate, int Width, int Height,
    double SampleRate, int Channels, long DefaultDurationNanoseconds, List<MediaPacket> Packets)
{
    public int TrackType { get; init; }
    public string Name { get; init; } = "";
    public string Language { get; init; } = "und";
    public bool IsDefault { get; init; } = true;
    public bool IsForced { get; init; }
    public double? OutputSampleRate { get; init; }
}
public sealed record DemuxedMatroska(List<DemuxedTrack> Tracks, long TimestampScaleNanoseconds);

/// <summary>Original bounded EBML/Matroska packet reader. RFC9559 sections5,10,11
/// and the official Matroska codec mappings. Does not decode compressed media,
/// reconstruct DTS or promise native playback. Packet coding order and PTS remain
/// unchanged. Known-sized Clusters, one Segment, AVC/HEVC/AAC and supported raw
/// text/PGS subtitle packets; other enabled codecs/features fail explicitly.</summary>
public static class MatroskaDemuxer
{
    public const int MaximumFileBytes = 536_870_912;
    public const int MaximumPackets = 500_000;
    public static DemuxedMatroska Demux(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data); cancellationToken.ThrowIfCancellationRequested();
        if (data.Length == 0) throw new InvalidDataException("Empty Matroska file.");
        if (data.Length > MaximumFileBytes) throw new NotSupportedException("Matroska exceeds the 512 MiB input limit.");
        try { return new Document(data,cancellationToken).Parse(); }
        catch (OverflowException error) { throw new InvalidDataException("Matroska integer/timestamp exceeds the supported range.",error); }
    }
    private sealed class Track
    {
        public ulong Id, Type, Width, Height, DisplayWidth, DisplayHeight, DefaultDuration, Channels = 1;
        public string Codec = "", Name = "", Language = "und";
        public string? LanguageIetf;
        public byte[] Private = [];
        public bool Enabled = true, Default = true, Forced, Lacing = true;
        public double Rate = 8000;
        public double? OutputRate;
        public decimal Scale = 1, AacStep;
        public readonly HashSet<string> Unsupported = [];
        public readonly List<MediaPacket> Packets = [];
    }
    private readonly record struct Element(ulong Id,int Start,int End) { public int Length => End - Start; }
    private sealed class Document(byte[] data,CancellationToken token)
    {
        readonly Reader reader = new(data,token);
        readonly Dictionary<ulong,Track> tracks = [];
        readonly List<ulong> order = [];
        long timestampScale = 1_000_000, copiedBytes;
        int packetCount;
        public DemuxedMatroska Parse()
        {
            var header = reader.ElementAt(0,data.Length);
            if (header.Id != 0x1a45dfa3) throw new InvalidDataException("Matroska EBML header missing.");
            string? docType = null;
            foreach(var field in reader.Children(header.Start,header.End))
            {
                if(field.Id==0x4282) docType=reader.Text(field);
                if((field.Id==0x4285 && reader.UInt(field)>4) || (field.Id==0x42f7 && reader.UInt(field)>1)
                    || (field.Id==0x42f2 && reader.UInt(field)>4) || (field.Id==0x42f3 && reader.UInt(field)>8))
                    throw new NotSupportedException("Matroska/EBML reader version or integer widths exceed supported bounds.");
            }
            if(docType is null) throw new InvalidDataException("EBML DocType is missing.");
            if(docType!="matroska") throw new NotSupportedException("Only Matroska DocType is supported by this packet reader.");
            Element? segment=null;
            foreach(var field in reader.Children(header.End,data.Length,true))
            {
                if(field.Id==0x18538067)
                { if(segment is not null) throw new NotSupportedException("Multiple or linked Matroska Segments are not supported."); segment=field; }
                else if(field.Id is not (0xec or 0xbf)) throw new InvalidDataException("Unexpected top-level EBML element.");
            }
            if(segment is not {} body) throw new InvalidDataException("Matroska Segment missing.");
            var clusters=new List<Element>(); bool sawTracks=false, sawInfo=false;
            foreach(var field in reader.Children(body.Start,body.End))
            {
                switch(field.Id)
                {
                    case 0x1549a966:
                        if(sawInfo) throw new InvalidDataException("Duplicate Matroska Info element."); sawInfo=true;
                        foreach(var info in reader.Children(field.Start,field.End))
                        {
                            if(info.Id==0x2ad7b1) { var value=reader.UInt(info); if(value is 0 or >1_000_000_000) throw new NotSupportedException("TimestampScale is outside the supported range."); timestampScale=(long)value; }
                            if(info.Id is 0x3cb923 or 0x3eb923) throw new NotSupportedException("Linked Matroska Segments are not supported.");
                        }
                        break;
                    case 0x1654ae6b:
                        if(sawTracks) throw new InvalidDataException("Duplicate Tracks element."); sawTracks=true;
                        foreach(var track in reader.Children(field.Start,field.End)) if(track.Id==0xae) ParseTrack(track);
                        break;
                    case 0x1f43b675:
                        if(clusters.Count>=100_000) throw new NotSupportedException("Matroska cluster-count limit exceeded."); clusters.Add(field); break;
                    case 0x18538067: throw new NotSupportedException("Nested/multiple Matroska Segments.");
                }
            }
            if(!sawTracks || !tracks.Values.Any(t=>t.Enabled)) throw new InvalidDataException("Matroska has no enabled supported tracks.");
            if(tracks.Values.Count(t=>t.Enabled && t.Type==1)>1 || tracks.Values.Count(t=>t.Enabled && t.Type==2)>64 || tracks.Values.Count(t=>t.Enabled && t.Type==17)>64)
                throw new NotSupportedException("At most one video, 64 audio and 64 subtitle tracks are supported.");
            foreach(var cluster in clusters) ParseCluster(cluster);
            var output=new List<DemuxedTrack>();
            foreach(ulong id in order)
            {
                token.ThrowIfCancellationRequested(); var track=tracks[id]; if(!track.Enabled) continue;
                if(track.Packets.Count==0 && track.Type!=17) throw new InvalidDataException("An enabled media track has no packets.");
                // Infer only unambiguous positive adjacent durations. Reordered PTS
                // is retained; no DTS is invented and packets are never sorted.
                if(track.Type==1 && track.Packets.Zip(track.Packets.Skip(1),(a,b)=>b.TimestampNanoseconds>a.TimestampNanoseconds).All(increasing=>increasing))
                    for(int i=0;i+1<track.Packets.Count;i++)
                        if(track.Packets[i].DurationNanoseconds is null && track.Packets[i+1].TimestampNanoseconds>track.Packets[i].TimestampNanoseconds)
                            track.Packets[i]=track.Packets[i] with {DurationNanoseconds=checked(track.Packets[i+1].TimestampNanoseconds-track.Packets[i].TimestampNanoseconds)};
                output.Add(new(track.Id,track.Codec,track.Private,track.Type==1?(int)track.Width:0,track.Type==1?(int)track.Height:0,track.Type==2?track.Rate:0,track.Type==2?(int)track.Channels:0,(long)track.DefaultDuration,track.Packets)
                {TrackType=(int)track.Type,Name=track.Name,Language=track.LanguageIetf??track.Language,IsDefault=track.Default,IsForced=track.Forced,OutputSampleRate=track.OutputRate});
            }
            if(!output.Any(t=>t.Packets.Count>0)) throw new InvalidDataException("Matroska has no enabled packets.");
            token.ThrowIfCancellationRequested(); return new(output,timestampScale);
        }
        void ParseTrack(Element entry)
        {
            if(tracks.Count>=256) throw new NotSupportedException("Matroska track-count limit exceeded.");
            var track=new Track();
            foreach(var field in reader.Children(entry.Start,entry.End))
            {
                switch(field.Id)
                {
                    case 0xd7: track.Id=reader.UInt(field); break;
                    case 0x83: track.Type=reader.UInt(field); break;
                    case 0x86: track.Codec=reader.Text(field); break;
                    case 0x536e: track.Name=reader.Text(field); break;
                    case 0x22b59c: track.Language=reader.Text(field); break;
                    case 0x22b59d: track.LanguageIetf=reader.Text(field); break;
                    case 0xb9: track.Enabled=Flag(field); break;
                    case 0x88: track.Default=Flag(field); break;
                    case 0x55aa: track.Forced=Flag(field); break;
                    case 0x9c: track.Lacing=Flag(field); break;
                    case 0x63a2:
                        if(field.Length>1_048_576) throw new NotSupportedException("Matroska CodecPrivate exceeds 1 MiB.");
                        track.Private=Copy(field.Start,field.Length); break;
                    case 0x23e383:
                        track.DefaultDuration=reader.UInt(field);
                        if(track.DefaultDuration==0 || track.DefaultDuration>long.MaxValue) throw new InvalidDataException("Invalid DefaultDuration."); break;
                    case 0x23314f:
                        double scale=reader.Float(field);
                        if(scale<0.000001 || scale>1_000_000) throw new NotSupportedException("TrackTimestampScale is outside supported bounds.");
                        track.Scale=(decimal)scale; break;
                    case 0x56aa: if(reader.UInt(field)!=0) track.Unsupported.Add("CodecDelay"); break;
                    case 0x56bb: if(reader.UInt(field)!=0) track.Unsupported.Add("SeekPreRoll"); break;
                    case 0x537f: if(reader.Signed(field)!=0) track.Unsupported.Add("TrackOffset"); break;
                    case 0x6d80: case 0xe2: track.Unsupported.Add("content encoding/encryption or TrackOperation"); break;
                    case 0xe0:
                        foreach(var video in reader.Children(field.Start,field.End))
                        {
                            switch(video.Id)
                            {
                                case 0xb0: track.Width=reader.UInt(video); break;
                                case 0xba: track.Height=reader.UInt(video); break;
                                case 0x54b0: track.DisplayWidth=reader.UInt(video); break;
                                case 0x54ba: track.DisplayHeight=reader.UInt(video); break;
                                case 0x54b2: case 0x54aa: case 0x54bb: case 0x54cc: case 0x54dd: case 0x53b8: case 0x53c0:
                                    if(reader.UInt(video)!=0) track.Unsupported.Add("video display/crop/stereo/alpha metadata"); break;
                                case 0x9a: if(reader.UInt(video)==1) track.Unsupported.Add("interlaced video"); break;
                                case 0x7670: track.Unsupported.Add("video projection metadata"); break;
                                case 0x55b0: track.Unsupported.Add("video colour/HDR metadata preservation"); break;
                            }
                        }
                        break;
                    case 0xe1:
                        foreach(var audio in reader.Children(field.Start,field.End))
                        {
                            if(audio.Id==0xb5) track.Rate=reader.Float(audio);
                            if(audio.Id==0x78b5) track.OutputRate=reader.Float(audio);
                            if(audio.Id==0x9f) track.Channels=reader.UInt(audio);
                        }
                        break;
                }
            }
            if(track.Id==0 || tracks.ContainsKey(track.Id)) throw new InvalidDataException("Invalid/duplicate Matroska TrackNumber.");
            if(track.Enabled)
            {
                if(track.Unsupported.Count>0) throw new NotSupportedException("Enabled track uses unsupported "+string.Join(", ",track.Unsupported)+".");
                bool supported=track.Type==1 && track.Codec is "V_MPEG4/ISO/AVC" or "V_MPEGH/ISO/HEVC"
                    || track.Type==2 && track.Codec=="A_AAC"
                    || track.Type==17 && track.Codec is "S_TEXT/UTF8" or "S_TEXT/WEBVTT" or "S_TEXT/ASS" or "S_TEXT/SSA" or "S_HDMV/PGS";
                if(!supported) throw new NotSupportedException("Unsupported enabled Matroska track: "+track.Codec+"; nothing was silently dropped.");
                if(track.Type==1)
                {
                    if(track.Width is 0 or >8192 || track.Height is 0 or >8192 || track.Width*track.Height>16_777_216) throw new InvalidDataException("Invalid/oversized Matroska video dimensions.");
                    if(track.DisplayWidth!=0 && track.DisplayWidth!=track.Width || track.DisplayHeight!=0 && track.DisplayHeight!=track.Height)
                        throw new NotSupportedException("Non-square-pixel Matroska display dimensions.");
                    int minimum=track.Codec=="V_MPEGH/ISO/HEVC"?23:7;
                    if(track.Private.Length<minimum || track.Private[0]!=1) throw new InvalidDataException("AVC/HEVC CodecPrivate configuration is missing/truncated.");
                }
                if(track.Type==2)
                {
                    if(!double.IsFinite(track.Rate) || track.Rate<7350 || track.Rate>96000 || track.Channels is 0 or >8
                        || track.OutputRate is {} output && (output<7350 || output>192000)) throw new InvalidDataException("Invalid AAC rate/channels.");
                    track.AacStep=AacFrameDuration(track.Private,track.Rate);
                }
            }
            tracks.Add(track.Id,track); order.Add(track.Id);
        }
        bool Flag(Element field) { ulong value=reader.UInt(field); if(value>1) throw new InvalidDataException("Matroska flag is not boolean."); return value==1; }
        void ParseCluster(Element cluster)
        {
            ulong? timestamp=null; var blocks=new List<Element>();
            foreach(var field in reader.Children(cluster.Start,cluster.End))
            {
                if(field.Id==0xe7) {if(timestamp is not null) throw new InvalidDataException("Duplicate Cluster Timestamp.");timestamp=reader.UInt(field);}
                if(field.Id is 0xa3 or 0xa0) { if(blocks.Count>=MaximumPackets) throw new NotSupportedException("Too many Blocks in Cluster.");blocks.Add(field); }
            }
            if(timestamp is null) throw new InvalidDataException("Cluster Timestamp missing.");
            foreach(var field in blocks)
            {
                token.ThrowIfCancellationRequested();
                if(field.Id==0xa3) ParseBlock(field,timestamp.Value,null,null);
                else
                {
                    Element? block=null; ulong? duration=null; bool reference=false,unsupported=false;
                    foreach(var child in reader.Children(field.Start,field.End))
                    {
                        if(child.Id==0xa1) {if(block is not null) throw new InvalidDataException("Multiple Blocks in BlockGroup.");block=child;}
                        if(child.Id==0x9b) {if(duration is not null) throw new InvalidDataException("Duplicate BlockDuration.");duration=reader.UInt(child);}
                        if(child.Id==0xfb) {_=reader.Signed(child);reference=true;}
                        if(child.Id==0x75a2 && reader.Signed(child)!=0) unsupported=true;
                        if(child.Id is 0xa4 or 0x75a1 or 0xa2) unsupported=true;
                    }
                    if(block is not {} value) throw new InvalidDataException("BlockGroup has no Block.");
                    if(!BlockTrack(value).Enabled) continue;
                    if(unsupported) throw new NotSupportedException("CodecState/BlockAdditions/virtual Blocks or DiscardPadding are not supported.");
                    ParseBlock(value,timestamp.Value,duration,!reference);
                }
            }
        }
        Track BlockTrack(Element block)
        {
            var (number,_,_)=reader.Vint(block.Start,block.End,false);
            if(number==0 || !tracks.TryGetValue(number,out var track)) throw new InvalidDataException("Block references unknown TrackNumber.");
            return track;
        }
        void ParseBlock(Element block,ulong cluster,ulong? durationTicks,bool? groupKeyframe)
        {
            if(block.Length>67_108_864) throw new NotSupportedException("Matroska Block exceeds 64 MiB.");
            Track track=BlockTrack(block); if(!track.Enabled) return;
            var (_,width,_)=reader.Vint(block.Start,block.End,false); int cursor=block.Start+width;
            reader.Require(cursor,3,block.End); short relative=BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(cursor,2));byte flags=data[cursor+2];cursor+=3;
            if((flags&8)!=0) throw new NotSupportedException("Invisible video frames are not supported.");
            int mode=(flags>>1)&3; var sizes=new List<int>();
            if(mode==0) sizes.Add(block.End-cursor);
            else
            {
                if(!track.Lacing) throw new InvalidDataException("Lacing is disabled for this track.");
                reader.Require(cursor,1,block.End);int count=data[cursor++]+1;
                if(count<2) throw new InvalidDataException("A lace must contain multiple frames.");
                if(mode==2)
                {
                    if((block.End-cursor)%count!=0) throw new InvalidDataException("Fixed lacing sizes disagree.");
                    for(int i=0;i<count;i++) sizes.Add((block.End-cursor)/count);
                }
                else if(mode==1)
                {
                    for(int i=0;i<count-1;i++)
                    {
                        int size=0;byte next;
                        do {reader.Require(cursor,1,block.End);next=data[cursor++];size=checked(size+next);if((cursor&4095)==0)token.ThrowIfCancellationRequested();} while(next==255);
                        sizes.Add(size);
                    }
                }
                else
                {
                    var (first,length,_)=reader.Vint(cursor,block.End,false);cursor+=length;
                    if(first>(ulong)block.Length) throw new InvalidDataException("First EBML lace size exceeds Block.");sizes.Add((int)first);
                    for(int i=1;i<count-1;i++)
                    {
                        var (encoded,deltaWidth,_)=reader.Vint(cursor,block.End,false);cursor+=deltaWidth;
                        long bias=(1L<<(7*deltaWidth-1))-1, next=checked(sizes[^1]+(long)encoded-bias);
                        if(next<=0 || next>block.Length) throw new InvalidDataException("EBML lace delta produces an invalid size.");sizes.Add((int)next);
                    }
                }
                if(mode!=2)
                {
                    long used=sizes.Sum(s=>(long)s); if(used>=block.End-cursor) throw new InvalidDataException("Lacing payload exceeds Block.");
                    sizes.Add(block.End-cursor-(int)used);
                }
            }
            if(sizes.Any(s=>s<=0)) throw new InvalidDataException("Empty Matroska lace frame.");
            decimal? step=durationTicks is {} ticks?(decimal)ticks*track.Scale*timestampScale/sizes.Count
                :track.DefaultDuration>0?track.DefaultDuration:track.Type==2?track.AacStep:null;
            if(sizes.Count>1 && (step is null || step<=0)) throw new NotSupportedException("Laced frames require a known contiguous duration.");
            if(step<0 || step==0 && track.Type!=17) throw new InvalidDataException("Invalid packet duration.");
            decimal start=((decimal)cluster+relative*track.Scale)*timestampScale;
            for(int index=0;index<sizes.Count;index++)
            {
                token.ThrowIfCancellationRequested();int size=sizes[index];reader.Require(cursor,size,block.End);
                if(track.Type==17 && size>1_048_576) throw new NotSupportedException("Subtitle packet exceeds 1 MiB.");
                if(++packetCount>MaximumPackets) throw new NotSupportedException("Matroska packet-count limit exceeded.");
                long pts=Nanoseconds(start+(step??0)*index);
                long? duration=step is {} d?checked(Nanoseconds(d*(index+1))-Nanoseconds(d*index)):null;
                track.Packets.Add(new(Copy(cursor,size),pts,duration,track.Type==2 || (groupKeyframe??((flags&0x80)!=0))));cursor+=size;
            }
            if(cursor!=block.End) throw new InvalidDataException("Block payload accounting mismatch.");
        }
        byte[] Copy(int start,int length)
        {
            copiedBytes=checked(copiedBytes+length);if(copiedBytes>MaximumFileBytes)throw new NotSupportedException("Retained Matroska packet/metadata budget exceeds 512 MiB.");
            return data.AsSpan(start,length).ToArray();
        }
    }
    static long Nanoseconds(decimal value)
    {
        value=decimal.Round(value,0,MidpointRounding.AwayFromZero);
        if(value<long.MinValue || value>long.MaxValue) throw new InvalidDataException("Matroska timestamp exceeds Int64 nanoseconds.");
        return (long)value;
    }
    static decimal AacFrameDuration(byte[] bytes,double declaredRate)
    {
        if(bytes.Length is <2 or >64) throw new InvalidDataException("AAC AudioSpecificConfig is missing or oversized.");
        int position=0;
        int Bits(int count)
        {
            if(position+count>bytes.Length*8) throw new InvalidDataException("Truncated AAC AudioSpecificConfig.");int value=0;
            for(int i=0;i<count;i++)value=(value<<1)|(bytes[position/8]>>(7-position++%8)&1);return value;
        }
        int ObjectType(){int value=Bits(5);return value==31?32+Bits(6):value;}
        int Frequency()
        {
            int[] frequencies=[96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350];int index=Bits(4);
            int value=index==15?Bits(24):index<frequencies.Length?frequencies[index]:throw new InvalidDataException("Reserved AAC frequency.");
            if(value is <7350 or >96000)throw new NotSupportedException("AAC frequency is outside supported bounds.");return value;
        }
        int type=ObjectType();if(type is not (2 or 5 or 29))throw new NotSupportedException("Only AAC-LC/HE-AAC configuration timing is supported.");
        int rate=Frequency(),channels=Bits(4);
        if(channels is <1 or >7)throw new NotSupportedException("AAC program-config-element layout is not supported.");
        if(type is 5 or 29){_=Frequency();if(ObjectType()!=2)throw new NotSupportedException("HE-AAC requires an LC core.");}
        int frameLength=Bits(1)==0?1024:960;
        if(Bits(1)!=0 || Bits(1)!=0)throw new NotSupportedException("AAC core-coder/GA extension timing is not supported.");
        if(Math.Abs(declaredRate-rate)>.5)throw new InvalidDataException("AAC SamplingFrequency differs from its core configuration.");
        return 1_000_000_000m*frameLength/rate;
    }
    private sealed class Reader(byte[] data,CancellationToken token)
    {
        int elements;
        public void Require(int offset,int count,int limit)
        {if(offset<0 || count<0 || limit<0 || limit>data.Length || offset>limit || count>limit-offset)throw new InvalidDataException("Truncated EBML element.");}
        public (ulong Value,int Width,bool AllOnes) Vint(int offset,int limit,bool id)
        {
            Require(offset,1,limit);byte first=data[offset];if(first==0)throw new InvalidDataException("Zero EBML VINT prefix.");
            int width=1;byte mask=128;while((first&mask)==0){width++;mask>>=1;}
            if(width>(id?4:8))throw new InvalidDataException("Oversized EBML VINT.");Require(offset,width,limit);
            ulong value=(ulong)(id?first:first&(mask-1));for(int i=1;i<width;i++)value=(value<<8)|data[offset+i];
            return(value,width,!id && value==(1UL<<(7*width))-1);
        }
        public Element ElementAt(int offset,int limit,bool allowUnknownSegment=false)
        {
            if((elements++&1023)==0)token.ThrowIfCancellationRequested();if(elements>2_000_000)throw new NotSupportedException("EBML element-count limit exceeded.");
            var(id,idWidth,_)=Vint(offset,limit,true);var(size,sizeWidth,unknown)=Vint(offset+idWidth,limit,false);int start=offset+idWidth+sizeWidth;
            if(unknown)
            {if(!allowUnknownSegment || id!=0x18538067)throw new NotSupportedException("Unknown-sized elements other than Segment are not supported.");return new(id,start,limit);}
            if(size>(ulong)(limit-start))throw new InvalidDataException("EBML element exceeds parent.");return new(id,start,start+(int)size);
        }
        public IEnumerable<Element> Children(int start,int end,bool allowUnknownSegment=false)
        {Require(start,end-start,end);for(int cursor=start;cursor<end;){var field=ElementAt(cursor,end,allowUnknownSegment);yield return field;cursor=field.End;}}
        public ulong UInt(Element field)
        {if(field.Length>8)throw new InvalidDataException("Oversized EBML integer.");ulong value=0;for(int i=field.Start;i<field.End;i++)value=(value<<8)|data[i];return value;}
        public long Signed(Element field)
        {ulong value=UInt(field);if(field.Length==0)return 0;if(field.Length==8)return unchecked((long)value);int shift=64-field.Length*8;return (long)(value<<(shift))>>shift;}
        public double Float(Element field)
        {
            double value=field.Length switch{4=>BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(field.Start,4))),8=>BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(field.Start,8))),_=>throw new InvalidDataException("Invalid EBML float width.")};
            if(!double.IsFinite(value))throw new InvalidDataException("Nonfinite EBML float.");return value;
        }
        public string Text(Element field)
        {
            if(field.Length>4096)throw new NotSupportedException("EBML text exceeds 4096 bytes.");
            try{return new UTF8Encoding(false,true).GetString(data,field.Start,field.Length).TrimEnd('\0');}
            catch(DecoderFallbackException error){throw new InvalidDataException("Invalid UTF-8 EBML text.",error);}
        }
    }
}
