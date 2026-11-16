import Foundation
import Darwin

/// Optional local repeated-audio analysis. Runs on a worker; never the UI/native playback graph.
/// The existing libVLC runtime decodes only bounded head/tail windows into spectral fingerprints.
/// All results require user confirmation; this is not a trained AI classifier.
enum SkipAudioAnalysis {
    enum Failure: LocalizedError {
        case unavailable, busy, invalidFiles, invalidOptions, duration, decode, timing, changed, timeout
        var errorDescription: String? {
            switch self {
            case .unavailable: return "The current playback runtime cannot analyze audio."
            case .busy: return "Another audio analysis is already running."
            case .invalidFiles: return "Choose two different local episodes from the same series."
            case .invalidOptions: return "The requested analysis limits are invalid."
            case .duration: return "Both episodes need a known, finite duration and an audio track."
            case .decode: return "Audio analysis could not decode a complete window."
            case .timing: return "This audio window has gaps or incomplete timing; no skip timings were accepted."
            case .changed: return "A selected file changed during analysis. Try again with the current files."
            case .timeout: return "Audio analysis exceeded its time limit."
            }
        }
    }
    private static let jobLock = NSLock()
    private struct FileStamp: Equatable {
        var size: UInt64, inode: UInt64, modified: Date
        init(_ url: URL) throws {
            guard url.isFileURL else { throw Failure.invalidFiles }
            guard let values = try? FileManager.default.attributesOfItem(atPath: url.path) else { throw Failure.invalidFiles }
            guard values[.type] as? FileAttributeType == .typeRegular,
                  let size = values[.size] as? NSNumber, let inode = values[.systemFileNumber] as? NSNumber,
                  let modified = values[.modificationDate] as? Date else { throw Failure.invalidFiles }
            self.size = size.uint64Value; self.inode = inode.uint64Value; self.modified = modified
        }
    }
    static func analyze(first: URL, second: URL, runtimePath: String,
                        progress: @Sendable (Double) -> Void = { _ in },
                        cancelled: @Sendable () -> Bool = { false },
                        minimumMatchSeconds: Double = 15, maxWindowSeconds: Double = 180) throws -> [SkipAudioCandidate] {
        guard minimumMatchSeconds.isFinite, maxWindowSeconds.isFinite, minimumMatchSeconds >= 1,
              maxWindowSeconds >= 2, maxWindowSeconds <= 180, minimumMatchSeconds <= maxWindowSeconds else { throw Failure.invalidOptions }
        if cancelled() { throw CancellationError() }
        guard jobLock.try() else { throw Failure.busy }; defer { jobLock.unlock() }
        let a = first.standardizedFileURL.resolvingSymlinksInPath(), b = second.standardizedFileURL.resolvingSymlinksInPath()
        guard a != b, a.isFileURL, b.isFileURL, runtimePath.hasPrefix("/") else { throw Failure.invalidFiles }
        let firstStamp = try FileStamp(a), secondStamp = try FileStamp(b)
        let started = ProcessInfo.processInfo.systemUptime
        func check() throws {
            if cancelled() { throw CancellationError() }
            if ProcessInfo.processInfo.systemUptime - started > 1200 { throw Failure.timeout }
        }
        try check(); progress(0)
        let decoder = try Decoder(runtimePath: runtimePath); defer { decoder.close() }
        let firstDuration = try decoder.duration(a, check: check), secondDuration = try decoder.duration(b, check: check)
        let firstWindow = min(maxWindowSeconds, firstDuration / 2), secondWindow = min(maxWindowSeconds, secondDuration / 2)
        guard min(firstWindow, secondWindow) >= minimumMatchSeconds else { throw Failure.duration }
        let firstHead = try decoder.window(a, start: 0, seconds: firstWindow, check: check) { progress(min(0.24, $0 * 0.24)) }
        let secondHead = try decoder.window(b, start: 0, seconds: secondWindow, check: check) { progress(0.24 + min(0.24, $0 * 0.24)) }
        let firstTailStart = firstDuration - firstWindow, secondTailStart = secondDuration - secondWindow
        let firstTail = try decoder.window(a, start: firstTailStart, seconds: firstWindow, check: check) { progress(0.48 + min(0.24, $0 * 0.24)) }
        let secondTail = try decoder.window(b, start: secondTailStart, seconds: secondWindow, check: check) { progress(0.72 + min(0.24, $0 * 0.24)) }
        try check()
        var result = try RepeatedAudioMatcher.match(first: firstHead, second: secondHead, kind: "intro", firstOffset: 0, secondOffset: 0, minimumSeconds: minimumMatchSeconds, cancelled: cancelled)
        result += try RepeatedAudioMatcher.match(first: firstTail, second: secondTail, kind: "credits", firstOffset: firstTailStart, secondOffset: secondTailStart, minimumSeconds: minimumMatchSeconds, cancelled: cancelled)
        try check()
        guard try FileStamp(a) == firstStamp, try FileStamp(b) == secondStamp else { throw Failure.changed }
        progress(1); return result
    }

