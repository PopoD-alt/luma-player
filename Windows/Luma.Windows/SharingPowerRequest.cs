using System.Runtime.InteropServices;

namespace Luma.WindowsApp;

/// <summary>Requests only SystemRequired while media bodies are being streamed; no display/away-mode request.</summary>
internal sealed class SharingPowerRequest : IDisposable
{
    readonly IPowerApi api;
    IntPtr handle;
    bool disposed;
    public bool IsActive { get; private set; }
    public bool IsUnavailable { get; private set; }
    public bool? LastReleaseSucceeded { get; private set; }
    public SharingPowerRequest() : this(new NativePowerApi()) { }
    internal SharingPowerRequest(IPowerApi powerApi) => api = powerApi;
    public void Update(bool mediaStreaming)
    {
        if (disposed) return;
        if (!mediaStreaming) { Release(); IsUnavailable = false; return; }
        if (IsActive || IsUnavailable) return;
        try
        {
            LastReleaseSucceeded = null;
            handle = api.Create("Luma Player is streaming your local playlist.");
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) { handle = IntPtr.Zero; IsUnavailable = true; return; }
            if (api.SetSystemRequired(handle)) IsActive = true;
            else { IsUnavailable = true; Release(); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        { IsUnavailable = true; Release(); }
    }
    void Release()
    {
        IntPtr previous = handle; handle = IntPtr.Zero; bool clear = IsActive; IsActive = false;
        if (previous == IntPtr.Zero) return;
        try { if (clear) _ = api.ClearSystemRequired(previous); }
        finally { LastReleaseSucceeded = api.Close(previous); }
    }
    public void Dispose() { if (disposed) return; disposed = true; Release(); }
    internal interface IPowerApi
    { IntPtr Create(string reason); bool SetSystemRequired(IntPtr request); bool ClearSystemRequired(IntPtr request); bool Close(IntPtr request); }
    [StructLayout(LayoutKind.Sequential)] internal struct DetailedReason
    { public IntPtr LocalizedReasonModule; public uint LocalizedReasonId, ReasonStringCount; public IntPtr ReasonStrings; }
    [StructLayout(LayoutKind.Explicit)] internal struct ReasonUnion
    { [FieldOffset(0)] public IntPtr SimpleReasonString; [FieldOffset(0)] public DetailedReason Detailed; }
    [StructLayout(LayoutKind.Sequential)] internal struct ReasonContext
    { public uint Version, Flags; public ReasonUnion Reason; }
    sealed class NativePowerApi : IPowerApi
    {
        public IntPtr Create(string reason)
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
            IntPtr text = Marshal.StringToHGlobalUni(reason);
            try { var context = new ReasonContext { Version = 0, Flags = 1, Reason = new ReasonUnion { SimpleReasonString = text } }; return PowerCreateRequest(ref context); }
            finally { Marshal.FreeHGlobal(text); }
        }
        public bool SetSystemRequired(IntPtr request) => PowerSetRequest(request, 1);
        public bool ClearSystemRequired(IntPtr request) => PowerClearRequest(request, 1);
        public bool Close(IntPtr request) => CloseHandle(request);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] static extern IntPtr PowerCreateRequest(ref ReasonContext context);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PowerSetRequest(IntPtr request, int type);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PowerClearRequest(IntPtr request, int type);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
    }
}
