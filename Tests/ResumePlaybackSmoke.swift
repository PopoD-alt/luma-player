import AppKit
import Foundation
import SwiftUI

/// Uses a temporary library, authored fixture, the production authenticated
/// listener and the actual native playback clock. Never opens personal media.
@main struct ResumePlaybackSmoke {
    enum Failure: Error { case check(String) }
    @MainActor static func main() {
        setbuf(stdout, nil); NSApplication.shared.setActivationPolicy(.accessory)
        Task { @MainActor in do { try await run(); exit(0) } catch { print("FAIL: \(error)"); exit(1) } }
        NSApplication.shared.run()
    }
    @MainActor static func run() async throws {
        let fixture = URL(fileURLWithPath: CommandLine.arguments[1])
        let output = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LumaResumeNative-\(UUID())")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        let secondURL = folder.appendingPathComponent("A night of cinema.mkv")
        try FileManager.default.copyItem(at: fixture, to: secondURL)
        let library = LibraryStore(storageURL: folder.appendingPathComponent("library.json"))
        let entries = library.add(urls: [fixture, secondURL]), first = entries[0], stale = entries[1]
        library.updateProgress(id: first.id, time: 1, duration: 12.25)
        let suite = "dev.sdas.luma.resume-test.\(UUID())"
        let preferences = UserDefaults(suiteName: suite)!
        let player = PlayerCoordinator(library: library, preferences: preferences)
        let hosting = NSHostingView(rootView: PlayerView(player: player).preferredColorScheme(.dark))
        let window = NSWindow(contentRect: NSRect(x: 120, y: 100, width: 1280, height: 800), styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.title = "Luma — isolated playback handoff review"; window.isReleasedWhenClosed = false
        window.contentView = hosting; window.orderFront(nil)
        defer { player.shutdown(); window.close(); preferences.removePersistentDomain(forName: suite); try? FileManager.default.removeItem(at: folder) }
        player.engine.setVolume(0); player.startSharing()
        try await wait("production sharing listener", player) { player.sharing.port > 0 }
        let base = URL(string: "http://127.0.0.1:\(player.sharing.port)")!
        let configuration = URLSessionConfiguration.ephemeral; configuration.timeoutIntervalForRequest = 5
        let session = URLSession(configuration: configuration)
        defer { session.invalidateAndCancel() }
        func post(_ path: String, _ body: [String: Any], token: String? = nil) async throws -> Data {
            var request = URLRequest(url: base.appendingPathComponent(path)); request.httpMethod = "POST"
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            if let token { request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization") }
            let (data, response) = try await session.data(for: request)
            try check((response as? HTTPURLResponse)?.statusCode == 200, "Authenticated test request failed")
            return data
        }
        let paired = try await post("v1/pair", ["code": player.sharing.pairingCode])
        guard let token = (try JSONSerialization.jsonObject(with: paired) as? [String: Any])?["token"] as? String else { throw Failure.check("No test token") }
        func phone(_ position: Double, completed: Bool = false) async throws {
            _ = try await post("v1/progress", ["itemId": stale.id.uuidString.lowercased(), "positionSeconds": position,
                "durationSeconds": 12.25, "completed": completed], token: token)
        }
        try await phone(5.5)
        try check(library.selectedID == stale.id && !player.engine.hasMedia && player.engine.state == "idle", "Idle phone activity must update Continue Watching without autoplay")
        library.flush()
        let restored = LibraryStore(storageURL: library.storageURL)
        try check(restored.selectedID == stale.id && ResumePolicy.preferred(for: restored.selectedItem!).startPosition == 5.5, "Fresh launch lost latest phone activity")
        try await Task.sleep(for: .milliseconds(250)); hosting.layoutSubtreeIfNeeded()
        try capture(hosting, output.appendingPathComponent("Luma-Mac-continue-watching.png"))

        player.play(stale)
        try await wait("new load reaches actual phone checkpoint", player) {
            let native = player.engine.actualNativePlaybackTime ?? -1
            return player.engine.isPlaying && native >= 5.3 && native < 7.5 && (player.engine.vlcStatistics?.displayedVideo ?? 0) > 0
        }
        player.engine.pause()
        try await wait("native paused after handoff", player) { player.engine.state == "paused" }
        try await Task.sleep(for: .milliseconds(550))
        let native = player.engine.actualNativePlaybackTime ?? -1
        let revision = player.engine.userSeekRevision
        try await phone(1.2)
        try await Task.sleep(for: .milliseconds(750))
        try check(abs((player.engine.actualNativePlaybackTime ?? -99) - native) < 0.2
                  && player.engine.userSeekRevision == revision && library.selectedID == stale.id,
                  "Incoming phone metadata moved the already loaded player")
        let host = findHost(hosting), canvas = host?.subviews.first
        player.toggle()
        try await wait("normal Play on paused computer resumes phone checkpoint", player) {
            let time = player.engine.actualNativePlaybackTime ?? -1
            return player.engine.isPlaying && time >= 1.1 && time < 3
        }
        try check(host?.subviews.first === canvas, "Paused handoff replaced the native canvas")
        player.engine.pause(); try await wait("pause before fresh load", player) { player.engine.state == "paused" }
        try await Task.sleep(for: .milliseconds(550))
        try await phone(2.6)
        player.engine.unload(); player.play(stale)
        try await wait("stale captured entry loads fresh checkpoint", player) {
            let time = player.engine.actualNativePlaybackTime ?? -1
            return player.engine.isPlaying && time >= 2.5 && time < 4 && (player.engine.vlcStatistics?.displayedVideo ?? 0) > 0
        }
        player.engine.pause(); try await wait("pause before explicit phone zero", player) { player.engine.state == "paused" }
        try await phone(0)
        let beforeExplicit = player.engine.userSeekRevision
        player.resumeFromPhone(stale)
        try await wait("explicit phone zero overrides newer computer resume", player) {
            let time = player.engine.actualNativePlaybackTime ?? -1
            return time >= 0 && time < 0.7 && player.engine.userSeekRevision > beforeExplicit
        }
        player.engine.pause(); try await wait("pause before manual local seek", player) { player.engine.state == "paused" }
        player.engine.seek(to: 3)
        try await wait("manual paused local position", player) { abs((player.engine.actualNativePlaybackTime ?? -1) - 3) < 0.2 }
        try await Task.sleep(for: .milliseconds(600))
        try await phone(9)
        player.toggle()
        try await wait("manual paused seek pins next normal Play", player) {
            let time = player.engine.actualNativePlaybackTime ?? -1
            return player.engine.isPlaying && time >= 2.9 && time < 4.5
        }
        try await phone(0.5)
        try await Task.sleep(for: .milliseconds(650))
        try check((player.engine.actualNativePlaybackTime ?? -1) > 3 && (player.engine.actualNativePlaybackTime ?? 99) < 5,
                  "Incoming metadata moved the already-playing native clock")
        player.engine.unload()
        player.jumpToBookmark(MediaBookmark(time: 7, label: "0:07"), in: stale)
        try await wait("bookmark overrides cross-device resume", player) {
            let time = player.engine.actualNativePlaybackTime ?? -1
            return player.engine.isPlaying && time >= 6.8 && time < 8.5
        }
        player.engine.unload()
        // A reset is an explicit choice even against metadata retained by an
        // older captured row; it must not be reinterpreted as a phone handoff.
        try check(library.updateMobileProgress(id: stale.id, time: 9, duration: 12.25, completed: false,
            updatedAt: Date().timeIntervalSince1970 + 5), "Test checkpoint rejected")
        player.resetProgress(stale); player.play(stale)
        try await wait("explicit reset starts at actual native zero", player) {
            let time = player.engine.actualNativePlaybackTime ?? -1
            return player.engine.isPlaying && time >= 0 && time < 1
        }
        print("PASS: authenticated phone checkpoint selects idle Continue Watching without autoplay; saved startup chooses phone activity; actual libVLC new load resumes5.5s and stale row resumes2.6s; paused and playing clocks unchanged by remote updates; normal Play handoff resumes1.2s on same canvas; manual paused seek pins next Play; explicit phone zero/bookmark7s/reset0 preserved")
    }
    @MainActor static func findHost(_ root: NSView) -> NSView? {
        if root.identifier?.rawValue == "LumaVideoHost" { return root }
        return root.subviews.lazy.compactMap { findHost($0) }.first
    }
    @MainActor static func capture(_ view: NSView, _ url: URL) throws {
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { throw Failure.check("Review bitmap unavailable") }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let data = bitmap.representation(using: .png, properties: [:]) else { throw Failure.check("PNG unavailable") }
        try data.write(to: url)
    }
    @MainActor static func wait(_ name: String, _ player: PlayerCoordinator, timeout: Double = 15, _ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !predicate() {
            if let error = player.engine.errorMessage { throw Failure.check(error) }
            guard Date() < deadline else { throw Failure.check("Timeout \(name): state\(player.engine.state), native\(String(describing: player.engine.actualNativePlaybackTime)), UI\(player.engine.currentTime)") }
            try await Task.sleep(for: .milliseconds(40))
        }
    }
    static func check(_ condition: @autoclosure () -> Bool, _ text: String) throws { if !condition() { throw Failure.check(text) } }
}
