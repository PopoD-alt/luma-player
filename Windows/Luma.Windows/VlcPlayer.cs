using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Luma.WindowsApp;

internal sealed record VlcTrack(int Id, string Name);
internal sealed class VlcFailureEventArgs(string message) : EventArgs { public string Message { get; } = message; }

/// <summary>Original direct libVLC3 C-ABI host. libVLC owns demux, codecs, A/V clock and native subtitle rendering.</summary>
internal sealed class VlcPlayer : HwndHost
{
    IntPtr window, instance, media, player, events;
    Native.EventCallback? notification;
    readonly List<int> attached = [];
    readonly DispatcherTimer metadataTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    string? source, pendingSubtitle;
    int revision, disposed;
    bool opened, failed, ended, playRequested = true;
    readonly bool dummyAudioForTest;
    double volume = .7, rate = 1, subtitleDelay;
    public FrameworkElement View => this;
    public static string RuntimeVersion => VlcRuntime.Version;
    public static string RuntimePath => VlcRuntime.Path;
    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler? TracksChanged;
    public event EventHandler<VlcFailureEventArgs>? Failed;
    public event EventHandler<VlcFailureEventArgs>? Warning;
    public event EventHandler? DoubleClicked;
    public VlcPlayer(bool useDummyAudioForTest = false) { dummyAudioForTest = useDummyAudioForTest; Focusable = false; metadataTimer.Tick += (_, _) => Refresh(); }
    public double CurrentTime { get { VerifyAccess(); return player == IntPtr.Zero ? 0 : Math.Max(0, Native.libvlc_media_player_get_time(player) / 1000.0); } }
    public double Duration { get { VerifyAccess(); return player == IntPtr.Zero ? 0 : Math.Max(0, Native.libvlc_media_player_get_length(player) / 1000.0); } }
    public bool IsPaused { get { VerifyAccess(); return player == IntPtr.Zero || Native.libvlc_media_player_is_playing(player) == 0; } }
    public int NaturalVideoWidth { get { Size(out int width, out _); return width; } }
    public int NaturalVideoHeight { get { Size(out _, out int height); return height; } }
    public int? DisplayedPictures => Statistics()?.DisplayedPictures;
    public int? DecodedVideo => Statistics()?.DecodedVideo;
    public int? DecodedAudio => Statistics()?.DecodedAudio;
    public int? PlayedAudioBuffers => Statistics()?.PlayedAudioBuffers;
    public double Volume
    {
        get { VerifyAccess(); return volume; }
        set { Live(); if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); volume = Math.Clamp(value, 0, 1); if (player != IntPtr.Zero) Native.libvlc_audio_set_volume(player, (int)Math.Round(volume * 100)); }
    }
    public double Rate
    {
        get { VerifyAccess(); return player == IntPtr.Zero ? rate : Native.libvlc_media_player_get_rate(player); }
        set { Live(); if (!double.IsFinite(value) || value <= 0 || value > 8) throw new ArgumentOutOfRangeException(nameof(value)); rate = value; if (player != IntPtr.Zero && opened && Native.libvlc_media_player_set_rate(player, (float)value) != 0) RaiseFailure("libVLC cannot use that playback speed for this media."); }
    }
    public double SubtitleDelay
    {
        get { VerifyAccess(); return subtitleDelay; }
        set { Live(); if (!double.IsFinite(value) || Math.Abs(value) > 86400) throw new ArgumentOutOfRangeException(nameof(value)); subtitleDelay = value; if (player != IntPtr.Zero && opened) Native.libvlc_video_set_spu_delay(player, (long)Math.Round(value * 1_000_000)); }
    }
    public int AudioTrack { get { VerifyAccess(); return player == IntPtr.Zero ? -1 : Native.libvlc_audio_get_track(player); } }
    public int SubtitleTrack { get { VerifyAccess(); return player == IntPtr.Zero ? -1 : Native.libvlc_video_get_spu(player); } }
    public IReadOnlyList<VlcTrack> AudioTracks => TrackList(false);
    public IReadOnlyList<VlcTrack> SubtitleTracks => TrackList(true);

    public void Load(string path)
    {
        Live(); string absolute = System.IO.Path.GetFullPath(path); if (!File.Exists(absolute)) throw new FileNotFoundException("This file has moved or is unavailable.");
        ++revision; ReleasePlayer(); source = absolute; pendingSubtitle = null; opened = failed = ended = false; playRequested = true; subtitleDelay = 0;
        if (window != IntPtr.Zero) StartPlayer();
    }
    public void Play() { Live(); playRequested = true; ended = false; if (player != IntPtr.Zero && Native.libvlc_media_player_play(player) < 0) RaiseFailure("libVLC could not start playback."); }
    public void Pause() { Live(); playRequested = false; if (player != IntPtr.Zero) Native.libvlc_media_player_set_pause(player, 1); }
    public void Stop() { Live(); playRequested = false; ended = true; if (player != IntPtr.Zero) Native.libvlc_media_player_stop(player); }
    public void Seek(double seconds)
    {
        Live(); if (!double.IsFinite(seconds)) return;
        if (player != IntPtr.Zero && Native.libvlc_media_player_is_seekable(player) != 0)
        { ended = false; Native.libvlc_media_player_set_time(player, (long)Math.Round(Math.Clamp(seconds, 0, Duration > 0 ? Duration : double.MaxValue) * 1000)); }
    }
    public void SelectAudio(int id) { Live(); if (player != IntPtr.Zero && Native.libvlc_audio_set_track(player, id) != 0) throw new InvalidOperationException("This audio track could not be selected."); }
    public void SelectSubtitle(int id) { Live(); pendingSubtitle = null; if (player != IntPtr.Zero && Native.libvlc_video_set_spu(player, id) != 0) throw new InvalidOperationException("This caption track could not be selected."); }
    public void AddSubtitles(string path)
    {
        Live(); path = System.IO.Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("This subtitle file is unavailable.");
        pendingSubtitle = path; if (opened) ApplySubtitle();
    }
    public void Snapshot(string output)
    {
        Live(); if (player == IntPtr.Zero || Native.libvlc_video_take_snapshot(player, 0, System.IO.Path.GetFullPath(output), 0, 0) != 0) throw new InvalidOperationException("The current video frame is unavailable.");
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        Live(); window = CreateWindowEx(0, "static", "", 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error()); if (source is not null) StartPlayer(); return new(this, window);
    }
    protected override void DestroyWindowCore(HandleRef handle) { VerifyAccess(); ++revision; ReleasePlayer(); window = IntPtr.Zero; DestroyWindow(handle.Handle); }
    protected override void Dispose(bool disposing)
    { if (disposing) { VerifyAccess(); if (Interlocked.Exchange(ref disposed, 1) == 0) { ++revision; metadataTimer.Stop(); ReleasePlayer(); } } base.Dispose(disposing); }
    protected override IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0203) { DoubleClicked?.Invoke(this, EventArgs.Empty); handled = true; return IntPtr.Zero; }
        return base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }
    void StartPlayer()
    {
        if (player != IntPtr.Zero || source is null || window == IntPtr.Zero || disposed != 0) return;
        try
        {
            VlcRuntime.Initialize();
            string[] options = ["--ignore-config", "--no-video-title-show", "--no-metadata-network-access", "--no-sub-autodetect-file", "--no-snapshot-preview", .. dummyAudioForTest ? new[] { "--aout=adummy" } : Array.Empty<string>()];
            IntPtr[] strings = options.Select(Marshal.StringToCoTaskMemUTF8).ToArray(); IntPtr argv = Marshal.AllocHGlobal(strings.Length * IntPtr.Size);
            try { for (int i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]); instance = Native.libvlc_new(strings.Length, argv); }
            finally { Marshal.FreeHGlobal(argv); foreach (IntPtr text in strings) Marshal.FreeCoTaskMem(text); }
            if (instance == IntPtr.Zero) throw new InvalidOperationException("libVLC initialization failed. Check that its plugins folder matches the installed runtime.");
            media = Native.libvlc_media_new_path(instance, source); if (media == IntPtr.Zero) throw new InvalidOperationException("libVLC could not open the selected local path.");
            player = Native.libvlc_media_player_new_from_media(media); if (player == IntPtr.Zero) throw new InvalidOperationException("libVLC could not create its media player.");
            Native.libvlc_media_player_set_hwnd(player, window); Native.libvlc_video_set_key_input(player, 0); Native.libvlc_video_set_mouse_input(player, 0);
            events = Native.libvlc_media_player_event_manager(player); int token = revision;
            notification = (pointer, _) =>
            {
                try
                {
                    int type = Marshal.ReadInt32(pointer);
                    Post(() =>
                    {
                        if (token != revision || disposed != 0 || player == IntPtr.Zero || failed) return;
                        if (type == 0x10a) { RaiseFailure("libVLC could not open or decode this media. Its installed modules may not support this file."); return; }
                        if (type == 0x109) { if (opened && !ended && Native.libvlc_media_player_get_state(player) == 6) { ended = true; Ended?.Invoke(this, EventArgs.Empty); } return; }
                        Refresh(); if (type is 0x114 or 0x115 or 0x116) TracksChanged?.Invoke(this, EventArgs.Empty);
                    });
                }
                catch (Exception) { /* A managed exception must never unwind through libVLC's native callback. */ }
            };
            foreach (int type in new[] { 0x104, 0x109, 0x10a, 0x111, 0x112, 0x114, 0x115, 0x116 })
            { if (Native.libvlc_event_attach(events, type, notification, IntPtr.Zero) != 0) throw new InvalidOperationException("libVLC event registration failed."); attached.Add(type); }
            Native.libvlc_audio_set_volume(player, (int)Math.Round(volume * 100)); metadataTimer.Start();
            if (playRequested && Native.libvlc_media_player_play(player) < 0) throw new InvalidOperationException("libVLC could not start playback.");
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException or IOException)
        { ReleasePlayer(); RaiseFailure(error.Message); }
    }
    void Refresh()
    {
        if (disposed != 0 || player == IntPtr.Zero || failed) return;
        int state = Native.libvlc_media_player_get_state(player);
        if (state == 7) { RaiseFailure("libVLC could not open or decode this media."); return; }
        if (!opened && state is 3 or 4)
        {
            opened = true; Native.libvlc_media_player_set_rate(player, (float)rate); Native.libvlc_audio_set_volume(player, (int)Math.Round(volume * 100));
            Native.libvlc_video_set_spu_delay(player, (long)Math.Round(subtitleDelay * 1_000_000)); ApplySubtitle();
            Opened?.Invoke(this, EventArgs.Empty); TracksChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    void ApplySubtitle()
    {
        if (player == IntPtr.Zero || pendingSubtitle is not string file) return;
        pendingSubtitle = null; if (Native.libvlc_media_player_add_slave(player, 0, new Uri(file).AbsoluteUri, true) != 0) Warning?.Invoke(this, new("libVLC could not load the selected subtitle file."));
    }
    void Size(out int width, out int height)
    { VerifyAccess(); width = height = 0; if (player != IntPtr.Zero && Native.libvlc_video_get_size(player, 0, out uint x, out uint y) == 0 && x <= int.MaxValue && y <= int.MaxValue) { width = (int)x; height = (int)y; } }
    Native.Statistics? Statistics()
    { VerifyAccess(); return media != IntPtr.Zero && Native.libvlc_media_get_stats(media, out var result) != 0 ? result : null; }
    IReadOnlyList<VlcTrack> TrackList(bool subtitles)
    {
        VerifyAccess(); if (player == IntPtr.Zero) return []; IntPtr list = subtitles ? Native.libvlc_video_get_spu_description(player) : Native.libvlc_audio_get_track_description(player);
        try
        {
            var output = new List<VlcTrack>(); var seen = new HashSet<IntPtr>();
            for (IntPtr current = list; current != IntPtr.Zero && output.Count < 256 && seen.Add(current);)
            { var item = Marshal.PtrToStructure<Native.TrackDescription>(current); output.Add(new(item.Id, Marshal.PtrToStringUTF8(item.Name) ?? "Track " + item.Id)); current = item.Next; }
            return output;
        }
        finally { if (list != IntPtr.Zero) Native.libvlc_track_description_list_release(list); }
    }
    void RaiseFailure(string message)
    { int token = revision; Post(() => { if (disposed != 0 || token != revision || failed) return; failed = true; metadataTimer.Stop(); Failed?.Invoke(this, new(message)); }); }
    void ReleasePlayer()
    {
        metadataTimer.Stop();
        if (events != IntPtr.Zero && notification is not null) foreach (int type in attached) Native.libvlc_event_detach(events, type, notification, IntPtr.Zero);
        attached.Clear(); events = IntPtr.Zero;
        if (player != IntPtr.Zero) { Native.libvlc_media_player_stop(player); Native.libvlc_media_player_release(player); player = IntPtr.Zero; }
        notification = null;
        if (media != IntPtr.Zero) { Native.libvlc_media_release(media); media = IntPtr.Zero; }
        if (instance != IntPtr.Zero) { Native.libvlc_release(instance); instance = IntPtr.Zero; }
    }
    void Post(Action action) { if (disposed != 0 || Dispatcher.HasShutdownStarted) return; try { Dispatcher.BeginInvoke(DispatcherPriority.Normal, action); } catch (InvalidOperationException) { } }
    void Live() { VerifyAccess(); ObjectDisposedException.ThrowIf(disposed != 0, this); }

    internal static class Native
    {
        const string Library = "Luma.LibVLC3";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void EventCallback(IntPtr notification, IntPtr user);
        [StructLayout(LayoutKind.Sequential)] internal struct TrackDescription { public int Id; public IntPtr Name, Next; }
        [StructLayout(LayoutKind.Sequential)] internal struct Statistics
        { public int ReadBytes; public float InputBitrate; public int DemuxReadBytes; public float DemuxBitrate; public int DemuxCorrupted, DemuxDiscontinuity, DecodedVideo, DecodedAudio, DisplayedPictures, LostPictures, PlayedAudioBuffers, LostAudioBuffers, SentPackets, SentBytes; public float SendBitrate; }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_new(int count, IntPtr arguments);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_release(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_get_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_media_new_path(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_release(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_media_player_new_from_media(IntPtr media);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_release(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_hwnd(IntPtr value, IntPtr hwnd);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_play(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_stop(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_pause(IntPtr value, int pause);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_is_playing(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_is_seekable(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_get_state(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long libvlc_media_player_get_time(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_media_player_set_time(IntPtr value, long milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long libvlc_media_player_get_length(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_set_rate(IntPtr value, float rate);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern float libvlc_media_player_get_rate(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_audio_set_volume(IntPtr value, int volume);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_audio_get_track(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_audio_set_track(IntPtr value, int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_audio_get_track_description(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_video_get_spu_description(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_video_get_spu(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_video_set_spu(IntPtr value, int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_video_set_spu_delay(IntPtr value, long microseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_video_set_key_input(IntPtr value, uint enabled);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_video_set_mouse_input(IntPtr value, uint enabled);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_video_get_size(IntPtr value, uint number, out uint width, out uint height);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_video_take_snapshot(IntPtr value, uint number, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint width, uint height);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_player_add_slave(IntPtr value, int type, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri, [MarshalAs(UnmanagedType.I1)] bool selected);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_track_description_list_release(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_media_get_stats(IntPtr value, out Statistics statistics);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libvlc_media_player_event_manager(IntPtr value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int libvlc_event_attach(IntPtr value, int type, EventCallback callback, IntPtr user);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void libvlc_event_detach(IntPtr value, int type, EventCallback callback, IntPtr user);
    }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DestroyWindow(IntPtr hwnd);
}

internal static class VlcRuntime
{
    static readonly object gate = new();
    static IntPtr library, core, directoryCookie;
    public static string Path { get; private set; } = "";
    public static string Version { get; private set; } = "";
    static VlcRuntime() => NativeLibrary.SetDllImportResolver(typeof(VlcRuntime).Assembly, Resolve);
    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? search) => name == "Luma.LibVLC3" ? library : IntPtr.Zero;
    public static void Initialize()
    {
        lock (gate)
        {
            if (library != IntPtr.Zero) return;
            if (!OperatingSystem.IsWindows()) throw new DllNotFoundException("The Windows player requires a Windows libVLC3 runtime.");
            string? requested = Environment.GetEnvironmentVariable("LUMA_LIBVLC_PATH");
            if (!string.IsNullOrWhiteSpace(requested) && (!System.IO.Path.IsPathFullyQualified(requested) || !File.Exists(requested))) throw new DllNotFoundException("LUMA_LIBVLC_PATH must name an existing absolute path to libvlc.dll.");
            var candidates = new List<string>(); if (requested is not null) candidates.Add(requested);
            string bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "libvlc", "libvlc.dll");
            if (File.Exists(bundled) && ApprovedBundle(System.IO.Path.GetDirectoryName(bundled)!)) candidates.Add(bundled);
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
                if (!string.IsNullOrEmpty(root)) candidates.Add(System.IO.Path.Combine(root, "VideoLAN", "VLC", "libvlc.dll"));
            string? selected = candidates.FirstOrDefault(File.Exists);
            if (selected is null) throw new DllNotFoundException("Install the official 64-bit VLC 3 application separately, or set LUMA_LIBVLC_PATH to its libvlc.dll. This review build does not bundle an unapproved VLC runtime.");
            string folder = System.IO.Path.GetDirectoryName(selected)!, plugins = System.IO.Path.Combine(folder, "plugins");
            if (!Directory.Exists(plugins) || !File.Exists(System.IO.Path.Combine(folder, "libvlccore.dll"))) throw new DllNotFoundException("libvlc.dll requires its matching libvlccore.dll and plugins folder.");
            if (!SetDefaultDllDirectories(0x1000)) throw new Win32Exception(Marshal.GetLastWin32Error());
            directoryCookie = AddDllDirectory(folder); if (directoryCookie == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                core = NativeLibrary.Load(System.IO.Path.Combine(folder, "libvlccore.dll")); library = NativeLibrary.Load(selected);
                Version = Marshal.PtrToStringUTF8(Marshal.GetDelegateForFunctionPointer<VersionCall>(NativeLibrary.GetExport(library, "libvlc_get_version"))()) ?? "";
                if (!Version.StartsWith("3.", StringComparison.Ordinal)) throw new DllNotFoundException("This adapter requires the stable libVLC 3 C ABI; the selected runtime has another major version.");
                Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", plugins); Path = selected;
            }
            catch { if (library != IntPtr.Zero) NativeLibrary.Free(library); if (core != IntPtr.Zero) NativeLibrary.Free(core); library = core = IntPtr.Zero; RemoveDllDirectory(directoryCookie); directoryCookie = IntPtr.Zero; throw; }
        }
    }
    static bool ApprovedBundle(string folder)
    {
        try { using var json = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(folder, "redistribution-audit.json"))); return json.RootElement.GetProperty("approvedForRedistribution").GetBoolean(); }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr VersionCall();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] static extern IntPtr AddDllDirectory(string directory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RemoveDllDirectory(IntPtr cookie);
}
