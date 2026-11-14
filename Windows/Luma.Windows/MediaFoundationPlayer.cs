using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Luma.WindowsApp;

internal sealed class MediaFoundationFailureEventArgs(string message, int hResult, uint mediaEngineErrorCode) : EventArgs
{
    public string Message { get; } = message;
    public int HResult { get; } = hResult;
    public uint NativeHResult => unchecked((uint)HResult);
    public uint MediaEngineErrorCode { get; } = mediaEngineErrorCode;
}

/// <summary>
/// Microsoft Media Foundation MediaEngine in HWND rendering mode. The native
/// engine owns decoding, audio output (including channel negotiation), rendering
/// and the shared A/V clock. HEVC needs an installed compatible Windows decoder.
/// All public operations and disposal belong to the owning WPF dispatcher.
/// HwndHost has Win32 airspace: WPF captions need an independent overlay HWND.
/// ABI declarations below are checked against Microsoft's public Windows SDK:
/// https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/mfmediaengine.h
/// https://learn.microsoft.com/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengineclassfactory-createinstance
/// </summary>
internal sealed class MediaFoundationPlayer : HwndHost
{
    static readonly Guid FactoryClass = new("b44392da-499b-446b-a4cb-005fead0e6d5");
    static readonly Guid FactoryInterface = new("4d645ace-26aa-4688-9be1-df3516990b93");
    static readonly Guid EngineExInterface = new("83015ead-b1e6-40d0-a98a-37145ffe1ad1");
    static readonly Guid CallbackAttribute = new("c60381b8-83a4-41f8-a3d0-de05076849a9");
    static readonly Guid PlaybackWindowAttribute = new("d988879b-67c9-4d92-baa7-6eadd446039d");
    static readonly Guid AudioCategoryAttribute = new("c8d4c51d-350e-41f2-ba46-faebbb0857f6");
    static readonly Guid SynchronousCloseAttribute = new("c3c2e12f-7e0e-4e43-b91c-dc992ccdfa5e");

    IntPtr child, engine, engineEx;
    LumaMediaEngineNotify? callback;
    string? pendingSource;
    bool platformStarted, opened, failed, desiredPlaying, stopped, endedRaised;
    int disposed, nativeRevision;
    double volume = .7, rate = 1, knownDuration;
    double? pendingSeek;
    int naturalWidth, naturalHeight;
    uint aspectWidth, aspectHeight;

    public FrameworkElement View => this;
    public event EventHandler? Opened;
    public event EventHandler<MediaFoundationFailureEventArgs>? Failed;
    public event EventHandler? Ended;

    public MediaFoundationPlayer()
    {
        Focusable = false;
        SizeChanged += (_, _) => UpdateVideoRectangle();
    }

    public double CurrentTime
    {
        get { VerifyAccess(); return engine == IntPtr.Zero || failed ? 0 : FiniteNonnegative(Function<GetDouble>(engine, Slots.GetCurrentTime)(engine)); }
    }
    public double Duration
    {
        get
        {
            VerifyAccess();
            if (engine != IntPtr.Zero && !failed)
            {
                double value = Function<GetDouble>(engine, Slots.GetDuration)(engine);
                if (double.IsFinite(value) && value >= 0) knownDuration = value;
            }
            return knownDuration;
        }
    }
    public int NaturalVideoWidth { get { VerifyAccess(); return naturalWidth; } }
    public int NaturalVideoHeight { get { VerifyAccess(); return naturalHeight; } }
    public bool IsPaused { get { VerifyAccess(); return engine == IntPtr.Zero || failed || stopped || Function<GetBoolean>(engine, Slots.IsPaused)(engine) != 0; } }
    /// <summary>Native MF rendered-frame statistic; null means unavailable, never fabricated from the clock.</summary>
    public uint? RenderedFrames
    {
        get
        {
            VerifyAccess(); if (engineEx == IntPtr.Zero || failed) return null;
            // PROPVARIANT is 24 bytes on x64 and 16 on x86. The SDK specifies
            // FRAMES_RENDERED=0 as ULONG/VT_UI4, with its union at byte offset 8.
            int size = IntPtr.Size == 8 ? 24 : 16; IntPtr value = Marshal.AllocCoTaskMem(size);
            try
            {
                Marshal.Copy(new byte[size], 0, value, size);
                int result = Function<GetStatisticCall>(engineEx, Slots.GetStatistics)(engineEx, 0, value);
                if (result < 0 || Marshal.ReadInt16(value) != 19) return null; // VT_UI4
                return unchecked((uint)Marshal.ReadInt32(value, 8));
            }
            finally { PropVariantClear(value); Marshal.FreeCoTaskMem(value); }
        }
    }
    public double Volume
    {
        get { VerifyAccess(); return volume; }
        set
        {
            VerifyLive(); if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            volume = Math.Clamp(value, 0, 1);
            if (engine != IntPtr.Zero) NativeCall(() => Function<SetDouble>(engine, Slots.SetVolume)(engine, volume), "set volume");
        }
    }
    public double Rate
    {
        get { VerifyAccess(); return rate; }
        set
        {
            VerifyLive(); if (!double.IsFinite(value) || value <= 0 || value > 16) throw new ArgumentOutOfRangeException(nameof(value));
            rate = value;
            if (engine != IntPtr.Zero && opened) NativeCall(() => Function<SetDouble>(engine, Slots.SetPlaybackRate)(engine, rate), "set playback rate");
        }
    }

