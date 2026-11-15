import AppKit
import Foundation
import SwiftUI

/// Runs the production SwiftUI hierarchy and coordinator in an owned test window.
/// Uses a temporary library and synthetic media; no user's saved queue is opened.
@main
struct FullscreenSmoke {
    enum Failure: Error, CustomStringConvertible {
        case check(String)
        var description: String { switch self { case .check(let text): return text } }
    }

    @MainActor
    static func main() {
        setbuf(stdout, nil)
        let app = NSApplication.shared
        app.setActivationPolicy(.regular)
        Task { @MainActor in
            do {
                try await run()
                exit(0)
            } catch {
                print("FAIL: \(error)")
                exit(1)
            }
        }
        // Native key-window and fullscreen transitions require AppKit's actual
        // event pump, rather than just async main's dispatch run loop.
        app.run()
    }

    @MainActor
    static func run() async throws {
        guard CommandLine.arguments.count == 2 else {
            throw Failure.check("Usage: fullscreen-smoke /absolute/path/Luma-Demo.mp4")
        }
        let fixture = URL(fileURLWithPath: CommandLine.arguments[1])
        guard FileManager.default.fileExists(atPath: fixture.path) else { throw Failure.check("Missing synthetic video fixture") }
        let temporaryDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("LumaFullscreenSmoke-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporaryDirectory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporaryDirectory) }

        let app = NSApplication.shared
        let library = LibraryStore(storageURL: temporaryDirectory.appendingPathComponent("library.json"))
        let coordinator = PlayerCoordinator(library: library)
        coordinator.showInspector = true
        let hosting = NSHostingView(rootView: PlayerView(player: coordinator).preferredColorScheme(.dark))
        let window = NSWindow(
            contentRect: NSRect(x: 100, y: 100, width: 1280, height: 800),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered, defer: false
        )
        window.title = "Luma Player — Fullscreen integration test"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.collectionBehavior = [.fullScreenPrimary]
        window.isReleasedWhenClosed = false
        window.contentView = hosting
        window.makeKeyAndOrderFront(nil)
        app.activate(ignoringOtherApps: true)
        defer {
            coordinator.shutdown()
            window.close()
        }
        try await wait("owned test window becomes key") { window.isKeyWindow }
        try await wait("production video host") { findVideoHost(in: hosting) != nil }
        guard let originalHost = findVideoHost(in: hosting) else { throw Failure.check("Missing production video host") }
        coordinator.engine.setVolume(0)
        coordinator.importFiles([fixture])
        try await wait("production engine starts with embedded video") {
            coordinator.engine.isPlaying && !originalHost.subviews.isEmpty
        }
        window.makeFirstResponder(originalHost)
        postSpace(window)
        try await wait("Space pauses production playback") { coordinator.engine.state == "paused" && !coordinator.engine.isPlaying }
        postSpace(window)
        try await wait("Space resumes production playback") { coordinator.engine.isPlaying }
        let editor = NSTextField(frame: NSRect(x: 20, y: 20, width: 180, height: 26))
        editor.stringValue = "hello"; hosting.addSubview(editor); window.makeFirstResponder(editor)
        if let fieldEditor = window.firstResponder as? NSTextView { fieldEditor.setSelectedRange(NSRange(location: 5, length: 0)) }
        postSpace(window)
        try await wait("Space remains available to text input") { editor.stringValue == "hello " }
        try check(coordinator.engine.isPlaying, "Typing a space paused playback")
        window.makeFirstResponder(originalHost); editor.removeFromSuperview()
        print("PASS: real queued Space key events pause/resume production playback; editable text receives spaces without toggling")
        coordinator.engine.pause()
        try await wait("video pauses before fullscreen transition") { coordinator.engine.state == "paused" }
        hosting.layoutSubtreeIfNeeded()
        let initialBounds = originalHost.convert(originalHost.bounds, to: hosting)
        try check(initialBounds.width < hosting.bounds.width - 300, "Normal view does not expose queue and inspector: \(initialBounds), content \(hosting.bounds)")
        try check(initialBounds.height < hosting.bounds.height - 100, "Normal view does not reserve controls/header: \(initialBounds)")
        print("PASS: production playback embedded at \(initialBounds), content \(hosting.bounds)")

        coordinator.toggleVideoFullscreen()
        try await wait("native fullscreen entry", timeout: 12) {
            window.styleMask.contains(.fullScreen) && coordinator.isVideoFullscreen
        }
        try await wait("video fills the fullscreen content", timeout: 12) {
            hosting.layoutSubtreeIfNeeded()
            guard let host = findVideoHost(in: hosting) else { return false }
            return rectMatches(host.convert(host.bounds, to: hosting), hosting.bounds)
        }
        guard let fullscreenHost = findVideoHost(in: hosting) else { throw Failure.check("Video host disappeared entering fullscreen") }
        try check(fullscreenHost === originalHost, "Fullscreen replaced the video drawable NSView")
        try check(!fullscreenHost.subviews.isEmpty, "Fullscreen lost embedded output")
        // AppKit suppresses input during the native space transition even after
        // the layout has reached its destination rectangle.
        try await Task.sleep(for: .milliseconds(900))
        try await wait("fullscreen window accepts keyboard input") { window.isKeyWindow }
        window.makeFirstResponder(fullscreenHost); postSpace(window)
        try await wait("Space resumes in video fullscreen") { coordinator.engine.isPlaying }
        postSpace(window)
        try await wait("Space pauses in video fullscreen") { coordinator.engine.state == "paused" }
        print("PASS: native fullscreen, video fills \(hosting.bounds), same drawable NSView and video output retained")

        // The fullscreen animation has completed before toggling back.
        coordinator.toggleVideoFullscreen()
        try await wait("native fullscreen exit", timeout: 12) {
            !window.styleMask.contains(.fullScreen) && !coordinator.isVideoFullscreen
        }
        try await wait("normal margins and chrome return", timeout: 12) {
            hosting.layoutSubtreeIfNeeded()
            guard let host = findVideoHost(in: hosting) else { return false }
            let restored = host.convert(host.bounds, to: hosting)
            return restored.width < hosting.bounds.width - 300 && restored.height < hosting.bounds.height - 100
        }
        guard let restoredHost = findVideoHost(in: hosting) else { throw Failure.check("Video host disappeared leaving fullscreen") }
        try check(restoredHost === originalHost, "Leaving fullscreen replaced the video drawable NSView")
        try check(!restoredHost.subviews.isEmpty, "Leaving fullscreen lost video output")
        coordinator.engine.togglePause()
        try await wait("production video resumes after fullscreen") { coordinator.engine.isPlaying }
        print("PASS: exited fullscreen, queue/inspector/control margins restored, same video NSView, playback resumed")
        if let directory = ProcessInfo.processInfo.environment["LUMA_PREVIEW_DIRECTORY"] {
            coordinator.engine.unload()
            library.clear()
            coordinator.showInspector = false
            let output = URL(fileURLWithPath: directory, isDirectory: true)
            try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
            for dark in [false, true] {
                let name = dark ? "dark" : "light"
                window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                hosting.rootView = PlayerView(player: coordinator).preferredColorScheme(dark ? .dark : .light)
                try await Task.sleep(for: .milliseconds(600))
                hosting.layoutSubtreeIfNeeded()
                guard let bitmap = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else { throw Failure.check("Preview bitmap allocation failed") }
                hosting.cacheDisplay(in: hosting.bounds, to: bitmap)
                guard let data = bitmap.representation(using: .png, properties: [:]) else { throw Failure.check("Preview PNG export failed") }
                try data.write(to: output.appendingPathComponent("Luma-\(name).png"))
                print("Rendered native \(name) appearance preview")
            }
        }

    }

    @MainActor
    static func findVideoHost(in root: NSView) -> NSView? {
        if root.identifier?.rawValue == "LumaVideoHost" { return root }
        for child in root.subviews {
            if let match = findVideoHost(in: child) { return match }
        }
        return nil
    }

    static func rectMatches(_ lhs: NSRect, _ rhs: NSRect) -> Bool {
        abs(lhs.minX - rhs.minX) < 2 && abs(lhs.minY - rhs.minY) < 2 && abs(lhs.width - rhs.width) < 2 && abs(lhs.height - rhs.height) < 2
    }
    @MainActor static func postSpace(_ window: NSWindow) {
        let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
            windowNumber: window.windowNumber, context: nil, characters: " ", charactersIgnoringModifiers: " ", isARepeat: false, keyCode: 49)!
        NSApp.postEvent(event, atStart: false)
    }

    @MainActor
    static func wait(_ description: String, timeout: TimeInterval = 8, until condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() >= deadline { throw Failure.check("Timed out waiting for \(description)") }
            try await Task.sleep(for: .milliseconds(100))
        }
    }

    static func check(_ condition: @autoclosure () -> Bool, _ message: String) throws {
        if !condition() { throw Failure.check(message) }
    }
}
