using System.Diagnostics;
using System.Runtime.InteropServices;
using Luma.Core.Sharing;
using Luma.WindowsApp;

namespace Luma.Core.Tests;

public static class DnsSdLifecycleTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        using var server = new PlaylistSharingServer("Original DNS lifecycle test");
        var pending = new FakeApi(); using (var advertisement = new WindowsDnsSdAdvertisement(server, pending))
        {
            await pending.Registered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew(); advertisement.Dispose();
            Check(watch.ElapsedMilliseconds < 500, "Dispose returns without waiting on native cancellation");
            await pending.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!advertisement.CompletionTask.IsCompleted && pending.Frees == 0, "Native request stays alive while cancel is in flight");
            pending.AllowCancel.Set(); await advertisement.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3));
            Check(pending.Frees == 1 && !pending.FreedInsideNative && pending.CancelThread != pending.OwnerThread, "Cancel callback can finish without a gate deadlock and cleanup follows native return");
        }
        var active = new FakeApi(); using (var advertisement = new WindowsDnsSdAdvertisement(server, active))
        {
            await active.Registered.Task.WaitAsync(TimeSpan.FromSeconds(3)); active.Fire(0);
            await Until(() => advertisement.Status == "Bonjour discovery active");
            advertisement.Dispose(); await advertisement.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3));
            Check(active.Deregistrations == 1 && active.Frees == 1 && !active.FreedInsideNative, "Active registration deregisters off-thread with an inline completion and one owned free");
        }
        var inline = new FakeApi { InlineRegistration = true }; using (var advertisement = new WindowsDnsSdAdvertisement(server, inline))
        {
            await Until(() => advertisement.Status == "Bonjour discovery active"); advertisement.Dispose();
            await advertisement.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!inline.FreedInsideNative && inline.Frees == 1, "Inline Register completion cannot free an in-flight request");
        }
        var failed = new FakeApi { RegistrationResult = 87 }; using (var advertisement = new WindowsDnsSdAdvertisement(server, failed))
        {
            await advertisement.CompletionTask.WaitAsync(TimeSpan.FromSeconds(3)); Check(failed.Frees == 1, "Immediate native failure cleans up without waiting for a callback");
        }
        Console.WriteLine("PASS: DNS-SD cancellation returns to UI, synchronous native callback waits cannot deadlock, registration/deregistration buffers survive native calls, and owned instances free exactly once");
    }
    static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("DNS lifecycle: " + message); }
    sealed class FakeApi : WindowsDnsSdAdvertisement.IDnsServiceApi
    {
        public readonly int OwnerThread = Environment.CurrentManagedThreadId;
        public readonly TaskCompletionSource Registered = new(TaskCreationOptions.RunContinuationsAsynchronously), CancelEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim AllowCancel = new(false);
        public bool InlineRegistration, FreedInsideNative;
        public uint RegistrationResult = 9506;
        public int Frees, Deregistrations, CancelThread;
        WindowsDnsSdAdvertisement.Registration registration;
        int inside;
        public IntPtr Construct(string service, string host, byte[] ipv4, ushort port, string id)
        { Check(service.Split('.')[0].Length <= 63 && !service.Contains(id, StringComparison.Ordinal), "Bounded opaque discovery label"); return Marshal.AllocHGlobal(1); }
        public uint Register(IntPtr request, IntPtr cancellation)
        {
            Interlocked.Increment(ref inside); registration = Marshal.PtrToStructure<WindowsDnsSdAdvertisement.Registration>(request);
            if (InlineRegistration) Fire(0); Interlocked.Decrement(ref inside); Registered.TrySetResult(); return RegistrationResult;
        }
        public uint Cancel(IntPtr cancellation)
        {
            Interlocked.Increment(ref inside); CancelThread = Environment.CurrentManagedThreadId; CancelEntered.TrySetResult();
            Check(AllowCancel.Wait(TimeSpan.FromSeconds(3)), "Test releases native cancellation");
            Check(Task.Run(() => Fire(995)).Wait(TimeSpan.FromSeconds(2)), "Native cancellation callback finishes before Cancel returns");
            Interlocked.Decrement(ref inside); return 0;
        }
        public uint DeRegister(IntPtr request)
        {
            Interlocked.Increment(ref inside); Interlocked.Increment(ref Deregistrations);
            Check(Task.Run(() => Fire(0)).Wait(TimeSpan.FromSeconds(2)), "Native deregistration callback completes synchronously");
            Interlocked.Decrement(ref inside); return 9506;
        }
        public void Fire(uint result) => Marshal.GetDelegateForFunctionPointer<WindowsDnsSdAdvertisement.Completion>(registration.Callback)(result, registration.Context, IntPtr.Zero);
        public void Free(IntPtr instance)
        { if (Volatile.Read(ref inside) != 0) FreedInsideNative = true; Interlocked.Increment(ref Frees); Marshal.FreeHGlobal(instance); }
    }
}
