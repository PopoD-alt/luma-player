using Luma.Core.Analysis;

namespace Luma.Core.Tests;

internal static class RepeatedAudioAnalysisTests
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static short[] Signal(double seconds, int seed, double gain = 1)
    {
        return Enumerable.Range(0, (int)(seconds * 8000)).Select(i =>
        {
            double t = i / 8000d; int note = (int)(t * 8);
            double frequency = 130 + (note * 37 + seed * 13) % 800, mix = .15 + .35 * ((seed + note * 17) % 13) / 12;
            double value = Math.Sin(2 * Math.PI * frequency * t) + mix * Math.Sin(2 * Math.PI * frequency * 2.37 * t + seed);
            return (short)Math.Round(value * 7000 * gain);
        }).ToArray();
    }
    static RepeatedAudioFingerprint Fingerprint(short[] values, double maxSeconds = 6)
    { var result = new RepeatedAudioFingerprint(maxSeconds); result.Append(values); return result; }
    public static void Run()
    {
        var first = Signal(1, 10).Concat(Signal(4, 123)).Concat(Signal(1, 20)).ToArray();
        var second = Signal(2, 11).Concat(Signal(4, 123, .6)).ToArray();
        var a = Fingerprint(first); var b = Fingerprint(second);
        var matches = RepeatedAudioMatcher.Match(a, b, "intro", 0, 0, 3);
        Check(matches.Any(c => Math.Abs(c.FirstStartSeconds - 1) < .15 && Math.Abs(c.FirstEndSeconds - 5) < .15 && Math.Abs(c.SecondStartSeconds - 2) < .15 && c.Similarity >= .99), "Repeated gain/offset audio did not match");
        var tail = Fingerprint(Signal(4, 456), 4);
        var credits = RepeatedAudioMatcher.Match(tail, tail, "credits", 98, 101, 3);
        Check(credits.FirstOrDefault()?.FirstStartSeconds == 98 && credits.FirstOrDefault()?.SecondStartSeconds == 101, "Tail offsets were lost");
        Check(RepeatedAudioMatcher.Match(a, Fingerprint(Signal(6, 999)), "intro", 0, 0, 3).Count == 0, "Unrelated audio matched");
        var silence = Fingerprint(new short[32000], 4);
        Check(RepeatedAudioMatcher.Match(silence, silence, "intro", 0, 0, 3).Count == 0, "Silence matched");
        var tone = Fingerprint(Enumerable.Range(0, 32000).Select(i => (short)Math.Round(Math.Sin(2 * Math.PI * 220 * i / 8000) * 7000)).ToArray(), 4);
        Check(RepeatedAudioMatcher.Match(tone, tone, "intro", 0, 0, 3).Count == 0, "Flat tone matched");
        var shortRepeat = Fingerprint(Signal(2, 123), 2);
        Check(RepeatedAudioMatcher.Match(shortRepeat, shortRepeat, "intro", 0, 0, 3).Count == 0, "Short repeat matched");
        var bounded = Fingerprint(Signal(6, 33), 2);
        Check(bounded.FrameCount == 16, "Fingerprint exceeded its frame cap");
        Check(RepeatedAudioMatcher.Match(a, b, "intro", 0, 0, double.MaxValue).Count == 0, "Huge minimum accepted");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { _ = RepeatedAudioMatcher.Match(a, b, "intro", 0, 0, 3, cancelled.Token); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        try { _ = new RepeatedAudioFingerprint(double.MaxValue); throw new InvalidOperationException("Huge cap accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine("PASS: repeated audio gain/offset matching, tail offsets, unrelated/silence/flat/short rejection, bounds and cancellation");
    }
}
