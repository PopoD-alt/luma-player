using System.Net.Sockets;
using System.Runtime.InteropServices;
using Luma.Core.Sharing;

namespace Luma.WindowsApp;

/// <summary>Windows10 DNS-SD/mDNS registration; URL entry remains available when multicast is unavailable.</summary>
internal sealed class WindowsDnsSdAdvertisement : IDisposable
{
    const uint Pending = 9506;
    readonly object gate = new();
    static readonly Completion callback = Complete;
    IntPtr request, cancellation, instance;
    GCHandle root;
    bool registering, registered, deregistering, stopping, cleaned;
    string status = "Manual URL entry is available";
    public string Status => Volatile.Read(ref status);

    public WindowsDnsSdAdvertisement(PlaylistSharingServer server)
    {
        if (!OperatingSystem.IsWindows()) return;
        var addresses = PlaylistSharingServer.LocalAddresses(); var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (address is null) { status = "Discovery unavailable; use a displayed URL"; return; }
        var allocations = new List<IntPtr>();
        IntPtr Text(string value) { var pointer = Marshal.StringToHGlobalUni(value); allocations.Add(pointer); return pointer; }
        try
        {
            IntPtr keys = Marshal.AllocHGlobal(2 * IntPtr.Size), values = Marshal.AllocHGlobal(2 * IntPtr.Size), ipv4 = Marshal.AllocHGlobal(4);
            allocations.Add(keys); allocations.Add(values); allocations.Add(ipv4); Marshal.Copy(address.GetAddressBytes(), 0, ipv4, 4);
            Marshal.WriteIntPtr(keys, 0, Text("v")); Marshal.WriteIntPtr(keys, IntPtr.Size, Text("id"));
            Marshal.WriteIntPtr(values, 0, Text("1")); Marshal.WriteIntPtr(values, IntPtr.Size, Text(server.Id));
            string host = new(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(48).ToArray()); if (host.Length == 0) host = "luma";
            instance = DnsServiceConstructInstance("Luma-" + server.Id[..12] + "._luma-player._tcp.local", host + ".local", ipv4, IntPtr.Zero, (ushort)server.Port, 0, 0, 2, keys, values);
            if (instance == IntPtr.Zero) { status = "Discovery unavailable; use a displayed URL"; return; }
            root = GCHandle.Alloc(this); cancellation = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(cancellation, IntPtr.Zero);
            request = Marshal.AllocHGlobal(Marshal.SizeOf<Registration>());
            Marshal.StructureToPtr(new Registration { Version = 1, Instance = instance, Callback = Marshal.GetFunctionPointerForDelegate(callback), Context = GCHandle.ToIntPtr(root) }, request, false);
            registering = true; status = "Starting Bonjour discovery…"; uint result = DnsServiceRegister(request, cancellation);
            if (result != Pending) { registering = false; status = "Discovery unavailable; use a displayed URL"; Cleanup(); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        { status = "Discovery unavailable; use a displayed URL"; Cleanup(); }
        finally { foreach (IntPtr allocation in allocations) Marshal.FreeHGlobal(allocation); }
    }
    public void Dispose()
    {
        bool cancel, deregister, cleanup;
        lock (gate) { if (stopping || cleaned) return; stopping = true; status = "Sharing discovery stopped"; cancel = registering; deregister = registered; cleanup = !registering && !registered; }
        if (cancel) { lock (gate) { if (!cleaned && registering) _ = DnsServiceRegisterCancel(cancellation); } }
        if (deregister) BeginDeregister(); else if (cleanup) Cleanup();
    }
    static void Complete(uint result, IntPtr context, IntPtr returned)
    {
        WindowsDnsSdAdvertisement? owner = null;
        IntPtr original = IntPtr.Zero;
        try { owner = GCHandle.FromIntPtr(context).Target as WindowsDnsSdAdvertisement; original = owner?.instance ?? IntPtr.Zero; owner?.OnComplete(result); }
        catch (InvalidOperationException) { }
        finally { if (returned != IntPtr.Zero && returned != original) DnsServiceFreeInstance(returned); }
    }
    void OnComplete(uint result)
    {
        bool remove = false, cleanup = false;
        lock (gate)
        {
            if (cleaned) return;
            if (deregistering) { deregistering = registered = false; cleanup = true; }
            else
            {
                registering = false;
                if (result == 0) { registered = true; status = stopping ? "Stopping Bonjour discovery…" : "Bonjour discovery active"; remove = stopping; }
                else { status = stopping ? "Sharing discovery stopped" : "Discovery unavailable; use a displayed URL"; cleanup = true; }
            }
        }
        if (remove) BeginDeregister(); else if (cleanup) Cleanup();
    }
    void BeginDeregister()
    {
        lock (gate) { if (cleaned || deregistering || !registered) return; deregistering = true; }
        if (DnsServiceDeRegister(request, IntPtr.Zero) != Pending) Cleanup();
    }
    void Cleanup()
    {
        lock (gate)
        {
            if (cleaned) return; cleaned = true;
            if (request != IntPtr.Zero) { Marshal.FreeHGlobal(request); request = IntPtr.Zero; }
            if (cancellation != IntPtr.Zero) { Marshal.FreeHGlobal(cancellation); cancellation = IntPtr.Zero; }
            if (instance != IntPtr.Zero) { DnsServiceFreeInstance(instance); instance = IntPtr.Zero; }
            if (root.IsAllocated) root.Free();
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct Registration
    { public uint Version, InterfaceIndex; public IntPtr Instance, Callback, Context, Credentials; public int UnicastEnabled; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Completion(uint status, IntPtr context, IntPtr instance);
    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern IntPtr DnsServiceConstructInstance(string service, string host, IntPtr ipv4, IntPtr ipv6, ushort port, ushort priority, ushort weight, uint properties, IntPtr keys, IntPtr values);
    [DllImport("dnsapi.dll", ExactSpelling = true)] static extern uint DnsServiceRegister(IntPtr request, IntPtr cancellation);
    [DllImport("dnsapi.dll", ExactSpelling = true)] static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancellation);
    [DllImport("dnsapi.dll", ExactSpelling = true)] static extern uint DnsServiceRegisterCancel(IntPtr cancellation);
    [DllImport("dnsapi.dll", ExactSpelling = true)] static extern void DnsServiceFreeInstance(IntPtr instance);
}
