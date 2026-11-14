import AppKit
import Foundation

@main struct EngineSmoke {
    @MainActor static func main() {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        Task { @MainActor in
            do { try await run(); exit(0) }
            catch { print("FAIL: \(error)"); exit(1) }
        }
        app.run()
    }
    @MainActor static func run() async throws {
        let engine = PlaybackEngine()
        defer { engine.shutdown() }
        engine.setVolume(0)
        engine.load(URL(fileURLWithPath: CommandLine.arguments[1]))
        try await wait { ["buffering", "playing"].contains(engine.state) }
        engine.setRate(1.25)
        precondition(abs(engine.player.rate - 1.25) < 0.001)
        try await wait { engine.duration > 2 && engine.isPlaying }
        print("Decoder: \(engine.backendName)")
        precondition(engine.isSeekable)
        engine.pause()
        precondition(!engine.isPlaying && engine.state == "paused")
        engine.seek(to: 1.5)
        try await wait { abs(engine.currentTime - 1.5) < 0.25 }
        engine.setRate(1.5); precondition(engine.rate == 1.5)
        engine.setVolume(37); precondition(engine.volume == 37); engine.setVolume(0)
        engine.togglePause()
        try await wait { engine.isPlaying }
        engine.stop(); precondition(!engine.isPlaying && engine.state == "stopped")
        engine.unload(); precondition(!engine.hasMedia && engine.duration == 0)
        print("PASS: original audio playback, pause/seek/rate/volume/resume/stop/unload")
    }
    @MainActor static func wait(_ condition: () -> Bool) async throws {
        for _ in 0..<100 { if condition() { return }; try await Task.sleep(for: .milliseconds(100)) }
        throw NSError(domain: "EngineSmokeTimeout", code: 1)
    }
}
