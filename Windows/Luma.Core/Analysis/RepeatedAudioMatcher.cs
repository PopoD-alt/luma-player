namespace Luma.Core.Analysis;

/// <summary>Repeated audio is a candidate for explicit review, never a credits label inferred with certainty.</summary>
public sealed record SkipAudioCandidate(string Kind, double FirstStartSeconds, double FirstEndSeconds,
    double SecondStartSeconds, double SecondEndSeconds, double Similarity);

/// <summary>Original bounded Goertzel spectral fingerprint, with no model or external codec process.</summary>
public sealed class RepeatedAudioFingerprint
{
    public const int SampleRate = 8000, FrameSamples = 1000;
    public const double FrameSeconds = .125;
    static readonly double[] Coefficients = new double[] { 100, 150, 210, 290, 390, 520, 690, 910, 1200, 1570, 2050, 2670, 3450 }
        .Select(f => 2 * Math.Cos(2 * Math.PI * f / SampleRate)).ToArray();
    readonly int maximumFrames;
    readonly short[] pending = new short[FrameSamples];
    readonly List<double[]?> frames = [];
    int count;
    internal IReadOnlyList<double[]?> Frames => frames;
    public int FrameCount => frames.Count;
    public RepeatedAudioFingerprint(double maximumSeconds)
    {
        if (!double.IsFinite(maximumSeconds) || maximumSeconds < 0 || maximumSeconds > 180) throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        maximumFrames = (int)Math.Floor(maximumSeconds / FrameSeconds);
    }
    public void Append(ReadOnlySpan<short> samples)
    {
        foreach (short value in samples)
        {
            if (frames.Count >= maximumFrames) return;
            pending[count++] = value;
            if (count == FrameSamples) { frames.Add(Feature(pending)); count = 0; }
        }
    }
    static double[]? Feature(short[] samples)
    {
        double rms = samples.Sum(x => (double)x * x) / FrameSamples;
        if (rms < 10000) return null;
        double[] energies = new double[Coefficients.Length];
        for (int i = 0; i < Coefficients.Length; i++)
        {
            double a = 0, b = 0, coefficient = Coefficients[i];
            foreach (short sample in samples) { double next = sample + coefficient * a - b; b = a; a = next; }
            energies[i] = Math.Log(1 + Math.Max(0, a * a + b * b - coefficient * a * b));
        }
        double mean = energies.Average();
        double[] centered = energies.Select(x => x - mean).ToArray();
        double norm = Math.Sqrt(centered.Sum(x => x * x));
        return norm > .000001 ? centered.Select(x => x / norm).ToArray() : null;
    }
}

public static class RepeatedAudioMatcher
{
    public static IReadOnlyList<SkipAudioCandidate> Match(RepeatedAudioFingerprint first, RepeatedAudioFingerprint second,
        string kind, double firstOffset, double secondOffset, double minimumSeconds = 15, CancellationToken cancellationToken = default)
    {
        if (kind is not ("intro" or "credits") || !double.IsFinite(minimumSeconds) || minimumSeconds is < 1 or > 180 ||
            !double.IsFinite(firstOffset) || !double.IsFinite(secondOffset) || firstOffset < 0 || secondOffset < 0 || firstOffset > 31536000 || secondOffset > 31536000) return [];
        var a = first.Frames; var b = second.Frames;
        if (a.Count == 0 || b.Count == 0) return [];
        int minimum = (int)Math.Ceiling(minimumSeconds / RepeatedAudioFingerprint.FrameSeconds);
        var output = new List<SkipAudioCandidate>();
        void Accept(int start, int count, int offset, double score)
        {
            if (count < minimum) return;
            var values = a.Skip(start).Take(count).Where(v => v is not null).Select(v => v!).ToArray();
            int unique = values.Select(row => string.Join(',', row.Select(v => (int)Math.Round(v * 10)))).Distinct().Count();
            if (unique < Math.Min(16, Math.Max(6, minimum / 2))) return;
            double[] centroid = Enumerable.Range(0, 13).Select(i => values.Average(row => row[i])).ToArray();
            double variance = values.Average(row => row.Zip(centroid).Sum(pair => Math.Pow(pair.First - pair.Second, 2)));
            if (variance < .01) return;
            const double step = RepeatedAudioFingerprint.FrameSeconds;
            output.Add(new(kind, firstOffset + start * step, firstOffset + (start + count) * step,
                secondOffset + (start + offset) * step, secondOffset + (start + offset + count) * step, Math.Clamp(score / count, 0, 1)));
        }
        for (int offset = 1 - a.Count; offset < b.Count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int run = 0, start = 0; double score = 0;
            for (int i = Math.Max(0, -offset); i < Math.Min(a.Count, b.Count - offset); i++)
            {
                int j = i + offset;
                double similarity = a[i] is { } x && b[j] is { } y ? x.Zip(y).Sum(pair => pair.First * pair.Second) : -1;
                if (similarity >= .99) { if (run == 0) start = i; run++; score += similarity; }
                else { Accept(start, run, offset, score); run = 0; score = 0; }
            }
            Accept(start, run, offset, score);
        }
        var picked = new List<SkipAudioCandidate>();
        foreach (var candidate in output.OrderByDescending(c => c.FirstEndSeconds - c.FirstStartSeconds))
        {
            if (picked.Any(p => candidate.FirstStartSeconds >= p.FirstStartSeconds && candidate.FirstEndSeconds <= p.FirstEndSeconds)) continue;
            picked.Add(candidate); if (picked.Count == 4) break;
        }
        return picked;
    }
}
