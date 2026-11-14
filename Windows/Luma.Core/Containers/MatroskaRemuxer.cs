using Luma.Core.Subtitles;

namespace Luma.Core.Containers;

public sealed record MatroskaRemuxResult(IReadOnlyList<DemuxedTrack> AudioTracks, IReadOnlyList<DemuxedTrack> SubtitleTracks,
    ulong? SelectedAudioTrackId, long OriginNanoseconds, string VideoCodec);

/// <summary>Original container conversion. AVC/HEVC/AAC bytes are retained; decoding belongs to Windows.</summary>
public static class MatroskaRemuxer
{
    public static MatroskaRemuxResult Remux(DemuxedMatroska source, Stream output, ulong? audioTrackId = null, bool includeAudio = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var video = source.Tracks.Where(t => t.TrackType == 1).ToArray();
        var audio = source.Tracks.Where(t => t.TrackType == 2).ToArray();
        if (video.Length > 1) throw new NotSupportedException("Multiple Matroska video tracks are not implemented.");
        DemuxedTrack? selected = null;
        if (includeAudio)
        {
            selected = audioTrackId is ulong id ? audio.FirstOrDefault(t => t.Id == id) ?? throw new InvalidDataException("Selected Matroska audio track is unavailable.")
                : audio.FirstOrDefault(t => t.IsDefault) ?? audio.FirstOrDefault();
        }
        var tracks = new List<Mp4Track>();
        if (video.Length == 1)
        {
            var track = video[0];
            tracks.Add(new(1, track.CodecId, track.CodecPrivate, track.Width, track.Height, 0, 0,
                MatroskaTimeline.Video(track, source.TimestampScaleNanoseconds, cancellationToken)));
        }
        if (selected is not null)
        {
            if (selected.CodecId != "A_AAC") throw new NotSupportedException("This Matroska audio codec has no original Windows remux path: " + selected.CodecId);
            tracks.Add(new(2, selected.CodecId, selected.CodecPrivate, 0, 0, checked((int)selected.SampleRate), selected.Channels,
                MatroskaTimeline.Aac(selected, source.TimestampScaleNanoseconds, cancellationToken)));
        }
        if (tracks.Count == 0) throw new NotSupportedException("No selected playable Matroska audio or video track.");
        IsoMediaMuxer.Write(output, tracks, cancellationToken);
        var origin = tracks.SelectMany(t => t.Samples).Min(s => s.PtsNanoseconds);
        return new(audio.Select(t => t with { Packets = [] }).ToArray(), source.Tracks.Where(t => t.TrackType == 17).ToArray(), selected?.Id, origin,
            video.Length == 0 ? "AAC audio" : video[0].CodecId == "V_MPEGH/ISO/HEVC" ? "HEVC" : "AVC");
    }
}
