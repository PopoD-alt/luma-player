using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Luma.Core.Analysis;

/// <summary>Optional local worker analysis. Uses the existing libVLC3 runtime and never changes the playback player.
/// Four bounded head/tail windows produce fingerprints only. All candidate ranges require explicit user confirmation.</summary>
public static class SkipAudioAnalyzer
{
    static readonly SemaphoreSlim Job = new(1, 1);
    public static async Task<IReadOnlyList<SkipAudioCandidate>> AnalyzeAsync(string firstPath, string secondPath, string runtimePath,
        CancellationToken cancellationToken, IProgress<double>? progress = null, double minimumMatchSeconds = 15, double maxWindowSeconds = 180)
    {
        if (!double.IsFinite(minimumMatchSeconds) || !double.IsFinite(maxWindowSeconds) || minimumMatchSeconds < 1 || maxWindowSeconds is < 2 or > 180 || minimumMatchSeconds > maxWindowSeconds)
            throw new ArgumentOutOfRangeException(nameof(maxWindowSeconds), "The requested analysis limits are invalid.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Job.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Another audio analysis is already running.");
        try
        {
            return await Task.Run(() => Analyze(firstPath, secondPath, runtimePath, cancellationToken, progress, minimumMatchSeconds, maxWindowSeconds), cancellationToken).ConfigureAwait(false);
        }
        finally { Job.Release(); }
    }
    readonly record struct FileStamp(long Length, DateTime Modified, DateTime Created)
    {
        public static FileStamp Read(string path)
        {
            try { var file = new FileInfo(path); if (!file.Exists) throw new IOException(); return new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc); }
            catch { throw new IOException("A selected local file is unavailable."); }
        }
    }
    static IReadOnlyList<SkipAudioCandidate> Analyze(string firstPath, string secondPath, string runtimePath, CancellationToken cancellationToken,
        IProgress<double>? progress, double minimumMatchSeconds, double maxWindowSeconds)
    {
        string first, second;
        try { first = Path.GetFullPath(firstPath); second = Path.GetFullPath(secondPath); }
        catch { throw new IOException("Choose two different local episodes from the same series."); }
        if (!Path.IsPathFullyQualified(runtimePath) || string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Choose two different local episodes from the same series.");
        var firstStamp = FileStamp.Read(first); var secondStamp = FileStamp.Read(second);
        var watch = Stopwatch.StartNew();
        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromMinutes(20)) throw new TimeoutException("Audio analysis exceeded its time limit.");
        }
        Check(); progress?.Report(0);
        using var decoder = new Decoder(runtimePath);
        double durationA = decoder.Duration(first, Check), durationB = decoder.Duration(second, Check);
        double windowA = Math.Min(maxWindowSeconds, durationA / 2), windowB = Math.Min(maxWindowSeconds, durationB / 2);
        if (Math.Min(windowA, windowB) < minimumMatchSeconds) throw new IOException("Both episodes need a known duration and a long enough audio window.");
        var headA = decoder.Window(first, 0, windowA, Check, value => progress?.Report(Math.Min(.24, value * .24)));
        var headB = decoder.Window(second, 0, windowB, Check, value => progress?.Report(.24 + Math.Min(.24, value * .24)));
        double tailAStart = durationA - windowA, tailBStart = durationB - windowB;
        var tailA = decoder.Window(first, tailAStart, windowA, Check, value => progress?.Report(.48 + Math.Min(.24, value * .24)));
        var tailB = decoder.Window(second, tailBStart, windowB, Check, value => progress?.Report(.72 + Math.Min(.24, value * .24)));
        Check();
        var result = RepeatedAudioMatcher.Match(headA, headB, "intro", 0, 0, minimumMatchSeconds, cancellationToken)
            .Concat(RepeatedAudioMatcher.Match(tailA, tailB, "credits", tailAStart, tailBStart, minimumMatchSeconds, cancellationToken)).ToArray();
        Check();
        if (FileStamp.Read(first) != firstStamp || FileStamp.Read(second) != secondStamp) throw new IOException("A selected file changed during analysis. Try again with the current files.");
        progress?.Report(1); return result;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr New(int count, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Release(IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NewMedia(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NewPlayer(IntPtr media);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AddOption(IntPtr media, [MarshalAs(UnmanagedType.LPUTF8Str)] string option);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Parse(IntPtr media, uint flags, int timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetInt(IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GetTime(IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetCallbacks(IntPtr player, IntPtr play, IntPtr pause, IntPtr resume, IntPtr flush, IntPtr drain, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetFormat(IntPtr player, [MarshalAs(UnmanagedType.LPUTF8Str)] string format, uint rate, uint channels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PlayCallback(IntPtr context, IntPtr samples, uint count, long pts);
    static readonly PlayCallback AudioCallback = Receive;
    static void Receive(IntPtr context, IntPtr samples, uint count, long pts)
    {
        if (context == IntPtr.Zero) return;
        // All exceptions remain on the managed side; none may unwind through native decoder frames.
        try { if (GCHandle.FromIntPtr(context).Target is Capture capture) capture.Receive(samples, count, pts); }
        catch { /* Capture.Receive marks bounded-input failures itself. Runtime lifetime is retained through stop/release. */ }
    }
    sealed class Capture(double seconds)
    {
        readonly object gate = new();
        readonly RepeatedAudioFingerprint fingerprint = new(seconds);
        readonly long maximumSamples = (long)Math.Ceiling(seconds * 8000) + 16000;
        long samples, previousPts; uint previousCount;
        bool invalid, hasPts;
        public void Receive(IntPtr pointer, uint count, long pts)
        {
            lock (gate)
            {
                try
                {
                    if (invalid || pointer == IntPtr.Zero || count > 32000 || samples + count > maximumSamples) { invalid = true; return; }
                    if (hasPts && Math.Abs((double)pts - (previousPts + previousCount * 125d)) > 250000) { invalid = true; return; }
                    hasPts = true; previousPts = pts; previousCount = count; samples += count;
                    short[] values = new short[count]; Marshal.Copy(pointer, values, 0, (int)count); fingerprint.Append(values);
                }
                catch { invalid = true; }
            }
        }
        public (long Samples, bool Invalid) Status() { lock (gate) return (samples, invalid); }
        public RepeatedAudioFingerprint Result()
        {
            lock (gate)
            {
                if (invalid || Math.Abs(samples / 8000d - seconds) > .5) throw new IOException("This audio window has gaps or incomplete timing; no skip timings were accepted.");
                return fingerprint;
            }
        }
    }
    sealed class Decoder : IDisposable
    {
        IntPtr library, instance;
        readonly Release releaseInstance, releaseMedia, releasePlayer, stop, parseStop;
        readonly NewMedia newMedia; readonly NewPlayer newPlayer; readonly AddOption addOption; readonly Parse parse;
        readonly GetInt parsed, play, state; readonly GetTime mediaDuration;
        readonly SetCallbacks callbacks; readonly SetFormat format;
        public Decoder(string runtimePath)
        {
            try
            {
                library = OperatingSystem.IsWindows()
                    ? NativeLibrary.Load(runtimePath, typeof(SkipAudioAnalyzer).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories)
                    : NativeLibrary.Load(runtimePath);
                T Symbol<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
                string version = Marshal.PtrToStringUTF8(Symbol<GetVersion>("libvlc_get_version")()) ?? "";
                if (!version.StartsWith("3.", StringComparison.Ordinal)) throw new InvalidOperationException();
                var create = Symbol<New>("libvlc_new");
                releaseInstance = Symbol<Release>("libvlc_release"); releaseMedia = Symbol<Release>("libvlc_media_release");
                releasePlayer = Symbol<Release>("libvlc_media_player_release"); stop = Symbol<Release>("libvlc_media_player_stop");
                newMedia = Symbol<NewMedia>("libvlc_media_new_path"); newPlayer = Symbol<NewPlayer>("libvlc_media_player_new_from_media");
                addOption = Symbol<AddOption>("libvlc_media_add_option"); parse = Symbol<Parse>("libvlc_media_parse_with_options");
                parseStop = Symbol<Release>("libvlc_media_parse_stop"); parsed = Symbol<GetInt>("libvlc_media_get_parsed_status");
                mediaDuration = Symbol<GetTime>("libvlc_media_get_duration"); play = Symbol<GetInt>("libvlc_media_player_play");
                state = Symbol<GetInt>("libvlc_media_player_get_state"); callbacks = Symbol<SetCallbacks>("libvlc_audio_set_callbacks"); format = Symbol<SetFormat>("libvlc_audio_set_format");
                string[] options = ["--ignore-config", "--no-video", "--no-lua", "--no-metadata-network-access", "--no-sub-autodetect-file", "--quiet"];
                IntPtr[] strings = options.Select(Marshal.StringToCoTaskMemUTF8).ToArray(); IntPtr argv = Marshal.AllocHGlobal(strings.Length * IntPtr.Size);
                try { Marshal.Copy(strings, 0, argv, strings.Length); instance = create(strings.Length, argv); }
                finally { Marshal.FreeHGlobal(argv); foreach (IntPtr pointer in strings) Marshal.FreeCoTaskMem(pointer); }
                if (instance == IntPtr.Zero) throw new InvalidOperationException();
            }
            catch { if (library != IntPtr.Zero) { NativeLibrary.Free(library); library = IntPtr.Zero; } throw new InvalidOperationException("The current playback runtime cannot analyze audio."); }
        }
        public void Dispose()
        {
            if (instance != IntPtr.Zero) { releaseInstance(instance); instance = IntPtr.Zero; }
            if (library != IntPtr.Zero) { NativeLibrary.Free(library); library = IntPtr.Zero; }
        }
        public double Duration(string path, Action check)
        {
            IntPtr media = newMedia(instance, path);
            if (media == IntPtr.Zero) throw new IOException("Audio analysis could not open a selected local file.");
            try
            {
                if (parse(media, 0, 10000) != 0) throw new IOException("Both episodes need a known, finite duration and an audio track.");
                var watch = Stopwatch.StartNew();
                while (parsed(media) == 0) { check(); if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Audio analysis exceeded its metadata time limit."); Thread.Sleep(50); }
                double length = mediaDuration(media) / 1000d;
                if (parsed(media) != 4 || !double.IsFinite(length) || length <= 0 || length > 31536000) throw new IOException("Both episodes need a known, finite duration and an audio track.");
                return length;
            }
            finally { parseStop(media); releaseMedia(media); }
        }
        public RepeatedAudioFingerprint Window(string path, double start, double seconds, Action check, Action<double> progress)
        {
            check(); IntPtr media = newMedia(instance, path), player = IntPtr.Zero; GCHandle context = default;
            if (media == IntPtr.Zero) throw new IOException("Audio analysis could not open a selected local file.");
            try
            {
                addOption(media, ":start-time=" + start.ToString("F6", CultureInfo.InvariantCulture));
                addOption(media, ":stop-time=" + (start + seconds).ToString("F6", CultureInfo.InvariantCulture));
                player = newPlayer(media); if (player == IntPtr.Zero) throw new IOException("Audio analysis could not initialize its decoder.");
                var capture = new Capture(seconds); context = GCHandle.Alloc(capture);
                callbacks(player, Marshal.GetFunctionPointerForDelegate(AudioCallback), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, GCHandle.ToIntPtr(context));
                format(player, "S16N", 8000, 1);
                if (play(player) != 0) throw new IOException("Audio analysis could not decode this audio window.");
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    check(); int observed = state(player); var status = capture.Status();
                    if (status.Invalid) throw new IOException("This audio window has gaps or incomplete timing; no skip timings were accepted.");
                    progress(Math.Min(1, status.Samples / 8000d / seconds));
                    if (observed == 6) break;
                    if (observed == 7) throw new IOException("Audio analysis could not decode this audio window.");
                    if (watch.Elapsed.TotalSeconds > seconds + 20) throw new TimeoutException("Audio analysis exceeded its decoder time limit.");
                    Thread.Sleep(50);
                }
                return capture.Result();
            }
            finally
            {
                if (player != IntPtr.Zero) { stop(player); releasePlayer(player); }
                if (context.IsAllocated) context.Free();
                releaseMedia(media); GC.KeepAlive(AudioCallback);
            }
        }
    }
}
