import AppKit
import Foundation

@main struct VLCPlaybackSmoke {
    enum Failure: Error { case check(String) }
    @MainActor static func main() {
        setbuf(stdout, nil)
        NSApplication.shared.setActivationPolicy(.accessory)
        Task { @MainActor in
            do { try await run(); exit(0) } catch { print("FAIL: \(error)"); exit(1) }
        }
        NSApplication.shared.run()
    }
    @MainActor static func run() async throws {
        let root = URL(fileURLWithPath: CommandLine.arguments[1])
        let window = NSWindow(contentRect: NSRect(x: 80, y: 80, width: 640, height: 360), styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false; window.orderFront(nil)
        let host = NSView(frame: window.contentView!.bounds); window.contentView = host
        let engine = PlaybackEngine(); engine.setVolume(0); engine.attachVideoView(host)
        defer { engine.shutdown(); window.close() }
        for name in ["Luma-Demo.mp4", "Luma-HEVC10-AAC51.mp4", "Luma-AVC-AAC.mkv"] {
            engine.load(root.appendingPathComponent(name))
            try await wait(engine) { engine.isPlaying && engine.duration > 1 && (engine.vlcStatistics?.displayedVideo ?? 0) > 0 }
            if name.contains("AAC51") { try await wait(engine) { (engine.vlcStatistics?.decodedAudio ?? 0) > 0 } }
            if name == "Luma-Demo.mp4" {
                let drawable = host.subviews.first
                let replacement = NSView(frame: host.bounds); window.contentView = replacement
                engine.attachVideoView(replacement)
                guard drawable != nil, replacement.subviews.first === drawable else { throw Failure.check("Host replacement recreated the native canvas") }
                engine.addSubtitle(root.appendingPathComponent("Luma-Demo.srt"))
                try await wait(engine) { engine.subtitleTracks.contains(where: { $0.id >= 0 }) && engine.selectedSubtitleTrack >= 0 }
                engine.setSubtitleDelay(0.5)
                guard abs(engine.actualSubtitleDelay - 0.5) < 0.001 else { throw Failure.check("Native subtitle timing was not applied") }
                engine.setSubtitleTrack(-1); guard engine.selectedSubtitleTrack == -1 else { throw Failure.check("Native subtitles off") }
                print("PASS: native canvas retained across host replacement; actual libVLC external subtitle parsing/selection/delay/off")
            }
            engine.pause(); try await wait(engine) { engine.state == "paused" }
            engine.seek(to: min(0.7, engine.duration / 2)); engine.setRate(1.25)
            try await wait(engine) { abs(engine.currentTime - min(0.7, engine.duration / 2)) < 0.3 }
            guard abs(engine.actualPlaybackRate - 1.25) < 0.001 else { throw Failure.check("Native rate mismatch") }
            engine.togglePause(); try await wait(engine) { engine.isPlaying }
            print("PASS: actual libVLC decoded and displayed video, pause/seek/native rate/resume: \(name); stats=\(String(describing: engine.vlcStatistics))")
            engine.setRate(1)
        }
        engine.stop(); guard engine.state == "stopped", !engine.isPlaying else { throw Failure.check("Stop state") }
        engine.unload(); guard !engine.hasMedia else { throw Failure.check("Unload state") }
        if ProcessInfo.processInfo.environment["LUMA_REQUIRE_BUNDLED_RUNTIME"] == "1" {
            guard let root = Bundle.main.resourcePath, engine.vlcRuntimePath?.hasPrefix(root + "/libvlc-runtime/") == true else { throw Failure.check("App used an external fallback instead of its bundled runtime") }
            print("PASS: self-contained app bundle loaded its own approved libVLC runtime, without an installed VLC fallback")
        }
        print("PASS: libVLC production engine stop/unload; runtime \(engine.vlcRuntimePath ?? "unknown")")
        print("SCOPE: synthetic tested files only, not all VLC formats or physical speaker output")
    }
    @MainActor static func wait(_ engine: PlaybackEngine, _ check: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(15)
        while !check() {
            if let error = engine.errorMessage { throw Failure.check(error) }
            guard Date() < deadline else { throw Failure.check("Timed out, state=\(engine.state), stats=\(String(describing: engine.vlcStatistics))") }
            try await Task.sleep(for: .milliseconds(50))
        }
    }
}