    private typealias Pointer = UnsafeMutableRawPointer
    private typealias New = @convention(c) (Int32, UnsafePointer<UnsafePointer<CChar>?>?) -> Pointer?
    private typealias Release = @convention(c) (Pointer?) -> Void
    private typealias NewMedia = @convention(c) (Pointer?, UnsafePointer<CChar>?) -> Pointer?
    private typealias NewPlayer = @convention(c) (Pointer?) -> Pointer?
    private typealias GetInt = @convention(c) (Pointer?) -> Int32
    private typealias GetTime = @convention(c) (Pointer?) -> Int64
    private typealias AddOption = @convention(c) (Pointer?, UnsafePointer<CChar>?) -> Void
    private typealias Parse = @convention(c) (Pointer?, UInt32, Int32) -> Int32
    private typealias SetCallbacks = @convention(c) (Pointer?, Pointer?, Pointer?, Pointer?, Pointer?, Pointer?, Pointer?) -> Void
    private typealias SetFormat = @convention(c) (Pointer?, UnsafePointer<CChar>?, UInt32, UInt32) -> Void
    private typealias PlayCallback = @convention(c) (Pointer?, UnsafeRawPointer?, UInt32, Int64) -> Void
    private typealias GetVersion = @convention(c) () -> UnsafePointer<CChar>?

