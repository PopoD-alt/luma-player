using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Luma.WindowsApp;

/// <summary>
/// A bounded diagnostic of Microsoft's installed Source Reader decoders. No renderer,
/// audio endpoint, codec installation, external process, or third-party decoder is used.
/// A failed CPU-readable output negotiation is not proof that every hardware playback
/// route on the machine is unavailable. Successful samples prove decoding, not playback.
/// </summary>
/// <remarks>
/// Original interop follows Microsoft's SDK mfapi.h, mfobjects.h and mfreadwrite.h:
/// https://github.com/microsoft/win32metadata/tree/main/generation/WinSDK/RecompiledIdlHeaders/um
/// https://learn.microsoft.com/windows/win32/medfound/using-the-source-reader-in-asynchronous-mode
/// https://learn.microsoft.com/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsourcereader-readsample
/// https://learn.microsoft.com/windows/win32/medfound/mf-source-reader-enable-video-processing
/// Only the first 32 streams and first four audio/video streams are probed. Each output
/// uses a fresh reader, at most 16 callbacks and three samples. Sample buffers are capped
/// at 64 MiB. Reads wait at most four seconds, attempts eight seconds; the caller
/// waits at most 45 seconds (plus up to five seconds for the single-flight gate).
/// Native opening, negotiation and COM teardown cannot safely be force-aborted: after
/// a caller timeout the background MTA retains ownership until native calls return.
/// A single-flight gate prevents accumulating abandoned native workers.
/// </remarks>
public static class MediaFoundationProbe
{
    public sealed record Operation(string Name, int HResult, string Hex, string? Symbol, string Message)
    {
        public bool Succeeded => HResult >= 0;
    }
    public sealed record Aperture(double X, double Y, int Width, int Height);
    public sealed record MediaTypeInfo(
        string MajorType, string Subtype, string SubtypeName,
        uint? Width, uint? Height, uint? FrameRateNumerator, uint? FrameRateDenominator,
        uint? PixelAspectNumerator, uint? PixelAspectDenominator, int? Stride,
        uint? InterlaceMode, Aperture? GeometricAperture, Aperture? MinimumDisplayAperture,
        uint? Channels, uint? SampleRate, uint? BitsPerSample, uint? BlockAlignment,
        uint? ChannelMask);
    public sealed record SampleInfo(uint StreamIndex, uint Flags, long Timestamp100Nanoseconds,
        long? Duration100Nanoseconds, int ByteCount, string Sha256, string PrefixHex,
        int NonzeroBytes, long? PcmFrames);
    public sealed record DecodeAttempt(string RequestedFormat, string Status,
        IReadOnlyList<Operation> Operations, MediaTypeInfo? NegotiatedType,
        IReadOnlyList<SampleInfo> Samples);
    public sealed record StreamResult(uint Index, string Kind, MediaTypeInfo? NativeType,
        string Status, IReadOnlyList<DecodeAttempt> Attempts, Operation? Failure);
    public sealed record Result(string FileName, string Status, long ElapsedMilliseconds,
        IReadOnlyList<Operation> Operations, IReadOnlyList<StreamResult> Streams, string Scope);

