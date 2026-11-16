import AppKit
import Foundation
import SwiftUI

@main struct SkipPlaybackSmoke {
    enum Failure: Error { case check(String) }
    @MainActor static func main() {
        setbuf(stdout, nil); NSApplication.shared.setActivationPolicy(.accessory)
        Task { @MainActor in do { try await run(); exit(0) } catch { print("FAIL: \(error)"); exit(1) } }
        NSApplication.shared.run()
    }
    @MainActor static func run() async throws {
        let fixture = URL(fileURLWithPath: CommandLine.arguments[1])
        let output = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("LumaSkipNative-\(UUID())")
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        let suiteName = "dev.sdas.luma.skip-test.\(UUID())"
        let preferences = UserDefaults(suiteName: suiteName)!
        let library = LibraryStore(storageURL: temporary.appendingPathComponent("library.json"))
        let coordinator = PlayerCoordinator(library: library, preferences: preferences)
        let hosting = NSHostingView(rootView: PlayerView(player: coordinator).preferredColorScheme(.dark))
        let window = NSWindow(contentRect: NSRect(x: 120, y: 100, width: 1280, height: 800), styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.title = "Luma — isolated auto-skip review"; window.isReleasedWhenClosed = false
        window.contentView = hosting; window.orderFront(nil)
        defer { coordinator.shutdown(); window.close(); preferences.removePersistentDomain(forName: suiteName); try? FileManager.default.removeItem(at: temporary) }
        coordinator.engine.setVolume(0)
        coordinator.importFiles([fixture])
        try await wait("native chapter metadata and displayed video", coordinator) {
            coordinator.engine.isPlaying && coordinator.engine.chapters.count == 5 && (coordinator.engine.vlcStatistics?.displayedVideo ?? 0) > 0 && coordinator.resolvedSkipRanges.count == 2
        }
        try check(!coordinator.autoSkipEnabled, "Auto-skip must default off")
        let intro = coordinator.resolvedSkipRanges[0], credits = coordinator.resolvedSkipRanges[1]
        try check(intro.startSeconds == 1 && intro.endSeconds == 3 && credits.startSeconds == 8 && credits.endSeconds == 10, "Actual native chapter boundaries mismatch: \(coordinator.engine.chapters)")
        guard let host = findHost(hosting), let canvas = host.subviews.first else { throw Failure.check("No native video canvas") }
        coordinator.engine.pause(); try await wait("native paused", coordinator) { coordinator.engine.state == "paused" }
        coordinator.engine.seek(to: 1.3)
        try await wait("native intro seek", coordinator) { abs((coordinator.engine.actualNativePlaybackTime ?? -1) - 1.3) < 0.15 && coordinator.currentSkipRange?.kind == "intro" }
        coordinator.autoSkipEnabled = true
        try await Task.sleep(for: .milliseconds(800))
        try check((coordinator.engine.actualNativePlaybackTime ?? 0) < 1.6 && coordinator.engine.state == "paused", "Enabling auto-skip moved paused playback")
        let introFrames = coordinator.engine.vlcStatistics?.displayedVideo ?? 0
        coordinator.engine.togglePause()
        try await wait("actual native intro automatic seek", coordinator) { (coordinator.engine.actualNativePlaybackTime ?? 0) >= 3.2 && coordinator.engine.isPlaying && (coordinator.engine.vlcStatistics?.displayedVideo ?? 0) > introFrames }
        try check(host.subviews.first === canvas, "Auto-skip recreated native video")
        let advanced = coordinator.engine.actualNativePlaybackTime ?? 0
        coordinator.engine.pause(); try await wait("pause after intro", coordinator) { coordinator.engine.state == "paused" }
        coordinator.engine.seek(to: 1.4)
        try await wait("deliberate backward seek", coordinator) { abs((coordinator.engine.actualNativePlaybackTime ?? -1) - 1.4) < 0.2 }
        coordinator.engine.togglePause()
        try await Task.sleep(for: .milliseconds(700))
        try check((coordinator.engine.actualNativePlaybackTime ?? 99) < 3, "Auto-skip overrode a deliberate backward seek")
        coordinator.engine.pause(); try await wait("pause for explicit button", coordinator) { coordinator.engine.state == "paused" && coordinator.currentSkipRange?.kind == "intro" }
        try await wait("old notice fades", coordinator) { coordinator.notice == nil }
        hosting.layoutSubtreeIfNeeded()
        try capture(hosting, output.appendingPathComponent("Luma-Mac-auto-skip.png"))
        coordinator.skipCurrentSegment()
        try await wait("explicit skip while paused", coordinator) { (coordinator.engine.actualNativePlaybackTime ?? 0) >= 2.9 && coordinator.engine.state == "paused" }
        // Reload starts a new playthrough; moving deliberately to just before a
        // segment keeps the skip unconsumed so crossing its start exercises auto.
        guard let item = library.selectedItem else { throw Failure.check("Missing selected item") }
        coordinator.play(item)
        try await wait("reloaded native graph ready", coordinator) { coordinator.engine.isPlaying && coordinator.engine.duration > 10.5 && coordinator.engine.chapters.count == 5 }
        coordinator.engine.pause(); try await wait("pause before credits", coordinator) { coordinator.engine.state == "paused" }
        coordinator.engine.seek(to: 7.3)
        try await wait("native precredit seek", coordinator) { abs((coordinator.engine.actualNativePlaybackTime ?? -1) - 7.3) < 0.2 }
        print("TRACE precredits: state\(coordinator.engine.state) native\(String(describing: coordinator.engine.actualNativePlaybackTime)) ui\(coordinator.engine.currentTime) revision\(coordinator.engine.userSeekRevision) notice\(coordinator.notice ?? "")")
        let creditFrames = coordinator.engine.vlcStatistics?.displayedVideo ?? 0
        coordinator.engine.togglePause()
        try await wait("actual native credits skipped", coordinator) { (coordinator.engine.actualNativePlaybackTime ?? 0) >= 10.2 && coordinator.engine.isPlaying && (coordinator.engine.vlcStatistics?.displayedVideo ?? 0) > creditFrames }
        coordinator.engine.pause(); try await wait("postcredit pause", coordinator) { coordinator.engine.state == "paused" }
        let post = coordinator.engine.actualNativePlaybackTime ?? 0
        try check(post >= 10 && post < 11.8 && coordinator.currentSkipRange == nil && host.subviews.first === canvas, "Postcredit chapter lost or native video replaced")
        try check(preferences.bool(forKey: "autoSkipEnabled"), "Opt-in preference not persisted")
        // Use real positions to mark a manual opening, then confirm a new store.
        coordinator.autoSkipEnabled = false
        coordinator.engine.seek(to: 0.5); try await wait("manual range start native", coordinator) { abs((coordinator.engine.actualNativePlaybackTime ?? -1) - 0.5) < 0.2 }
        coordinator.markSkipStart(kind: "intro")
        coordinator.engine.seek(to: 2.5); try await wait("manual range end native", coordinator) { abs((coordinator.engine.actualNativePlaybackTime ?? -1) - 2.5) < 0.2 }
        coordinator.markSkipEnd(); library.flush()
        let saved = LibraryStore(storageURL: library.storageURL).items[0].skipRanges
        try check(saved?.count == 1 && saved?[0].source == "manual" && abs((saved?[0].startSeconds ?? 0) - 0.5) < 0.2 && abs((saved?[0].endSeconds ?? 0) - 2.5) < 0.2, "Marked range did not persist")
        print("PASS: actual libVLC chapters Opening1–3/Credits8–10; auto intro jumped to native\(advanced)s; auto credits reached native\(post)s with postcredits preserved; same native canvas, paused guard, deliberate seek override, explicit skip and persistent manual timings/toggle")
        let a = temporary.appendingPathComponent("Episode-One.wav"), b = temporary.appendingPathComponent("Episode-Two.wav")
        try wav([(1, 10), (4, 123), (3, 20), (3, 456), (1, 30)], to: a)
        try wav([(2, 11), (4, 123), (2, 21), (3, 456), (1, 31)], to: b)
        let episodes = library.add(urls: [a, b]); coordinator.play(episodes[0])
        try await wait("native unmarked episode ready", coordinator) { coordinator.engine.isPlaying && coordinator.engine.duration > 11.5 }
        coordinator.engine.pause(); try await wait("pause before analysis", coordinator) { coordinator.engine.state == "paused" }
        coordinator.analyzeSkipAudioFixture()
        try await wait("real local audio analysis candidates", coordinator, timeout: 45) { coordinator.skipAnalysisProgress == nil && coordinator.skipAudioCandidates.count >= 2 }
        try check(library.items.first(where: { $0.id == episodes[0].id })?.skipRanges == nil && library.items.first(where: { $0.id == episodes[1].id })?.skipRanges == nil, "Analysis auto-applied timings before confirmation")
        coordinator.confirmSkipAudioCandidates()
        try check(library.items.first(where: { $0.id == episodes[0].id })?.skipRanges == nil, "Unselected candidates were implicitly confirmed")
        if let introCandidate = coordinator.skipAudioCandidates.first(where: { $0.kind == "intro" }), let creditCandidate = coordinator.skipAudioCandidates.first(where: { $0.kind == "credits" }) {
            coordinator.selectSkipAudioCandidate(introCandidate, selected: true); coordinator.selectSkipAudioCandidate(creditCandidate, selected: true)
        } else { throw Failure.check("Expected intro and credits matches") }
        try await Task.sleep(for: .milliseconds(250))
        if let sheet = window.attachedSheet?.contentView { sheet.layoutSubtreeIfNeeded(); try capture(sheet, output.appendingPathComponent("Luma-Mac-audio-match-review.png")) }
        coordinator.confirmSkipAudioCandidates()
        let confirmed = LibraryStore(storageURL: library.storageURL)
        for episode in episodes {
            let savedRanges = confirmed.items.first(where: { $0.id == episode.id })?.skipRanges ?? []
            try check(savedRanges.count == 2 && savedRanges.allSatisfy { $0.source == "manual" && $0.label == "Confirmed audio match" }, "Confirmed candidates did not persist atomically for both videos")
        }
        try check(!coordinator.autoSkipEnabled && coordinator.engine.state == "paused", "Analysis changed playback or enabled automatic skipping")
        coordinator.analyzeSkipAudioFixture(); coordinator.cancelSkipAnalysis()
        try await Task.sleep(for: .milliseconds(300))
        try check(coordinator.skipAnalysisProgress == nil && coordinator.skipAudioCandidates.isEmpty, "Cancelled analysis revived stale UI results")
        print("PASS: production local-analysis UI worker found real shifted intro/credits in authored unmarked audio; no implicit acceptance; explicit two-kind selection and atomic two-file confirmation persisted; playback stayed paused and cancellation invalidated stale callbacks")
        print("SCOPE: chapter/manual plus original repeated-audio fingerprinting, not a trained AI model. Analysis fixture uses2.5s minimum/6s windows; production15s minimum/180s windows. No physical speaker claim.")
    }
    static func wav(_ parts: [(Double, Int)], to url: URL) throws {
        let samples: [Int16] = parts.flatMap { seconds, seed in
            (0..<Int(seconds * 8000)).map { i in
                let t = Double(i) / 8000, note = Int(t * 8)
                let f = Double(130 + (note * 37 + seed * 13) % 800), mix = 0.15 + 0.35 * Double((seed + note * 17) % 13) / 12
                return Int16(((sin(2 * Double.pi * f * t) + mix * sin(2 * Double.pi * f * 2.37 * t + Double(seed))) * 7000).rounded())
            }
        }
        var data = Data()
        func text(_ text: String) { data.append(Data(text.utf8)) }
        func number<T: FixedWidthInteger>(_ value: T) { var little = value.littleEndian; withUnsafeBytes(of: &little) { data.append(contentsOf: $0) } }
        text("RIFF"); number(UInt32(36 + samples.count * 2)); text("WAVEfmt "); number(UInt32(16)); number(UInt16(1)); number(UInt16(1))
        number(UInt32(8000)); number(UInt32(16000)); number(UInt16(2)); number(UInt16(16)); text("data"); number(UInt32(samples.count * 2))
        for value in samples { number(value) }; try data.write(to: url)
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
    @MainActor static func wait(_ name: String, _ coordinator: PlayerCoordinator, timeout: Double = 15, _ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        var traceAt = Date.distantPast
        while !predicate() {
            if name.contains("credits"), Date().timeIntervalSince(traceAt) > 0.5 {
                traceAt = Date(); print("TRACE \(name): state\(coordinator.engine.state) native\(String(describing: coordinator.engine.actualNativePlaybackTime)) ui\(coordinator.engine.currentTime) frames\(coordinator.engine.vlcStatistics?.displayedVideo ?? 0) range\(coordinator.currentSkipRange?.kind ?? "none") notice\(coordinator.notice ?? "")")
            }
            if let error = coordinator.engine.errorMessage { throw Failure.check(error) }
            guard Date() < deadline else { throw Failure.check("Timeout \(name): state\(coordinator.engine.state), time\(String(describing: coordinator.engine.actualNativePlaybackTime)), chapters\(coordinator.engine.chapters)") }
            try await Task.sleep(for: .milliseconds(50))
        }
    }
    static func check(_ condition: @autoclosure () -> Bool, _ message: String) throws { if !condition() { throw Failure.check(message) } }
}