    private final class Capture {
        let lock = NSLock()
        var fingerprint: RepeatedAudioFingerprint
        var sampleCount = 0, invalid = false
        var lastPTS: Int64?, lastCount = 0
        let maximumSamples: Int
        init(seconds: Double) {
            fingerprint = RepeatedAudioFingerprint(maximumSeconds: seconds)
            maximumSamples = Int(ceil(seconds * 8000)) + 16000
        }
        func receive(_ samples: UnsafeRawPointer?, count: UInt32, pts: Int64) {
            lock.lock(); defer { lock.unlock() }
            guard !invalid, let samples, count <= 32000, sampleCount + Int(count) <= maximumSamples else { invalid = true; return }
            if let lastPTS {
                let expected = lastPTS + Int64(lastCount) * 125
                if abs(Double(pts) - Double(expected)) > 250000 { invalid = true; return }
            }
            lastPTS = pts; lastCount = Int(count); sampleCount += Int(count)
            fingerprint.append(UnsafeBufferPointer(start: samples.assumingMemoryBound(to: Int16.self), count: Int(count)))
        }
        func status() -> (Int, Bool) { lock.lock(); defer { lock.unlock() }; return (sampleCount, invalid) }
        func result(seconds: Double) throws -> RepeatedAudioFingerprint {
            lock.lock(); defer { lock.unlock() }
            guard !invalid, abs(Double(sampleCount) / 8000 - seconds) <= 0.5 else { throw Failure.timing }
            return fingerprint
        }
    }
    private static let playCallback: PlayCallback = { opaque, samples, count, pts in
        guard let opaque else { return }
        Unmanaged<Capture>.fromOpaque(opaque).takeUnretainedValue().receive(samples, count: count, pts: pts)
    }
    private final class Decoder {
        private var library: Pointer?, core: Pointer?, instance: Pointer?
        private let releaseInstance: Release, releaseMedia: Release, releasePlayer: Release, stop: Release, parseStop: Release
        private let newMedia: NewMedia, newPlayer: NewPlayer, addOption: AddOption, parse: Parse
        private let parsed: GetInt, mediaDuration: GetTime, play: GetInt, state: GetInt
        private let callbacks: SetCallbacks, format: SetFormat
        init(runtimePath: String) throws {
            let corePath = URL(fileURLWithPath: runtimePath).deletingLastPathComponent().appendingPathComponent("libvlccore.dylib").path
            let loadedCore = dlopen(corePath, RTLD_NOW | RTLD_GLOBAL)
            guard let handle = dlopen(runtimePath, RTLD_NOW | RTLD_LOCAL) else { if let loadedCore { dlclose(loadedCore) }; throw Failure.unavailable }
            func symbol<T>(_ name: String, _ type: T.Type) throws -> T {
                guard let address = dlsym(handle, name) else { throw Failure.unavailable }
                return unsafeBitCast(address, to: T.self)
            }
            do {
                let version = try symbol("libvlc_get_version", GetVersion.self)().map(String.init(cString:)) ?? ""
                guard version.hasPrefix("3.") else { throw Failure.unavailable }
                let create = try symbol("libvlc_new", New.self)
                releaseInstance = try symbol("libvlc_release", Release.self); releaseMedia = try symbol("libvlc_media_release", Release.self)
                releasePlayer = try symbol("libvlc_media_player_release", Release.self); stop = try symbol("libvlc_media_player_stop", Release.self)
                newMedia = try symbol("libvlc_media_new_path", NewMedia.self); newPlayer = try symbol("libvlc_media_player_new_from_media", NewPlayer.self)
                addOption = try symbol("libvlc_media_add_option", AddOption.self); parse = try symbol("libvlc_media_parse_with_options", Parse.self)
                parseStop = try symbol("libvlc_media_parse_stop", Release.self); parsed = try symbol("libvlc_media_get_parsed_status", GetInt.self)
                mediaDuration = try symbol("libvlc_media_get_duration", GetTime.self); play = try symbol("libvlc_media_player_play", GetInt.self)
                state = try symbol("libvlc_media_player_get_state", GetInt.self); callbacks = try symbol("libvlc_audio_set_callbacks", SetCallbacks.self)
                format = try symbol("libvlc_audio_set_format", SetFormat.self)
                let options = ["--ignore-config", "--no-video", "--no-lua", "--no-metadata-network-access", "--no-sub-autodetect-file", "--quiet"]
                let strings = options.map { strdup($0) }; defer { strings.forEach { free($0) } }
                let args = strings.map { $0.map { UnsafePointer<CChar>($0) } }
                guard let created = args.withUnsafeBufferPointer({ create(Int32(args.count), $0.baseAddress) }) else { throw Failure.unavailable }
                library = handle; core = loadedCore; instance = created
            } catch { dlclose(handle); if let loadedCore { dlclose(loadedCore) }; throw error }
        }
        func close() {
            if let instance { releaseInstance(instance); self.instance = nil }
            if let library { dlclose(library); self.library = nil }
            if let core { dlclose(core); self.core = nil }
        }
        func media(_ url: URL) throws -> Pointer {
            guard let media = url.path.withCString({ newMedia(instance, $0) }) else { throw Failure.decode }
            return media
        }
        func duration(_ url: URL, check: () throws -> Void) throws -> Double {
            let media = try media(url); defer { parseStop(media); releaseMedia(media) }
            guard parse(media, 0, 10000) == 0 else { throw Failure.duration }
            let deadline = ProcessInfo.processInfo.systemUptime + 12
            while parsed(media) == 0 {
                try check(); if ProcessInfo.processInfo.systemUptime > deadline { throw Failure.timeout }; Thread.sleep(forTimeInterval: 0.05)
            }
            let length = Double(mediaDuration(media)) / 1000
            guard parsed(media) == 4, length.isFinite, length > 0, length <= 31536000 else { throw Failure.duration }
            return length
        }
        func window(_ url: URL, start: Double, seconds: Double, check: () throws -> Void, progress: (Double) -> Void) throws -> RepeatedAudioFingerprint {
            try check(); let media = try media(url); defer { releaseMedia(media) }
            for option in [String(format: ":start-time=%.6f", locale: Locale(identifier: "en_US_POSIX"), start), String(format: ":stop-time=%.6f", locale: Locale(identifier: "en_US_POSIX"), start + seconds)] {
                option.withCString { addOption(media, $0) }
            }
            guard let player = newPlayer(media) else { throw Failure.decode }
            let capture = Capture(seconds: seconds)
            // Keep exactly this callback context alive through native stop and release.
            let context = Unmanaged.passRetained(capture)
            defer { stop(player); releasePlayer(player); context.release() }
            callbacks(player, unsafeBitCast(SkipAudioAnalysis.playCallback, to: Pointer.self), nil, nil, nil, nil, context.toOpaque())
            "S16N".withCString { format(player, $0, 8000, 1) }
            guard play(player) == 0 else { throw Failure.decode }
            let deadline = ProcessInfo.processInfo.systemUptime + seconds + 20
            while true {
                try check(); let observed = state(player), status = capture.status()
                if status.1 { throw Failure.timing }
                progress(min(1, Double(status.0) / 8000 / seconds))
                if observed == 6 { break }; if observed == 7 { throw Failure.decode }
                if ProcessInfo.processInfo.systemUptime > deadline { throw Failure.timeout }
                Thread.sleep(forTimeInterval: 0.05)
            }
            return try capture.result(seconds: seconds)
        }
    }
}
