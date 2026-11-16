import Foundation

private final class AnalysisProbeState: @unchecked Sendable {
    let lock = NSLock(); var values: [Double] = []; var shouldCancel = false
    func progress(_ value: Double) { lock.lock(); values.append(value); lock.unlock() }
    func cancel() { lock.lock(); shouldCancel = true; lock.unlock() }
    var cancelled: Bool { lock.lock(); defer { lock.unlock() }; return shouldCancel }
}
@main struct SkipAudioAnalysisNative {
    static func signal(_ seconds: Double, seed: Int) -> [Int16] {
        (0..<Int(seconds * 8000)).map { i in
            let t = Double(i) / 8000, note = Int(t * 8)
            let frequency = Double(130 + (note * 37 + seed * 13) % 800), mix = 0.15 + 0.35 * Double((seed + note * 17) % 13) / 12
            return Int16(((sin(2 * Double.pi * frequency * t) + mix * sin(2 * Double.pi * frequency * 2.37 * t + Double(seed))) * 7000).rounded())
        }
    }
    static func wav(_ parts: [(Double, Int)], to url: URL) throws {
        let samples = parts.flatMap { signal($0.0, seed: $0.1) }
        var data = Data()
        func text(_ value: String) { data.append(Data(value.utf8)) }
        func number<T: FixedWidthInteger>(_ value: T) { var little = value.littleEndian; withUnsafeBytes(of: &little) { data.append(contentsOf: $0) } }
        text("RIFF"); number(UInt32(36 + samples.count * 2)); text("WAVEfmt "); number(UInt32(16)); number(UInt16(1)); number(UInt16(1))
        number(UInt32(8000)); number(UInt32(16000)); number(UInt16(2)); number(UInt16(16)); text("data"); number(UInt32(samples.count * 2))
        for value in samples { number(value) }; try data.write(to: url)
    }
    static func main() throws {
        let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        let output = root.appendingPathComponent("build/tests", isDirectory: true)
        let temporary = output.appendingPathComponent("skip-audio-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }
        let a = temporary.appendingPathComponent("first.wav"), b = temporary.appendingPathComponent("second.wav")
        try wav([(1, 10), (4, 123), (3, 20), (3, 456), (1, 30)], to: a)
        try wav([(2, 11), (4, 123), (2, 21), (3, 456), (1, 31)], to: b)
        let runtime = ProcessInfo.processInfo.environment["LUMA_LIBVLC_PATH"] ?? root.appendingPathComponent("ThirdParty/runtime/macos-lgpl-arm64-3.0.23-r1/lib/libvlc.5.dylib").path
        let state = AnalysisProbeState(), started = Date()
        let results = try SkipAudioAnalysis.analyze(first: a, second: b, runtimePath: runtime,
            progress: { state.progress($0) }, minimumMatchSeconds: 2.5, maxWindowSeconds: 6)
        guard results.contains(where: { $0.kind == "intro" && abs($0.firstStartSeconds - 1) < 0.15 && abs($0.firstEndSeconds - 5) < 0.15 && abs($0.secondStartSeconds - 2) < 0.15 }),
              results.contains(where: { $0.kind == "credits" && abs($0.firstStartSeconds - 8) < 0.15 && abs($0.firstEndSeconds - 11) < 0.15 }),
              state.values.first == 0, state.values.last == 1,
              zip(state.values, state.values.dropFirst()).allSatisfy({ $0.0 <= $0.1 }) else { fatalError("Native audio analysis/progress mismatch") }
        let before = AnalysisProbeState(); before.cancel()
        do { _ = try SkipAudioAnalysis.analyze(first: a, second: b, runtimePath: runtime, cancelled: { before.cancelled }); fatalError("Pre-cancel ignored") }
        catch is CancellationError { }
        let live = AnalysisProbeState(), cancelStarted = Date()
        do {
            _ = try SkipAudioAnalysis.analyze(first: a, second: b, runtimePath: runtime,
                progress: { if $0 > 0 { live.cancel() } }, cancelled: { live.cancelled }, minimumMatchSeconds: 2.5, maxWindowSeconds: 6)
            fatalError("Mid-analysis cancellation ignored")
        } catch is CancellationError { }
        guard Date().timeIntervalSince(cancelStarted) < 4 else { fatalError("Native cancellation was not responsive") }
        let record: [String: Any] = ["passed": true, "elapsedSeconds": Date().timeIntervalSince(started), "cancellationSeconds": Date().timeIntervalSince(cancelStarted),
            "progressEvents": state.values.count, "scope": "Authored varied PCM WAVs; approved runtime, no physical speaker or model claim",
            "candidates": results.map { ["kind": $0.kind, "firstStartSeconds": $0.firstStartSeconds, "firstEndSeconds": $0.firstEndSeconds, "secondStartSeconds": $0.secondStartSeconds, "secondEndSeconds": $0.secondEndSeconds, "similarity": $0.similarity] }]
        try JSONSerialization.data(withJSONObject: record, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("skip-audio-analysis-native.json"))
        print("PASS: actual native audio fingerprints found shifted intro/credits, monotonic progress, and responsive pre/mid cancellation")
    }
}
