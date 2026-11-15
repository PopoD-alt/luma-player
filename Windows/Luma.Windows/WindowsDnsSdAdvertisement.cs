using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Luma.Core.Sharing;

namespace Luma.WindowsApp;

/// <summary>Windows DNS-SD registration. Native calls and cleanup are serialized off the UI thread.</summary>
internal sealed class WindowsDnsSdAdvertisement : IDisposable
{
    const uint Pending = 9506;
    static readonly ConcurrentDictionary<nint, WindowsDnsSdAdvertisement> owners = new();
    static long nextContext;
    static readonly Completion callback = Complete;
    readonly ConcurrentQueue<Action> work = new();
    readonly IDnsServiceApi api;
    readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    IntPtr request, cancellation, instance, createdInstance, context;
    int draining, stopping;
    bool registering, registered, deregistering, cancelRequested, cleaned;
    string status = "Manual URL entry is available";
    public string Status => Volatile.Read(ref status);
    public Task CompletionTask => completed.Task;

    public WindowsDnsSdAdvertisement(PlaylistSharingServer server) : this(server, new NativeApi(), OperatingSystem.IsWindows()) { }
    internal WindowsDnsSdAdvertisement(PlaylistSharingServer server, IDnsServiceApi nativeApi) : this(server, nativeApi, true) { }
    WindowsDnsSdAdvertisement(PlaylistSharingServer server, IDnsServiceApi nativeApi, bool enabled)
    {
        api = nativeApi;
        if (!enabled || owners.Count >= 32) { completed.TrySetResult(); return; }
        var address = PlaylistSharingServer.LocalAddresses().FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (address is null) { status = "Discovery unavailable; use a displayed URL"; completed.TrySetResult(); return; }
        try
        {
            string host = new(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(48).ToArray()); if (host.Length == 0) host = "luma";
            instance = createdInstance = api.Construct("Luma-" + server.Id[..12] + "._luma-player._tcp.local", host + ".local", address.GetAddressBytes(), (ushort)server.Port, server.Id);
            if (instance == IntPtr.Zero) { status = "Discovery unavailable; use a displayed URL"; completed.TrySetResult(); return; }
            context = (nint)Interlocked.Increment(ref nextContext); owners[context] = this;
            cancellation = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(cancellation, IntPtr.Zero);
            request = Marshal.AllocHGlobal(Marshal.SizeOf<Registration>());
            Marshal.StructureToPtr(new Registration { Version = 1, Instance = instance, Callback = Marshal.GetFunctionPointerForDelegate(callback), Context = context }, request, false);
            status = "Starting Bonjour discovery…";
            Queue(() =>
            {
                if (Volatile.Read(ref stopping) != 0) { Cleanup(); return; }
                registering = true;
                uint result = api.Register(request, cancellation);
                if (result != Pending) { registering = false; status = "Discovery unavailable; use a displayed URL"; Cleanup(); }
            });
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        { status = "Discovery unavailable; use a displayed URL"; Cleanup(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        status = completed.Task.IsCompleted ? "Sharing discovery stopped" : "Stopping Bonjour discovery…";
        Queue(StopNative);
    }
    void StopNative()
    {
        if (cleaned) return;
        if (registering)
        {
            if (!cancelRequested)
            {
                cancelRequested = true;
                // This call may wait for Complete. Complete only queues work and never waits on this worker.
                uint result = api.Cancel(cancellation);
                if (result is not (0 or 1223)) status = "Discovery cancellation pending; media sharing is stopped";
                // Keep native memory alive until the registration callback. Do not guess that cancellation
                // suppresses all callbacks; a nonresponsive DNS service is bounded by the owners limit.
            }
        }
        else if (registered && !deregistering)
        {
            deregistering = true;
            if (api.DeRegister(request) != Pending)
            { deregistering = registered = false; status = "Discovery withdrawal unavailable; media sharing is stopped"; Cleanup(); }
        }
        else if (!registered && !deregistering) Cleanup();
    }
    static void Complete(uint result, IntPtr context, IntPtr returned)
    {
        try
        {
            if (owners.TryGetValue(context, out var owner)) owner.Queue(() => owner.OnComplete(result, returned));
        }
        catch (Exception) { /* Never unwind a managed exception through the DNS callback. */ }
    }
    void OnComplete(uint result, IntPtr returned)
    {
        // All calls and this callback processing share one serial worker. A synchronous callback can
        // therefore never free buffers still in use by Register, Cancel or DeRegister.
        try
        {
            if (cleaned) return;
            if (deregistering)
            {
                deregistering = registered = false;
                status = result == 0 ? "Sharing discovery stopped" : "Discovery withdrawal unavailable; media sharing is stopped";
                CleanupAfterReturned(returned); returned = IntPtr.Zero;
            }
            else if (registering)
            {
                registering = false;
                if (result == 0) { registered = true; status = Volatile.Read(ref stopping) == 0 ? "Bonjour discovery active" : "Stopping Bonjour discovery…"; if (Volatile.Read(ref stopping) != 0) StopNative(); }
                else { status = Volatile.Read(ref stopping) == 0 ? "Discovery unavailable; use a displayed URL" : "Sharing discovery stopped"; CleanupAfterReturned(returned); returned = IntPtr.Zero; }
            }
        }
        finally { if (returned != IntPtr.Zero && returned != createdInstance) api.Free(returned); }
    }
    void CleanupAfterReturned(IntPtr returned)
    { if (returned != IntPtr.Zero && returned != instance) api.Free(returned); Cleanup(); }
    void Cleanup()
    {
        if (cleaned) return; cleaned = true;
        if (request != IntPtr.Zero) { Marshal.FreeHGlobal(request); request = IntPtr.Zero; }
        if (cancellation != IntPtr.Zero) { Marshal.FreeHGlobal(cancellation); cancellation = IntPtr.Zero; }
        if (instance != IntPtr.Zero) { api.Free(instance); instance = IntPtr.Zero; }
        if (context != IntPtr.Zero) owners.TryRemove(context, out _);
        if (Volatile.Read(ref stopping) != 0 && status == "Stopping Bonjour discovery…") status = "Sharing discovery stopped";
        completed.TrySetResult();
    }
    void Queue(Action action)
    { work.Enqueue(action); if (Interlocked.CompareExchange(ref draining, 1, 0) == 0) _ = Task.Run(Drain); }
    void Drain()
    {
        while (true)
        {
            while (work.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
                { status = "Discovery unavailable; use a displayed URL"; registering = registered = deregistering = false; Cleanup(); }
            }
            Interlocked.Exchange(ref draining, 0);
            if (work.IsEmpty || Interlocked.CompareExchange(ref draining, 1, 0) != 0) return;
        }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Registration
    { public uint Version, InterfaceIndex; public IntPtr Instance, Callback, Context, Credentials; public int UnicastEnabled; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Completion(uint status, IntPtr context, IntPtr instance);
    internal interface IDnsServiceApi
    { IntPtr Construct(string service, string host, byte[] ipv4, ushort port, string id); uint Register(IntPtr request, IntPtr cancellation); uint Cancel(IntPtr cancellation); uint DeRegister(IntPtr request); void Free(IntPtr instance); }
    sealed class NativeApi : IDnsServiceApi
    {
        public IntPtr Construct(string service, string host, byte[] address, ushort port, string id)
        {
            var allocations = new List<IntPtr>();
            IntPtr Text(string value) { var p = Marshal.StringToHGlobalUni(value); allocations.Add(p); return p; }
            try
            {
                IntPtr keys = Marshal.AllocHGlobal(2 * IntPtr.Size), values = Marshal.AllocHGlobal(2 * IntPtr.Size), ipv4 = Marshal.AllocHGlobal(4);
                allocations.Add(keys); allocations.Add(values); allocations.Add(ipv4); Marshal.Copy(address, 0, ipv4, 4);
                Marshal.WriteIntPtr(keys, 0, Text("v")); Marshal.WriteIntPtr(keys, IntPtr.Size, Text("id")); Marshal.WriteIntPtr(values, 0, Text("1")); Marshal.WriteIntPtr(values, IntPtr.Size, Text(id));
                return DnsServiceConstructInstance(service, host, ipv4, IntPtr.Zero, port, 0, 0, 2, keys, values);
            }
            finally { foreach (IntPtr p in allocations) Marshal.FreeHGlobal(p); }
        }
        public uint Register(IntPtr request, IntPtr cancellation) => DnsServiceRegister(request, cancellation);
        public uint Cancel(IntPtr cancellation) => DnsServiceRegisterCancel(cancellation);
        public uint DeRegister(IntPtr request) => DnsServiceDeRegister(request, IntPtr.Zero);
        public void Free(IntPtr instance) => DnsServiceFreeInstance(instance);
    }
    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern IntPtr DnsServiceConstructInstance(string service, string host, IntPtr ipv4, IntPtr ipv6, ushort port, ushort priority, ushort weight, uint properties, IntPtr keys, IntPtr values);
    [DllImport("dnsapi.dll", ExactSpelling = true)] static extern uint DnsServiceRegister(IntPtr request, IntPtr cancellation);
    [DllImport("dnsapi.dll", ExactSpelling = true)] static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancellation);
    [DllImport("dnsapi.dll", ExactSpelling = true)] static extern uint DnsServiceRegisterCancel(IntPtr cancellation);
    [DllImport("dnsapi.dll", ExactSpelling = true)] static extern void DnsServiceFreeInstance(IntPtr instance);
}