    private const string Scope = "Installed Microsoft Media Foundation Source Reader; CPU-readable decoded samples only; no audio hardware or rendering. Failure does not establish that all hardware decoder paths are unavailable.";
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);
    private const uint AllStreams = 0xfffffffe;
    private const int InvalidStream = unchecked((int)0xc00d36b3);
    private const int MaxSampleBytes = 64 * 1024 * 1024;
    private static readonly Guid Video = G("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid Audio = G("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MajorKey = G("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeKey = G("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid SizeKey = G("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid RateKey = G("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid AspectKey = G("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid StrideKey = G("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid InterlaceKey = G("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid GeometricKey = G("66758743-7e5f-400d-980a-aa8596c85696");
    private static readonly Guid DisplayKey = G("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid ChannelsKey = G("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SampleRateKey = G("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BitsKey = G("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid AlignKey = G("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid BytesPerSecondKey = G("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid ChannelMaskKey = G("55fb5765-644a-4caf-8479-938983bb1588");
    private static readonly Guid CallbackKey = G("1e3dbeac-bb43-4c35-b507-cd644464c965");
    private static readonly Guid VideoProcessingKey = G("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    /// <summary>Inspect a local file; cancellation throws. All native work runs on a dedicated MTA.</summary>
    public static async Task<Result> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        string name = Path.GetFileName(fullPath);
        if (!OperatingSystem.IsWindows())
            return Empty(name, "unsupported-platform", 0, Op("Platform", unchecked((int)0x80004001), "Windows is required."));
        // A local path is intentional: probing must not fetch a URL or trigger codec downloads.
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(fullPath))
            return Empty(name, "invalid-local-file", 0, Op("File.Exists", unchecked((int)0x80070002), "An existing local file is required; UNC paths are excluded."));
        var clock = Stopwatch.StartNew();
        if (!await WorkerGate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
            return Empty(name, "busy", clock.ElapsedMilliseconds, Op("WorkerGate", unchecked((int)0x800705b4), "A previous native probe is still unwinding."));
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                completion.TrySetResult(ProbeCore(fullPath, linked.Token));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(linked.Token); }
            catch (Exception ex) { completion.TrySetResult(Empty(name, "failed", clock.ElapsedMilliseconds, Op("Probe", ex.HResult, ex.Message))); }
            finally { linked.Dispose(); WorkerGate.Release(); }
        }) { IsBackground = true, Name = "Luma Media Foundation probe" };
        try { worker.SetApartmentState(ApartmentState.MTA); worker.Start(); }
        catch { linked.Dispose(); WorkerGate.Release(); throw; }
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
            return Empty(name, "timeout", clock.ElapsedMilliseconds, Op("Probe deadline", unchecked((int)0x800705b4), "Native cleanup continues on its owning MTA until any in-flight native call returns."));
        }
        catch (OperationCanceledException)
        {
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Result ProbeCore(string path, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var operations = new List<Operation>();
        var streams = new List<StreamResult>();
        bool com = false, mf = false;
        string status = "failed";
        try
        {
            int hr = CoInitializeEx(IntPtr.Zero, 0);
            operations.Add(Op("CoInitializeEx(MTA)", hr));
            if (hr < 0) return Finish();
            com = true;
            hr = MFStartup(0x00020070, 0);
            operations.Add(Op("MFStartup", hr));
            if (hr < 0) return Finish();
            mf = true;
            token.ThrowIfCancellationRequested();
            using (var reader = Reader.Open(path, false, operations))
            {
                if (reader.Pointer == IntPtr.Zero) { status = "open-failed"; return Finish(); }
                for (uint index = 0; index < 32; index++)
                {
                    token.ThrowIfCancellationRequested();
                    hr = Fn<GetNativeType>(reader.Pointer, 5)(reader.Pointer, index, 0, out var type);
                    if (hr == InvalidStream) break;
                    if (hr < 0)
                    {
                        Release(type);
                        streams.Add(new(index, "unknown", null, "native-type-failed", [], Op("GetNativeMediaType", hr)));
                        continue;
                    }
                    try
                    {
                        var info = Describe(type);
                        string kind = info.MajorType == Video.ToString() ? "video" : info.MajorType == Audio.ToString() ? "audio" : "other";
                        streams.Add(new(index, kind, info, kind == "other" ? "metadata-only" : "pending", [], null));
                    }
                    finally { Release(type); }
                }
            }
            int decodedStreams = 0;
            for (int i = 0; i < streams.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var stream = streams[i];
                if (stream.Kind != "video" && stream.Kind != "audio") continue;
                if (++decodedStreams > 4) { streams[i] = stream with { Status = "stream-limit" }; continue; }
                var attempts = new List<DecodeAttempt>();
                var formats = stream.Kind == "video"
                    ? new[] { ("BGRA32", Subtype(21)), ("NV12", Subtype(0x3231564e)), ("P010", Subtype(0x30313050)), ("BGRX32", Subtype(22)) }
                    : new[] { ("PCM16", Subtype(1)) };
                foreach (var (label, subtype) in formats)
                {
                    token.ThrowIfCancellationRequested();
                    if (timer.Elapsed > TimeSpan.FromSeconds(30)) { attempts.Add(new(label, "overall-deadline", [], null, [])); break; }
                    attempts.Add(Decode(path, stream, label, subtype, token));
                }
                streams[i] = stream with { Attempts = attempts, Status = attempts.Any(a => a.Status == "decoded") ? "decoded" : "decode-not-proven" };
            }
            var av = streams.Where(s => s.Kind is "video" or "audio").ToArray();
            status = av.Length == 0 ? "no-audio-video-streams" : av.All(s => s.Status == "decoded") ? "decoded" : av.Any(s => s.Status == "decoded") ? "partial" : "decode-not-proven";
        }
        catch (NativeFailure) { status = "initialization-failed"; }
        finally
        {
            if (mf) operations.Add(Op("MFShutdown", MFShutdown()));
            if (com) CoUninitialize();
        }
        return Finish();
        Result Finish() => new(Path.GetFileName(path), status, timer.ElapsedMilliseconds, operations, streams, Scope);
    }

    [SupportedOSPlatform("windows")]
    private static DecodeAttempt Decode(string path, StreamResult stream, string label, Guid subtype, CancellationToken token)
    {
        var operations = new List<Operation>();
        var samples = new List<SampleInfo>();
        var timer = Stopwatch.StartNew();
        MediaTypeInfo? negotiated = null;
        string status = "failed";
        IntPtr type = IntPtr.Zero;
        try
        {
            // Only RGB output asks for the documented software color converter. Native
            // NV12/P010 requests avoid that converter, retaining a 10-bit diagnostic path.
            using var reader = Reader.Open(path, label is "BGRA32" or "BGRX32", operations);
            if (reader.Pointer == IntPtr.Zero) return Done("open-failed");
            Check(Fn<SetSelection>(reader.Pointer, 4)(reader.Pointer, AllStreams, 0), "Deselect all streams", operations);
            Check(Fn<SetSelection>(reader.Pointer, 4)(reader.Pointer, stream.Index, 1), "Select stream", operations);
            Check(MFCreateMediaType(out type), "MFCreateMediaType", operations);
            SetGuid(type, MajorKey, stream.Kind == "video" ? Video : Audio, operations);
            SetGuid(type, SubtypeKey, subtype, operations);
            if (stream.Kind == "audio")
            {
                uint channels = stream.NativeType?.Channels ?? 0, rate = stream.NativeType?.SampleRate ?? 0;
                if (channels is < 1 or > 32 || rate is < 1 or > 768000) return Done("unsupported-native-audio-geometry");
                SetUInt(type, ChannelsKey, channels, operations);
                SetUInt(type, SampleRateKey, rate, operations);
                SetUInt(type, BitsKey, 16, operations);
                SetUInt(type, AlignKey, channels * 2, operations);
                SetUInt(type, BytesPerSecondKey, checked(rate * channels * 2), operations);
                if (stream.NativeType?.ChannelMask is uint mask && mask != 0) SetUInt(type, ChannelMaskKey, mask, operations);
            }
            int hr = Fn<SetType>(reader.Pointer, 7)(reader.Pointer, stream.Index, IntPtr.Zero, type);
            operations.Add(Op("SetCurrentMediaType(" + label + ")", hr));
            if (hr < 0) return Done("negotiation-failed");
            negotiated = CurrentType(reader.Pointer, stream.Index, operations);
            for (int read = 0; read < 16 && samples.Count < 3; read++)
            {
                token.ThrowIfCancellationRequested();
                // In asynchronous mode every ReadSample output argument MUST be NULL.
                hr = Fn<ReadSample>(reader.Pointer, 9)(reader.Pointer, stream.Index, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                operations.Add(Op("ReadSample[" + read + "]", hr));
                if (hr < 0) return Done("read-failed");
                var remaining = TimeSpan.FromSeconds(8) - timer.Elapsed;
                if (remaining <= TimeSpan.Zero) return Done("attempt-deadline");
                var readEvent = reader.Callback.Wait(remaining < TimeSpan.FromSeconds(4) ? remaining : TimeSpan.FromSeconds(4), token);
                if (readEvent is null) return Done("read-timeout");
                try
                {
                    operations.Add(Op("OnReadSample[" + read + "]", readEvent.HResult,
                        $"Stream={readEvent.Stream}, Flags=0x{readEvent.Flags:X8}, Timestamp100ns={readEvent.Timestamp}, HasSample={readEvent.Sample != IntPtr.Zero}"));
                    if (readEvent.HResult < 0 || (readEvent.Flags & 1) != 0) return Done("decode-failed");
                    if (readEvent.Stream != stream.Index) return Done("unexpected-stream");
                    if ((readEvent.Flags & 0x30) != 0 || readEvent.Sample != IntPtr.Zero)
                        negotiated = CurrentType(reader.Pointer, stream.Index, operations);
                    if (negotiated?.Subtype != subtype.ToString()) return Done("unexpected-output-type");
                    if (stream.Kind == "video" && (negotiated.Width is null or 0 || negotiated.Height is null or 0))
                        return Done("missing-decoded-video-geometry");
                    if (stream.Kind == "audio" && (negotiated.Channels != stream.NativeType?.Channels ||
                        negotiated.SampleRate != stream.NativeType?.SampleRate || negotiated.BitsPerSample != 16 ||
                        negotiated.BlockAlignment != negotiated.Channels * 2))
                        return Done("unexpected-decoded-audio-geometry");
                    if (readEvent.Sample != IntPtr.Zero)
                    {
                        var sample = InspectSample(readEvent, negotiated, operations);
                        if (sample.ByteCount > 0) samples.Add(sample);
                    }
                    if ((readEvent.Flags & 2) != 0) return Done(samples.Count > 0 ? "decoded" : "end-of-stream-without-sample");
                }
                finally { Release(readEvent.Sample); }
            }
            status = samples.Count > 0 ? "decoded" : "sample-limit-without-data";
        }
        catch (NativeFailure) { status = "failed"; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { operations.Add(Op("Inspect decoded sample", ex.HResult, ex.Message)); status = "failed"; }
        finally { Release(type); }
        return Done(status);
        DecodeAttempt Done(string value) => new(label, value, operations, negotiated, samples);
    }

    private static SampleInfo InspectSample(ReadEvent read, MediaTypeInfo? type, List<Operation> operations)
    {
        Check(Fn<GetUInt>(read.Sample, 45)(read.Sample, out uint total), "IMFSample.GetTotalLength", operations);
        if (total > MaxSampleBytes) throw new InvalidDataException("Decoded sample exceeds the 64 MiB diagnostic bound.");
        long? duration = Fn<GetLong>(read.Sample, 37)(read.Sample, out long value) >= 0 ? value : null;
        IntPtr buffer = IntPtr.Zero;
        bool locked = false;
        try
        {
            Check(Fn<GetPointer>(read.Sample, 41)(read.Sample, out buffer), "ConvertToContiguousBuffer", operations);
            Check(Fn<LockBuffer>(buffer, 3)(buffer, out var data, out uint maximum, out uint length), "IMFMediaBuffer.Lock", operations);
            locked = true;
            if (length > maximum || length > MaxSampleBytes || length != total || (length != 0 && data == IntPtr.Zero))
                throw new InvalidDataException("Invalid decoded sample buffer length.");
            byte[] bytes = new byte[checked((int)length)];
            if (bytes.Length != 0) Marshal.Copy(data, bytes, 0, bytes.Length);
            long? frames = type?.Subtype == Subtype(1).ToString() && type.BlockAlignment is > 0
                ? length % type.BlockAlignment.Value == 0 ? length / type.BlockAlignment.Value : throw new InvalidDataException("PCM data is not frame aligned.")
                : null;
            return new(read.Stream, read.Flags, read.Timestamp, duration, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 32))), bytes.Count(b => b != 0), frames);
        }
        finally
        {
            if (locked) operations.Add(Op("IMFMediaBuffer.Unlock", Fn<NoArguments>(buffer, 4)(buffer)));
            Release(buffer);
        }
    }

    private static MediaTypeInfo? CurrentType(IntPtr reader, uint stream, List<Operation> operations)
    {
        int hr = Fn<GetCurrentTypeDelegate>(reader, 6)(reader, stream, out var type);
        operations.Add(Op("GetCurrentMediaType", hr));
        try { return hr >= 0 ? Describe(type) : null; }
        finally { Release(type); }
    }
    private static MediaTypeInfo Describe(IntPtr type)
    {
        Guid major = GetGuid(type, MajorKey) ?? Guid.Empty, subtype = GetGuid(type, SubtypeKey) ?? Guid.Empty;
        ulong? size = GetUInt64(type, SizeKey), rate = GetUInt64(type, RateKey), aspect = GetUInt64(type, AspectKey);
        uint? stride = GetUInt32(type, StrideKey);
        return new(major.ToString(), subtype.ToString(), SubtypeName(subtype), Hi(size), Lo(size), Hi(rate), Lo(rate), Hi(aspect), Lo(aspect),
            stride.HasValue ? unchecked((int)stride.Value) : null, GetUInt32(type, InterlaceKey), Area(type, GeometricKey), Area(type, DisplayKey),
            GetUInt32(type, ChannelsKey), GetUInt32(type, SampleRateKey), GetUInt32(type, BitsKey), GetUInt32(type, AlignKey), GetUInt32(type, ChannelMaskKey));
    }
    private static uint? Hi(ulong? value) => value.HasValue ? (uint)(value.Value >> 32) : null;
    private static uint? Lo(ulong? value) => value.HasValue ? (uint)value.Value : null;
    private static uint? GetUInt32(IntPtr obj, Guid key) => Fn<GetAttributeUInt>(obj, 7)(obj, ref key, out uint value) >= 0 ? value : null;
    private static ulong? GetUInt64(IntPtr obj, Guid key) => Fn<GetAttributeUInt64>(obj, 8)(obj, ref key, out ulong value) >= 0 ? value : null;
    private static Guid? GetGuid(IntPtr obj, Guid key) => Fn<GetAttributeGuid>(obj, 10)(obj, ref key, out Guid value) >= 0 ? value : null;
    private static Aperture? Area(IntPtr obj, Guid key)
    {
        if (Fn<GetAttributeUInt>(obj, 14)(obj, ref key, out uint size) < 0 || size != 16) return null;
        byte[] blob = new byte[16];
        if (Fn<GetBlob>(obj, 15)(obj, ref key, blob, 16, out uint actual) < 0 || actual != 16) return null;
        return new(BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(2)) + BinaryPrimitives.ReadUInt16LittleEndian(blob) / 65536.0,
            BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(6)) + BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(4)) / 65536.0,
            BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8)), BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(12)));
    }
    private static void SetGuid(IntPtr obj, Guid key, Guid value, List<Operation> operations) => Check(Fn<SetAttributeGuid>(obj, 24)(obj, ref key, ref value), "SetGUID(" + key + ")", operations);
    private static void SetUInt(IntPtr obj, Guid key, uint value, List<Operation> operations) => Check(Fn<SetAttributeUInt>(obj, 21)(obj, ref key, value), "SetUINT32(" + key + ")", operations);
    private static void Check(int hr, string name, List<Operation> operations) { operations.Add(Op(name, hr)); if (hr < 0) throw new NativeFailure(); }
    private sealed class NativeFailure : Exception { }
    private static Guid G(string value) => new(value);
    private static Guid Subtype(uint value) => new(unchecked((int)value), 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
    private static string SubtypeName(Guid value)
    {
        if (value == Subtype(21)) return "ARGB32 (BGRA bytes)";
        if (value == Subtype(22)) return "RGB32 (BGRX bytes)";
        if (value == Subtype(1)) return "PCM";
        if (value == Subtype(3)) return "IEEE_FLOAT";
        if (value == Subtype(0x1610)) return "AAC";
        byte[] bytes = value.ToByteArray();
        if (bytes.Take(4).All(b => b >= 32 && b <= 126) && value == Subtype(BinaryPrimitives.ReadUInt32LittleEndian(bytes)))
            return System.Text.Encoding.ASCII.GetString(bytes, 0, 4);
        return value.ToString();
    }
    private static Result Empty(string name, string status, long elapsed, Operation operation) => new(name, status, elapsed, [operation], [], Scope);
    private static Operation Op(string name, int hr, string? message = null)
    {
        string? symbol = unchecked((uint)hr) switch
        {
            0 => "S_OK", 1 => "S_FALSE", 0xc00d36b3 => "MF_E_INVALIDSTREAMNUMBER", 0xc00d36b4 => "MF_E_INVALIDMEDIATYPE",
            0xc00d36b9 => "MF_E_NO_MORE_TYPES", 0xc00d5212 => "MF_E_TOPO_CODEC_NOT_FOUND", 0x80070057 => "E_INVALIDARG",
            0x80004002 => "E_NOINTERFACE", 0x800705b4 => "HRESULT_FROM_WIN32(ERROR_TIMEOUT)", _ => null
        };
        return new(name, hr, "0x" + unchecked((uint)hr).ToString("X8"), symbol,
            message ?? (hr >= 0 ? "Succeeded" : Marshal.GetExceptionForHR(hr, new IntPtr(-1))?.Message ?? "Native operation failed"));
    }
    private static T Fn<T>(IntPtr obj, int slot) where T : Delegate
    {
        if (obj == IntPtr.Zero) throw new InvalidOperationException("A native COM pointer was null.");
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), checked(slot * IntPtr.Size)));
    }
    private static void Release(IntPtr pointer) { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }

    [SupportedOSPlatform("windows")]
    private sealed class Reader : IDisposable
    {
        internal IntPtr Pointer;
        internal readonly SourceReaderCallback Callback = new();
        private IntPtr attributes, callbackPointer;
        internal static Reader Open(string path, bool videoProcessing, List<Operation> operations)
        {
            var reader = new Reader();
            try
            {
                Check(MFCreateAttributes(out reader.attributes, 2), "MFCreateAttributes", operations);
                reader.callbackPointer = Marshal.GetComInterfaceForObject(reader.Callback, typeof(ISourceReaderCallback));
                var callbackKey = CallbackKey;
                Check(Fn<SetUnknown>(reader.attributes, 27)(reader.attributes, ref callbackKey, reader.callbackPointer), "Set async callback", operations);
                if (videoProcessing) SetUInt(reader.attributes, VideoProcessingKey, 1, operations);
                int hr = MFCreateSourceReaderFromURL(path, reader.attributes, out reader.Pointer);
                operations.Add(Op("MFCreateSourceReaderFromURL", hr));
                if (hr < 0) { Release(reader.Pointer); reader.Pointer = IntPtr.Zero; }
                return reader;
            }
            catch { reader.Dispose(); throw; }
        }
        public void Dispose()
        {
            Callback.Close();
            if (Pointer != IntPtr.Zero)
            {
                try { _ = Fn<Flush>(Pointer, 10)(Pointer, AllStreams); }
                finally { Release(Pointer); Pointer = IntPtr.Zero; }
            }
            Release(attributes); attributes = IntPtr.Zero;
            Release(callbackPointer); callbackPointer = IntPtr.Zero;
            Callback.Dispose();
            GC.KeepAlive(Callback);
        }
    }

    internal sealed record ReadEvent(int HResult, uint Stream, uint Flags, long Timestamp, IntPtr Sample);
    [ComVisible(true), Guid("deec8d99-fa1d-4d82-84c2-2c8969944867"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISourceReaderCallback
    {
        [PreserveSig] int OnReadSample(int status, uint stream, uint flags, long timestamp, IntPtr sample);
        [PreserveSig] int OnFlush(uint stream);
        [PreserveSig] int OnEvent(uint stream, IntPtr mediaEvent);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class SourceReaderCallback : ISourceReaderCallback, IDisposable
    {
        private readonly object gate = new();
        private readonly Queue<ReadEvent> events = new();
        private readonly AutoResetEvent available = new(false);
        private bool closed;
        internal SourceReaderCallback() { }
        public int OnReadSample(int status, uint stream, uint flags, long timestamp, IntPtr sample)
        {
            try
            {
                lock (gate)
                {
                    if (closed) return 0;
                    if (sample != IntPtr.Zero) Marshal.AddRef(sample);
                    events.Enqueue(new(status, stream, flags, timestamp, sample));
                    available.Set();
                }
                return 0;
            }
            catch { return unchecked((int)0x80004005); }
        }
        public int OnFlush(uint stream) => 0;
        public int OnEvent(uint stream, IntPtr mediaEvent) => 0;
        private ReadEvent? Take() { lock (gate) return events.Count > 0 ? events.Dequeue() : null; }
        internal ReadEvent? Wait(TimeSpan timeout, CancellationToken token)
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var value = Take();
                if (value != null) return value;
                int left = (int)Math.Max(0, (timeout - timer.Elapsed).TotalMilliseconds);
                if (left == 0 || WaitHandle.WaitAny([available, token.WaitHandle], left) == WaitHandle.WaitTimeout) return null;
            }
        }
        internal void Close()
        {
            lock (gate)
            {
                closed = true;
                while (events.Count > 0) Release(events.Dequeue().Sample);
            }
        }
        public void Dispose() { Close(); available.Dispose(); }
    }

    // Slot numbers include IUnknown, and (for samples/media types) all 30 IMFAttributes methods.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetNativeType(IntPtr self, uint stream, uint index, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCurrentTypeDelegate(IntPtr self, uint stream, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetType(IntPtr self, uint stream, IntPtr reserved, IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSelection(IntPtr self, uint stream, int selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadSample(IntPtr self, uint stream, uint controls, IntPtr actual, IntPtr flags, IntPtr timestamp, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Flush(IntPtr self, uint stream);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAttributeUInt(IntPtr self, ref Guid key, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAttributeUInt64(IntPtr self, ref Guid key, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAttributeGuid(IntPtr self, ref Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetAttributeUInt(IntPtr self, ref Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetAttributeGuid(IntPtr self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBlob(IntPtr self, ref Guid key, [Out] byte[] bytes, uint capacity, out uint actual);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUnknown(IntPtr self, ref Guid key, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt(IntPtr self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetLong(IntPtr self, out long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointer(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockBuffer(IntPtr self, out IntPtr data, out uint maximum, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArguments(IntPtr self);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
}
