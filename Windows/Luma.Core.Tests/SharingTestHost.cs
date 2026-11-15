using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Text.Json;
using Luma.Core.Sharing;

namespace Luma.Core.Tests;

internal static class SharingTestHost
{
    public static async Task<int> RunAsync(string fixtures, string readyFile)
    {
        fixtures = Path.GetFullPath(fixtures); readyFile = Path.GetFullPath(readyFile);
        string demo = Path.Combine(fixtures, "Luma-Demo.mp4"), hevc = Path.Combine(fixtures, "Luma-HEVC10-AAC51.mp4");
        if (!File.Exists(demo) || !File.Exists(hevc)) throw new FileNotFoundException("Generated test media is missing.");
        string demoId = Guid.NewGuid().ToString("N"), hevcId = Guid.NewGuid().ToString("N");
        await using var server = new PlaylistSharingServer("Luma generated test playlist");
        server.SetPlaylist([new(demoId, demo, "Luma Demo", 12, 1), new(hevcId, hevc, "Luma HEVC10 AAC 5.1", 2, .25)]);
        using var diagnostics = new HostDiagnostics(server);
        server.Start(); Directory.CreateDirectory(Path.GetDirectoryName(readyFile)!);
        diagnostics.Start();
        string temporary = readyFile + ".pending";
        string stopReason = "host-error";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { port = server.Port, code = server.PairingCode, ids = new { demo = demoId, hevc10 = hevcId } }));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, readyFile, true);
            Console.WriteLine("Sharing test host ready; generated fixtures only.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { while (await Console.In.ReadLineAsync(timeout.Token) is not null) { } stopReason = "stdin-eof"; }
            catch (OperationCanceledException) { stopReason = "deadline"; }
            return 0;
        }
        finally
        {
            diagnostics.Snapshot("stop-requested", stopReason);
            server.Stop();
            await diagnostics.StopAsync(stopReason);
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(readyFile)) File.Delete(readyFile);
        }
    }

    /// <summary>Test-host evidence only: no request data, remote addresses, paths or secrets.</summary>
    sealed class HostDiagnostics(PlaylistSharingServer server) : EventListener
    {
        // .NET 10's public System.Net.Sockets EventSource emits AcceptStart/Stop/Failed.
        // Inspect only the integer SocketError payload; its exception-message/address
        // payloads are deliberately never retained. Source: dotnet/runtime v10.0.0,
        // src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketsTelemetry.cs.
        readonly Stopwatch elapsed = Stopwatch.StartNew();
        readonly CancellationTokenSource observing = new();
        Task? monitor, accepting;
        int acceptStarts, acceptStops, acceptFailures, lastSocketError;
        int maxActive, maxActiveAtAccept, capPressureAcceptStops, records;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Net.Sockets") EnableEvents(source, EventLevel.Informational, EventKeywords.All);
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventSource.Name != "System.Net.Sockets") return;
            switch (data.EventName)
            {
                case "AcceptStart": Interlocked.Increment(ref acceptStarts); break;
                case "AcceptStop":
                    Interlocked.Increment(ref acceptStops);
                    int active = server?.ActiveConnections ?? 0;
                    Maximum(ref maxActiveAtAccept, active);
                    // This is pressure observed before the production accept loop's
                    // admission check, not a fabricated exact rejection counter.
                    if (active >= 12) Interlocked.Increment(ref capPressureAcceptStops);
                    break;
                case "AcceptFailed":
                    Interlocked.Increment(ref acceptFailures);
                    if (data.Payload is { Count: > 0 } && data.Payload[0] is int code) Volatile.Write(ref lastSocketError, code);
                    break;
            }
        }

        public void Start()
        {
            // Read-only test introspection detects the production loop's swallowed
            // SocketException: IsRunning alone stays true after that loop exits.
            accepting = typeof(PlaylistSharingServer).GetField("accepting", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(server) as Task;
            Snapshot("started");
            monitor = ObserveAsync();
        }

        async Task ObserveAsync()
        {
            int previousActive = -1, previousStreams = -1;
            bool previousAcceptDone = false;
            long heartbeat = 0;
            try
            {
                while (true)
                {
                    int active = server.ActiveConnections, streams = server.ActiveMediaStreams;
                    bool acceptDone = accepting?.IsCompleted ?? false;
                    Maximum(ref maxActive, active);
                    if (active != previousActive || streams != previousStreams || acceptDone != previousAcceptDone || elapsed.ElapsedMilliseconds >= heartbeat)
                    {
                        if (Volatile.Read(ref records) < 1024) Snapshot("sample");
                        previousActive = active; previousStreams = streams; previousAcceptDone = acceptDone;
                        heartbeat = elapsed.ElapsedMilliseconds + 1000;
                    }
                    await Task.Delay(100, observing.Token);
                }
            }
            catch (OperationCanceledException) when (observing.IsCancellationRequested) { }
        }

        public void Snapshot(string kind, string? stopReason = null)
        {
            Interlocked.Increment(ref records);
            Maximum(ref maxActive, server.ActiveConnections);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                source = "SharingTestHost", kind, utc = DateTimeOffset.UtcNow, elapsedMs = elapsed.ElapsedMilliseconds,
                running = server.IsRunning, activeConnections = server.ActiveConnections, activeMediaStreams = server.ActiveMediaStreams,
                sampledMaxConnections = Volatile.Read(ref maxActive), maxActiveAtAcceptStop = Volatile.Read(ref maxActiveAtAccept),
                acceptStarts = Volatile.Read(ref acceptStarts), acceptStops = Volatile.Read(ref acceptStops), acceptFailures = Volatile.Read(ref acceptFailures),
                lastAcceptSocketError = Volatile.Read(ref lastSocketError), capPressureAcceptStops = Volatile.Read(ref capPressureAcceptStops),
                acceptTaskObserved = accepting is not null, acceptTaskCompleted = accepting?.IsCompleted, acceptTaskStatus = accepting?.Status.ToString(), stopReason
            }));
        }

        public async Task StopAsync(string reason)
        {
            observing.Cancel();
            if (monitor is not null) await monitor;
            Snapshot("stopped", reason);
        }

        static void Maximum(ref int target, int value)
        {
            int previous;
            while (value > (previous = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, previous) != previous) { }
        }

        public override void Dispose() { observing.Cancel(); base.Dispose(); observing.Dispose(); }
    }
}
