namespace Luma.Core.Containers;

/// <summary>Bounded packet timing adaptation. Compressed packets stay in coding order.</summary>
public static class MatroskaTimeline
{
    public static IReadOnlyList<Mp4Sample> Video(DemuxedTrack track, long timestampScaleNanoseconds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packets = track.Packets;
        if (packets.Count is < 1 or > 2_000_000 || timestampScaleNanoseconds is < 1 or > 1_000_000_000)
            throw new InvalidDataException("Invalid Matroska video packet count or timestamp precision.");
        try
        {
            bool reordered = false;
            for (int i = 1; i < packets.Count; i++) if (packets[i].TimestampNanoseconds <= packets[i - 1].TimestampNanoseconds) reordered = true;
            var result = new Mp4Sample[packets.Count];
            if (reordered)
            {
                long step = track.DefaultDurationNanoseconds;
                if (step <= checked(timestampScaleNanoseconds * 2) || !packets[0].Keyframe)
                    throw new NotSupportedException("Reordered AVC/HEVC requires a random-access start and a precise positive DefaultDuration.");
                var order = Enumerable.Range(0, packets.Count).OrderBy(i => packets[i].TimestampNanoseconds).ToArray();
                if (order[0] != 0) throw new NotSupportedException("Open-GOP/preroll Matroska video is not implemented.");
                long first = packets[0].TimestampNanoseconds, latestOrigin = first;
                Int128 span = (Int128)packets[order[^1]].TimestampNanoseconds - first;
                // DefaultDuration is an integer nanosecond value. Fit the actual
                // presentation endpoints so 30000/1001 and30fps do not accrue
                // truncation drift, while still rejecting a non-CFR grid.
                if (Int128.Abs(span - (Int128)step * (order.Length - 1)) > order.Length - 1 + (Int128)timestampScaleNanoseconds * 2)
                    throw new NotSupportedException("Matroska presentation cadence disagrees with DefaultDuration.");
                long Offset(int index) => checked((long)(span * index / (order.Length - 1)));
                int lag = 0;
                for (int rank = 0; rank < order.Length; rank++)
                {
                    if ((rank & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    long pts = packets[order[rank]].TimestampNanoseconds;
                    if (rank > 0 && pts == packets[order[rank - 1]].TimestampNanoseconds)
                        throw new NotSupportedException("Duplicate Matroska video presentation timestamps.");
                    long expected = checked(first + Offset(rank));
                    if (Int128.Abs((Int128)pts - expected) > timestampScaleNanoseconds + 1)
                        throw new NotSupportedException("Reordered Matroska video requires a complete constant-frame-rate presentation timeline.");
                    lag = Math.Max(lag, order[rank] - rank);
                }
                if (lag > 16) throw new NotSupportedException("Matroska video reorder lag exceeds the 16-frame bound.");
                for (int i = 0; i < packets.Count; i++) latestOrigin = Math.Min(latestOrigin, checked(packets[i].TimestampNanoseconds - Offset(i)));
                long origin = Math.Min(checked(first - Offset(lag)), latestOrigin);
                for (int i = 0; i < packets.Count; i++)
                {
                    if ((i & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var packet = packets[i];
                    long duration = checked(Offset(i + 1) - Offset(i));
                    if (packet.DurationNanoseconds is long declared && (declared <= 0 || Int128.Abs((Int128)declared - duration) > timestampScaleNanoseconds + 1))
                        throw new NotSupportedException("Reordered Matroska video has inconsistent frame durations.");
                    result[i] = new(packet.Data, checked(origin + Offset(i)), packet.TimestampNanoseconds, duration, packet.Keyframe);
                }
                return result;
            }
            for (int i = 0; i < packets.Count; i++)
            {
                if ((i & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                var packet = packets[i];
                long duration = i + 1 < packets.Count ? checked(packets[i + 1].TimestampNanoseconds - packet.TimestampNanoseconds)
                    : packet.DurationNanoseconds ?? (track.DefaultDurationNanoseconds > 0 ? track.DefaultDurationNanoseconds : i > 0 ? result[i - 1].DurationNanoseconds : 0);
                if (duration <= 0) throw new NotSupportedException("Matroska video frame duration is unavailable or invalid.");
                if (packet.DurationNanoseconds is long declared && Int128.Abs((Int128)declared - duration) > timestampScaleNanoseconds)
                    throw new NotSupportedException("Gapped or overlapping Matroska video durations are not implemented.");
                result[i] = new(packet.Data, packet.TimestampNanoseconds, packet.TimestampNanoseconds, duration, packet.Keyframe);
            }
            if (!result[0].Keyframe) throw new NotSupportedException("Matroska video must begin at a random-access frame.");
            return result;
        }
        catch (OverflowException e) { throw new InvalidDataException("Matroska video timeline exceeds its integer clock bounds.", e); }
    }

    public static IReadOnlyList<Mp4Sample> Aac(DemuxedTrack track, long timestampScaleNanoseconds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (track.Packets.Count is < 1 or > 2_000_000 || !double.IsFinite(track.SampleRate) || track.SampleRate is < 1 or > 65535 || track.SampleRate != Math.Truncate(track.SampleRate)
            || timestampScaleNanoseconds is < 1 or > 1_000_000_000)
            throw new NotSupportedException("AAC track rate, packet count or timestamp precision is unsupported.");
        // The muxer separately validates AAC-LC AudioSpecificConfig, including 1024-sample frames.
        // Coarse clocks cannot distinguish quantization from an actual missing/overlapping frame.
        // Require strictly finer than half a frame before replacing timestamps with a sample grid.
        if ((Int128)timestampScaleNanoseconds * 2 * (long)track.SampleRate >= (Int128)1024 * 1_000_000_000)
            throw new NotSupportedException("Matroska AAC timestamp precision must be finer than half an AAC-LC frame.");
        try
        {
            long origin = track.Packets[0].TimestampNanoseconds;
            var result = new Mp4Sample[track.Packets.Count];
            for (int i = 0; i < result.Length; i++)
            {
                if ((i & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                long offset = checked((long)((Int128)i * 1024 * 1_000_000_000 / (long)track.SampleRate));
                long next = checked((long)((Int128)(i + 1) * 1024 * 1_000_000_000 / (long)track.SampleRate));
                long time = checked(origin + offset), duration = next - offset;
                var packet = track.Packets[i];
                if (packet.DurationNanoseconds is <= 0)
                    throw new InvalidDataException("Matroska AAC packet duration must be positive when declared.");
                if (Int128.Abs((Int128)packet.TimestampNanoseconds - time) > timestampScaleNanoseconds
                    || packet.DurationNanoseconds is long declared && Int128.Abs((Int128)declared - duration) > timestampScaleNanoseconds)
                    throw new NotSupportedException("AAC timestamps have a gap, overlap or rate inconsistent with 1024-sample AAC-LC frames.");
                result[i] = new(packet.Data, time, time, duration, true);
            }
            return result;
        }
        catch (OverflowException e) { throw new InvalidDataException("Matroska AAC timeline exceeds its integer clock bounds.", e); }
    }
}
