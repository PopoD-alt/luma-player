import AppKit
import Darwin

/// The stable libVLC 3 C ABI, dynamically loaded so an interface-compatible
/// library can be replaced independently. No VLC implementation is copied here.
/// Bundled runtimes are supplied only through the separate dependency audit gate.
@MainActor final class VLCBridge {
    enum Failure: LocalizedError {
        case message(String)
        var errorDescription: String? { if case .message(let text) = self { return text }; return nil }
    }
    typealias Pointer = UnsafeMutableRawPointer
    typealias New = @convention(c) (Int32, UnsafePointer<UnsafePointer<CChar>?>?) -> Pointer?
    typealias NewPlayer = @convention(c) (Pointer?) -> Pointer?
    typealias Release = @convention(c) (Pointer?) -> Void
    typealias NewMedia = @convention(c) (Pointer?, UnsafePointer<CChar>?) -> Pointer?
    typealias SetMedia = @convention(c) (Pointer?, Pointer?) -> Void
    typealias GetInt = @convention(c) (Pointer?) -> Int32
    typealias SetInt = @convention(c) (Pointer?, Int32) -> Int32
    typealias SetPause = @convention(c) (Pointer?, Int32) -> Void
    typealias GetTime = @convention(c) (Pointer?) -> Int64
    typealias SetTime = @convention(c) (Pointer?, Int64) -> Void
    typealias GetFloat = @convention(c) (Pointer?) -> Float
    typealias SetFloat = @convention(c) (Pointer?, Float) -> Int32
    typealias GetVersion = @convention(c) () -> UnsafePointer<CChar>?
    typealias GetTracks = @convention(c) (Pointer?) -> Pointer?
    typealias AddSlave = @convention(c) (Pointer?, Int32, UnsafePointer<CChar>?, Bool) -> Int32
    typealias GetStats = @convention(c) (Pointer?, Pointer?) -> Int32
    typealias Snapshot = @convention(c) (Pointer?, UInt32, UnsafePointer<CChar>?, UInt32, UInt32) -> Int32

    private let library: Pointer
    private let instance: Pointer
    private let mediaPlayer: Pointer
    private var media: Pointer?
    private var symbols: [String: Pointer] = [:]
    private(set) var version: String
    let runtimePath: String
    private var closed = false
    private let releaseInstance: Release
    private let releasePlayer: Release