    public void Load(string localPath)
    {
        VerifyLive(); ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        string path = Path.GetFullPath(localPath);
        if (!File.Exists(path)) throw new FileNotFoundException("This media file is unavailable.");
        ++nativeRevision; ReleaseEngine();
        pendingSource = new Uri(path).AbsoluteUri;
        opened = failed = desiredPlaying = stopped = endedRaised = false;
        knownDuration = 0; naturalWidth = naturalHeight = 0; aspectWidth = aspectHeight = 0; pendingSeek = null;
        // WPF can receive Load before its layout pass creates the Win32 child.
        if (child != IntPtr.Zero) StartEngine();
    }
    public void Play()
    {
        VerifyLive(); desiredPlaying = true; stopped = false;
        if (engine != IntPtr.Zero && !failed) NativeCall(() => Function<HResultCall>(engine, Slots.Play)(engine), "play media");
    }
    public void Pause()
    {
        VerifyLive(); desiredPlaying = false;
        if (engine != IntPtr.Zero && !failed) NativeCall(() => Function<HResultCall>(engine, Slots.Pause)(engine), "pause media");
    }
    public void Stop()
    {
        VerifyLive(); desiredPlaying = false; stopped = true; endedRaised = false;
        if (engine == IntPtr.Zero || failed) { pendingSeek = 0; return; }
        NativeCall(() => Function<HResultCall>(engine, Slots.Pause)(engine), "stop media");
        if (opened) NativeCall(() => Function<SetDouble>(engine, Slots.SetCurrentTime)(engine, 0), "rewind media");
        else pendingSeek = 0;
    }
    public void Seek(double seconds)
    {
        VerifyLive(); if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        double target = Math.Max(0, seconds); if (knownDuration > 0) target = Math.Min(target, knownDuration);
        endedRaised = false;
        if (engine == IntPtr.Zero || !opened) { pendingSeek = target; return; }
        NativeCall(() => Function<SetDouble>(engine, Slots.SetCurrentTime)(engine, target), "seek media");
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        VerifyLive();
        const uint childStyle = 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000; // child, visible, clip children/siblings
        child = CreateWindowEx(0, "static", "", childStyle, 0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (child == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the native media window.");
        if (pendingSource is not null) StartEngine();
        return new HandleRef(this, child);
    }
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        VerifyAccess(); ++nativeRevision; ReleaseEngine(); child = IntPtr.Zero;
        DestroyWindow(hwnd.Handle);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            VerifyAccess();
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                ++nativeRevision; pendingSource = null; desiredPlaying = false; ReleaseEngine();
            }
        }
        base.Dispose(disposing);
    }
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0005) UpdateVideoRectangle(); // WM_SIZE
        if (msg == 0x0014) { handled = true; return new IntPtr(1); } // WM_ERASEBKGND; paint owns the black background
        if (msg == 0x000f)
        {
            IntPtr dc = BeginPaint(hwnd, out NativePaint paint);
            try
            {
                if (engineEx == IntPtr.Zero || !opened)
                { if (GetClientRect(hwnd, out NativeRect rectangle)) FillRect(dc, ref rectangle, GetStockObject(4)); }
                else Function<RepaintCall>(engineEx, Slots.UpdateVideoStream)(engineEx, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            finally { EndPaint(hwnd, ref paint); }
            handled = true; return IntPtr.Zero;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    void StartEngine()
    {
        VerifyAccess(); if (Volatile.Read(ref disposed) != 0 || engine != IntPtr.Zero || child == IntPtr.Zero || pendingSource is null) return;
        int revision = nativeRevision;
        IntPtr attributes = IntPtr.Zero, factory = IntPtr.Zero, callbackPointer = IntPtr.Zero;
        try
        {
            Check(MFStartup(0x00020070, 0), "start Media Foundation"); platformStarted = true;
            Check(MFCreateAttributes(out attributes, 4), "create media attributes");
            callback = new LumaMediaEngineNotify(this, revision);
            callbackPointer = Marshal.GetComInterfaceForObject(callback, typeof(ILumaMediaEngineNotify));
            Guid key = CallbackAttribute;
            Check(Function<SetUnknownAttribute>(attributes, 27)(attributes, ref key, callbackPointer), "attach media callback");
            key = PlaybackWindowAttribute;
            Check(Function<SetUInt64Attribute>(attributes, 22)(attributes, ref key, unchecked((ulong)child.ToInt64())), "set video window");
            key = AudioCategoryAttribute;
            Check(Function<SetUInt32Attribute>(attributes, 21)(attributes, ref key, 11), "set media audio category"); // AudioCategory_Media
            key = SynchronousCloseAttribute;
            Check(Function<SetUInt32Attribute>(attributes, 21)(attributes, ref key, 1), "request synchronous media close");
            Guid clsid = FactoryClass, iid = FactoryInterface;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out factory), "create native MediaEngine factory");
            Check(Function<CreateEngine>(factory, 3)(factory, 0, attributes, out engine), "create native MediaEngine renderer");
            iid = EngineExInterface;
            Check(Marshal.QueryInterface(engine, in iid, out engineEx), "query native video renderer");
            Check(Function<SetBoolean>(engine, Slots.SetAutoPlay)(engine, 0), "disable automatic playback");
            Check(Function<SetUInt32>(engine, Slots.SetPreload)(engine, 4), "set media preload");
            Check(Function<SetDouble>(engine, Slots.SetVolume)(engine, volume), "set media volume");
            IntPtr source = Marshal.StringToBSTR(pendingSource);
            try { Check(Function<SetSourceCall>(engine, Slots.SetSource)(engine, source), "set media source"); }
            finally { Marshal.FreeBSTR(source); }
            Check(Function<HResultCall>(engine, Slots.Load)(engine), "load media source");
            UpdateVideoRectangle();
            if (desiredPlaying) Check(Function<HResultCall>(engine, Slots.Play)(engine), "start native playback");
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            Release(ref callbackPointer); Release(ref factory); Release(ref attributes);
            ReleaseEngine(); QueueFailure(revision, error.Message, error.HResult, 0);
        }
        finally
        {
            Release(ref callbackPointer); Release(ref factory); Release(ref attributes);
        }
    }

    internal void QueueNativeEvent(int revision, uint code, nuint parameter1, uint parameter2)
    {
        // Never wait on the dispatcher from an MF callback: Shutdown may itself wait
        // for callbacks to return. The immutable token belongs to this engine only.
        if (Volatile.Read(ref disposed) != 0 || Dispatcher.HasShutdownStarted) return;
        if (code is not (5 or 10 or 11 or 14 or 19 or 1000 or 1009 or 1014)) return;
        Post(() =>
        {
            if (revision != nativeRevision || Volatile.Read(ref disposed) != 0 || failed || engine == IntPtr.Zero) return;
            if (code is 5 or 1014)
            {
                // Only ERROR defines param1/param2. STREAMRENDERINGERROR does
                // not: query the engine's error object instead of trusting them.
                int hr = code == 5 ? unchecked((int)parameter2) : LastEngineError();
                uint category = code == 5 ? unchecked((uint)parameter1) : 0;
                if (hr >= 0) hr = LastEngineError();
                FailNow(revision, code == 1014 ? "A native audio or video stream could not be rendered." : "Windows Media Foundation could not play this file.", hr, category);
                return;
            }
            if (code == 19)
            {
                if (!opened || stopped || endedRaised || Function<GetBoolean>(engine, Slots.IsEnded)(engine) == 0) return;
                endedRaised = true; desiredPlaying = false; Ended?.Invoke(this, EventArgs.Empty); return;
            }
            RefreshMetadata(revision);
        });
    }
    void RefreshMetadata(int revision)
    {
        if (engine == IntPtr.Zero || failed) return;
        double value = Function<GetDouble>(engine, Slots.GetDuration)(engine);
        if (double.IsFinite(value) && value >= 0) knownDuration = value;
        int result = Function<GetSize>(engine, Slots.GetNativeVideoSize)(engine, out uint width, out uint height);
        if (result >= 0 && width <= int.MaxValue && height <= int.MaxValue) { naturalWidth = (int)width; naturalHeight = (int)height; }
        if (Function<GetSize>(engine, Slots.GetVideoAspectRatio)(engine, out uint numerator, out uint denominator) >= 0 && numerator > 0 && denominator > 0)
        { aspectWidth = numerator; aspectHeight = denominator; }
        UpdateVideoRectangle();
        if (opened || Function<GetBoolean>(engine, Slots.HasVideo)(engine) != 0 && (naturalWidth <= 0 || naturalHeight <= 0)) return;
        // HAVE_METADATA is sufficient; do not wait indefinitely for CANPLAY.
        if (Function<GetState>(engine, Slots.GetReadyState)(engine) < 1) return;
        opened = true;
        if (!NativeCall(() => Function<SetDouble>(engine, Slots.SetPlaybackRate)(engine, rate), "set playback rate")) return;
        if (pendingSeek is double target)
        {
            pendingSeek = null; if (knownDuration > 0) target = Math.Min(target, knownDuration);
            if (!NativeCall(() => Function<SetDouble>(engine, Slots.SetCurrentTime)(engine, target), "restore playback position")) return;
        }
        if (revision == nativeRevision && !failed) Opened?.Invoke(this, EventArgs.Empty);
    }
    void UpdateVideoRectangle()
    {
        if (engineEx == IntPtr.Zero || child == IntPtr.Zero || Volatile.Read(ref disposed) != 0 || !GetClientRect(child, out NativeRect bounds)) return;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top; if (width <= 0 || height <= 0) return;
        NativeRect destination = bounds;
        if (naturalWidth > 0 && naturalHeight > 0)
        {
            double pictureWidth = aspectWidth > 0 ? aspectWidth : naturalWidth, pictureHeight = aspectHeight > 0 ? aspectHeight : naturalHeight;
            double scale = Math.Min(width / pictureWidth, height / pictureHeight);
            int fittedWidth = Math.Max(1, (int)Math.Round(pictureWidth * scale)), fittedHeight = Math.Max(1, (int)Math.Round(pictureHeight * scale));
            destination.Left = (width - fittedWidth) / 2; destination.Top = (height - fittedHeight) / 2;
            destination.Right = destination.Left + fittedWidth; destination.Bottom = destination.Top + fittedHeight;
        }
        NativeColor black = new() { Alpha = 255 };
        int result = Function<UpdateVideoCall>(engineEx, Slots.UpdateVideoStream)(engineEx, IntPtr.Zero, ref destination, ref black);
        if (result < 0 && opened && !failed) QueueFailure(nativeRevision, "The native video renderer could not update its window.", result, 0);
    }
    bool NativeCall(Func<int> operation, string action)
    {
        if (failed || Volatile.Read(ref disposed) != 0) return false;
        int result = operation(); if (result >= 0) return true;
        QueueFailure(nativeRevision, "Windows Media Foundation could not " + action + ".", result, 0); return false;
    }
    void QueueFailure(int revision, string message, int result, uint category)
        => Post(() => FailNow(revision, message, result, category));
    void FailNow(int revision, string message, int result, uint category)
    {
        if (revision != nativeRevision || Volatile.Read(ref disposed) != 0 || failed) return;
        failed = true; desiredPlaying = false; stopped = true;
        if (engine != IntPtr.Zero) Function<HResultCall>(engine, Slots.Pause)(engine);
        if (result >= 0) result = unchecked((int)0x80004005);
        string diagnostic = $"{message} (0x{unchecked((uint)result):X8}" + (category == 0 ? ")." : $", MediaEngine error {category}).");
        Failed?.Invoke(this, new(diagnostic, result, category));
    }
    int LastEngineError()
    {
        IntPtr error = IntPtr.Zero;
        try
        {
            if (Function<GetObject>(engine, Slots.GetError)(engine, out error) >= 0 && error != IntPtr.Zero)
            { int result = Function<HResultCall>(error, 4)(error); if (result < 0) return result; }
            return unchecked((int)0x80004005);
        }
        finally { Release(ref error); }
    }
    void Post(Action action)
    {
        if (Volatile.Read(ref disposed) != 0 || Dispatcher.HasShutdownStarted) return;
        try { Dispatcher.BeginInvoke(DispatcherPriority.Normal, action); }
        catch (InvalidOperationException) { /* Dispatcher shutdown already owns teardown. */ }
    }
    void ReleaseEngine()
    {
        callback?.Detach();
        try { if (engine != IntPtr.Zero) Function<HResultCall>(engine, Slots.Shutdown)(engine); }
        finally
        {
            Release(ref engineEx); Release(ref engine); callback = null;
            if (platformStarted) { platformStarted = false; MFShutdown(); }
        }
    }
    void VerifyLive() { VerifyAccess(); ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); }
    static double FiniteNonnegative(double value) => double.IsFinite(value) && value >= 0 ? value : 0;
    static void Check(int result, string operation) { if (result < 0) throw new COMException("Could not " + operation + ".", result); }
    static void Release(ref IntPtr pointer) { IntPtr owned = pointer; pointer = IntPtr.Zero; if (owned != IntPtr.Zero) Marshal.Release(owned); }
    static T Function<T>(IntPtr instance, int index) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));

    // Absolute COM vtable slots, including IUnknown's three entries. The non-HRESULT
    // return types (double/BOOL/USHORT) must stay untransformed on this ABI.
    static class Slots
    {
        public const int GetError = 3, SetSource = 6, SetPreload = 10, Load = 12, GetReadyState = 14,
            GetCurrentTime = 16, SetCurrentTime = 17, GetDuration = 19, IsPaused = 20,
            SetPlaybackRate = 24, IsEnded = 27, SetAutoPlay = 29, Play = 32, Pause = 33,
            SetVolume = 37, HasVideo = 38, GetNativeVideoSize = 40, GetVideoAspectRatio = 41, Shutdown = 42, GetStatistics = 46, UpdateVideoStream = 47;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int HResultCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int GetBoolean(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate ushort GetState(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate double GetDouble(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int GetObject(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int GetSize(IntPtr self, out uint width, out uint height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int SetDouble(IntPtr self, double value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int SetBoolean(IntPtr self, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int SetUInt32(IntPtr self, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int SetSourceCall(IntPtr self, IntPtr bstr);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int SetUnknownAttribute(IntPtr self, ref Guid key, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int SetUInt32Attribute(IntPtr self, ref Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int SetUInt64Attribute(IntPtr self, ref Guid key, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int CreateEngine(IntPtr self, uint flags, IntPtr attributes, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int GetStatisticCall(IntPtr self, uint statistic, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int UpdateVideoCall(IntPtr self, IntPtr source, ref NativeRect destination, ref NativeColor border);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int RepaintCall(IntPtr self, IntPtr source, IntPtr destination, IntPtr border);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativeColor { public byte Blue, Green, Red, Alpha; }
    [StructLayout(LayoutKind.Sequential)] struct NativePaint
    {
        public IntPtr Dc; public int Erase; public NativeRect Rectangle; public int Restore, Incremental;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);
    [DllImport("ole32.dll", ExactSpelling = true)] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr value);
    [DllImport("ole32.dll", ExactSpelling = true)] static extern int PropVariantClear(IntPtr value);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetClientRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, out NativePaint paint);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EndPaint(IntPtr hwnd, ref NativePaint paint);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref NativeRect rectangle, IntPtr brush);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int value);
}

// Public COM-visible types are required for the managed callback's QueryInterface.
// This is an original adapter for the SDK ABI, not a decoder implementation.
[ComVisible(true), Guid("fee7c112-e776-42b5-9bbf-0048524e2bd5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ILumaMediaEngineNotify
{
    [PreserveSig] int EventNotify(uint eventCode, nuint parameter1, uint parameter2);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class LumaMediaEngineNotify : ILumaMediaEngineNotify
{
    WeakReference<MediaFoundationPlayer>? owner;
    readonly int revision;
    internal LumaMediaEngineNotify(MediaFoundationPlayer player, int token) { owner = new(player); revision = token; }
    internal void Detach() => Interlocked.Exchange(ref owner, null);
    public int EventNotify(uint eventCode, nuint parameter1, uint parameter2)
    {
        try
        {
            // Not used by our creation flags; acknowledge synchronously if emitted.
            if (eventCode == 1008 && parameter1 != 0) SetEvent(unchecked((IntPtr)parameter1));
            var reference = Volatile.Read(ref owner);
            if (reference is not null && reference.TryGetTarget(out var player)) player.QueueNativeEvent(revision, eventCode, parameter1, parameter2);
            return 0;
        }
        catch { return unchecked((int)0x80004005); } // Never unwind managed exceptions through an MF worker.
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetEvent(IntPtr handle);
}