    init() throws {
        let environment = ProcessInfo.processInfo.environment
        var candidates: [String] = []
        if let override = environment["LUMA_LIBVLC_PATH"], override.hasPrefix("/") { candidates.append(override) }
        if let frameworks = Bundle.main.privateFrameworksPath {
            candidates += [frameworks + "/libvlc-runtime/VLCKit.framework/VLCKit", frameworks + "/libvlc-runtime/lib/libvlc.5.dylib", frameworks + "/libvlc-runtime/libvlc.5.dylib", frameworks + "/VLCKit.framework/VLCKit", frameworks + "/libvlc/lib/libvlc.5.dylib", frameworks + "/libvlc/libvlc.5.dylib"]
        }
        if let resources = Bundle.main.resourcePath { candidates += [resources + "/libvlc-runtime/VLCKit.framework/VLCKit", resources + "/libvlc-runtime/lib/libvlc.5.dylib", resources + "/libvlc-runtime/libvlc.5.dylib"] }
        // A separately installed VLC is useful for local review. Its modules
        // are not copied into Luma or assumed to be LGPL-only redistributable.
        candidates += ["/Applications/VLC.app/Contents/MacOS/lib/libvlc.5.dylib",
                       FileManager.default.homeDirectoryForCurrentUser.path + "/Applications/VLC.app/Contents/MacOS/lib/libvlc.5.dylib"]
        var loaded: (String, Pointer)?; var loadError: String?
        for candidate in candidates where FileManager.default.fileExists(atPath: candidate) {
            let core = URL(fileURLWithPath: candidate).deletingLastPathComponent().appendingPathComponent("libvlccore.dylib").path
            if FileManager.default.fileExists(atPath: core) { _ = dlopen(core, RTLD_NOW | RTLD_GLOBAL) }
            if let handle = dlopen(candidate, RTLD_NOW | RTLD_LOCAL) { loaded = (candidate, handle); break }
            loadError = dlerror().map { String(cString: $0) }
        }
        guard let (path, handle) = loaded else {
            throw Failure.message("Luma needs a loadable libVLC 3 runtime. Install VLC in Applications, or configure an audited Luma runtime." + (loadError.map { " \($0)" } ?? ""))
        }
        func resolve<T>(_ name: String, as: T.Type) throws -> T {
            guard let address = dlsym(handle, name) else { throw Failure.message("The libVLC runtime is missing \(name).") }
            return unsafeBitCast(address, to: T.self)
        }
        do {
            let getVersion = try resolve("libvlc_get_version", as: GetVersion.self)
            let runtimeVersion = getVersion().map(String.init(cString:)) ?? "unknown"
            guard runtimeVersion.hasPrefix("3.") else { throw Failure.message("This Luma build requires the libVLC 3 ABI; found \(runtimeVersion).") }
            let create = try resolve("libvlc_new", as: New.self)
            let createPlayer = try resolve("libvlc_media_player_new", as: NewPlayer.self)
            let freeInstance = try resolve("libvlc_release", as: Release.self)
            let freePlayer = try resolve("libvlc_media_player_release", as: Release.self)
            // Never enable a web interface, recursive media discovery or
            // embedded Lua/network-controlled scripts from an opened file.
            let options = ["--ignore-config", "--no-video-title-show", "--no-osd", "--no-lua", "--no-metadata-network-access", "--no-sub-autodetect-file", "--no-snapshot-preview", "--quiet"]
            let strings = options.map { strdup($0) }
            defer { strings.forEach { free($0) } }
            let arguments = strings.map { $0.map { UnsafePointer<CChar>($0) } }
            guard let engine = arguments.withUnsafeBufferPointer({ create(Int32($0.count), $0.baseAddress) }) else {
                throw Failure.message("libVLC could not initialize its playback plugins.")
            }
            guard let player = createPlayer(engine) else { freeInstance(engine); throw Failure.message("libVLC could not create a media player.") }
            library = handle; instance = engine; mediaPlayer = player; version = runtimeVersion; runtimePath = path
            releaseInstance = freeInstance; releasePlayer = freePlayer
        } catch { dlclose(handle); throw error }
    }
    private func function<T>(_ name: String, _ type: T.Type) -> T {
        let address: Pointer
        if let cached = symbols[name] { address = cached }
        else {
            // All symbols used below are part of the required libVLC 3 ABI.
            guard let symbol = dlsym(library, name) else { preconditionFailure("Missing libVLC 3 symbol: \(name)") }
            symbols[name] = symbol; address = symbol
        }
        return unsafeBitCast(address, to: T.self)
    }
    func attach(_ view: NSView?) {
        guard !closed else { return }
        function("libvlc_media_player_set_nsobject", SetMedia.self)(mediaPlayer, view.map { Unmanaged.passUnretained($0).toOpaque() })
        // Native video subviews must leave the app's keyboard/mouse shortcuts
        // with AppKit rather than processing libVLC's own fullscreen bindings.
        function("libvlc_video_set_key_input", SetPause.self)(mediaPlayer, 0)
        function("libvlc_video_set_mouse_input", SetPause.self)(mediaPlayer, 0)
    }
    func load(_ url: URL) throws {
        guard !closed else { throw Failure.message("The media engine has shut down.") }
        stop()
        if let media { function("libvlc_media_release", Release.self)(media); self.media = nil }
        let create = function(url.isFileURL ? "libvlc_media_new_path" : "libvlc_media_new_location", NewMedia.self)
        guard let next = (url.isFileURL ? url.path : url.absoluteString).withCString({ create(instance, $0) }) else {
            throw Failure.message("libVLC could not open this media location.")
        }
        media = next
        function("libvlc_media_player_set_media", SetMedia.self)(mediaPlayer, next)
        guard function("libvlc_media_player_play", GetInt.self)(mediaPlayer) == 0 else { throw Failure.message("libVLC could not start playback.") }
    }
    func play() { guard !closed else { return }; _ = function("libvlc_media_player_play", GetInt.self)(mediaPlayer); function("libvlc_media_player_set_pause", SetPause.self)(mediaPlayer, 0) }
    func pause() { guard !closed else { return }; function("libvlc_media_player_set_pause", SetPause.self)(mediaPlayer, 1) }
    func stop() { guard !closed else { return }; function("libvlc_media_player_stop", Release.self)(mediaPlayer) }
    var state: Int32 { closed ? 0 : function("libvlc_media_player_get_state", GetInt.self)(mediaPlayer) }
    var time: Double { closed ? 0 : max(0, Double(function("libvlc_media_player_get_time", GetTime.self)(mediaPlayer)) / 1000) }
    var duration: Double { closed ? 0 : max(0, Double(function("libvlc_media_player_get_length", GetTime.self)(mediaPlayer)) / 1000) }
    var seekable: Bool { !closed && function("libvlc_media_player_is_seekable", GetInt.self)(mediaPlayer) != 0 }
    var playing: Bool { !closed && function("libvlc_media_player_is_playing", GetInt.self)(mediaPlayer) != 0 }
    var actualRate: Double { closed ? 0 : Double(function("libvlc_media_player_get_rate", GetFloat.self)(mediaPlayer)) }
    func seek(_ seconds: Double) { guard !closed, seconds.isFinite else { return }; function("libvlc_media_player_set_time", SetTime.self)(mediaPlayer, Int64(min(Double(Int64.max / 2000), max(0, seconds)) * 1000)) }
    func volume(_ percent: Double) { guard !closed else { return }; _ = function("libvlc_audio_set_volume", SetInt.self)(mediaPlayer, Int32(percent)) }
    func rate(_ rate: Double) { guard !closed else { return }; _ = function("libvlc_media_player_set_rate", SetFloat.self)(mediaPlayer, Float(rate)) }
    var selectedAudio: Int32 { closed ? -1 : function("libvlc_audio_get_track", GetInt.self)(mediaPlayer) }
    var selectedSubtitle: Int32 { closed ? -1 : function("libvlc_video_get_spu", GetInt.self)(mediaPlayer) }
    func audio(_ id: Int32) { guard !closed else { return }; _ = function("libvlc_audio_set_track", SetInt.self)(mediaPlayer, id) }
    func subtitle(_ id: Int32) { guard !closed else { return }; _ = function("libvlc_video_set_spu", SetInt.self)(mediaPlayer, id) }
    func subtitleDelay(_ seconds: Double) { guard !closed else { return }; _ = function("libvlc_video_set_spu_delay", (@convention(c) (Pointer?, Int64) -> Int32).self)(mediaPlayer, Int64(seconds * 1_000_000)) }
    var actualSubtitleDelay: Double { closed ? 0 : Double(function("libvlc_video_get_spu_delay", GetTime.self)(mediaPlayer)) / 1_000_000 }
    func addSubtitle(_ url: URL) -> Bool {
        !closed && url.absoluteString.withCString { function("libvlc_media_player_add_slave", AddSlave.self)(mediaPlayer, 0, $0, true) == 0 }
    }
    func tracks(audio: Bool) -> [PlaybackTrack] {
        guard !closed else { return [] }
        let list = function(audio ? "libvlc_audio_get_track_description" : "libvlc_video_get_spu_description", GetTracks.self)(mediaPlayer)
        defer { if let list { function("libvlc_track_description_list_release", Release.self)(list) } }
        var node = list; var result: [PlaybackTrack] = []; var count = 0
        while let current = node, count < 256 {
            let id = current.load(as: Int32.self)
            let name = current.load(fromByteOffset: 8, as: UnsafePointer<CChar>?.self).map(String.init(cString:)) ?? "Track \(id)"
            result.append(PlaybackTrack(id: id, name: name))
            node = current.load(fromByteOffset: 16, as: Pointer?.self); count += 1
        }
        return result
    }
    var statistics: (decodedVideo: Int32, displayedVideo: Int32, decodedAudio: Int32)? {
        guard !closed, let media else { return nil }
        let bytes = Pointer.allocate(byteCount: 60, alignment: 4); bytes.initializeMemory(as: UInt8.self, repeating: 0, count: 60)
        defer { bytes.deallocate() }
        guard function("libvlc_media_get_stats", GetStats.self)(media, bytes) != 0 else { return nil }
        return (bytes.load(fromByteOffset: 24, as: Int32.self), bytes.load(fromByteOffset: 32, as: Int32.self), bytes.load(fromByteOffset: 28, as: Int32.self))
    }
    func snapshot(to url: URL) -> Bool {
        !closed && url.path.withCString { function("libvlc_video_take_snapshot", Snapshot.self)(mediaPlayer, 0, $0, 0, 0) == 0 }
    }
    func unload() {
        guard !closed else { return }; stop()
        function("libvlc_media_player_set_media", SetMedia.self)(mediaPlayer, nil)
        if let media { function("libvlc_media_release", Release.self)(media); self.media = nil }
    }
    func shutdown() {
        guard !closed else { return }; unload(); attach(nil); closed = true
        releasePlayer(mediaPlayer); releaseInstance(instance)
        // macOS video plugins install Objective-C classes. Keep the library
        // mapped for process lifetime; dlclose after plugin use is unsafe.
    }
}
