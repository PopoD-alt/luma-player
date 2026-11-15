import AppKit
import SwiftUI
import Combine
import UniformTypeIdentifiers

private let ember = Color.accentColor
private let panel = Color(nsColor: .controlBackgroundColor)

// Keep the window opaque; glass surfaces blend within a stable native base.
struct NativeWindowMaterial: NSViewRepresentable {
    final class Backdrop: NSVisualEffectView {
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            window?.isOpaque = true
            window?.backgroundColor = .windowBackgroundColor
        }
    }
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = Backdrop()
        view.material = .windowBackground
        view.blendingMode = .withinWindow
        view.state = .followsWindowActiveState
        return view
    }
    func updateNSView(_ view: NSVisualEffectView, context: Context) {}
}

private struct GlassSurface: ViewModifier {
    let cornerRadius: CGFloat
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    func body(content: Content) -> some View {
        Group {
            if reduceTransparency {
                content.background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            } else if #available(macOS 26.0, *) {
                content.foregroundStyle(colorScheme == .dark ? Color.white : Color.black).glassEffect(.regular, in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            } else {
                content.background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            }
        }
            .overlay {
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(LinearGradient(colors: [.white.opacity(0.5), .white.opacity(0.08)], startPoint: .topLeading, endPoint: .bottomTrailing), lineWidth: 0.75)
                    .allowsHitTesting(false)
            }
            .shadow(color: .black.opacity(0.065), radius: 14, x: 0, y: 7)
    }
}

private extension View {
    func glassSurface(cornerRadius: CGFloat) -> some View { modifier(GlassSurface(cornerRadius: cornerRadius)) }
}

func clockLabel(_ time: Double) -> String {
    guard time.isFinite, time > 0 else { return "0:00" }
    let value = Int(time)
    return value >= 3600 ? String(format: "%d:%02d:%02d", value / 3600, value / 60 % 60, value % 60) : String(format: "%d:%02d", value / 60, value % 60)
}

@MainActor final class PlayerCoordinator: ObservableObject {
    static let shared = PlayerCoordinator()
    let engine: PlaybackEngine
    let library: LibraryStore
    let sharing = LocalSharingServer()
    @Published var showSharing = false
    @Published var showInspector = false
    @Published var showShortcuts = false
    @Published var showFormatSupport = false
    @Published var isVideoFullscreen = false
    @Published var loopMode = 0
    @Published var notice: String?
    private var timer: AnyCancellable?
    private var sharingLibraryObserver: AnyCancellable?
    private var pendingResume: Double?
    private var handledEnd = false
    private var noticeDeadline: Date = .distantPast
    private var fullscreenObservers: [NSObjectProtocol] = []

    init(library: LibraryStore? = nil) {
        self.engine = PlaybackEngine()
        self.library = library ?? LibraryStore()
        sharingLibraryObserver = self.library.$items.dropFirst().sink { [weak self] _ in
            // Published emits before the store is changed; validate on the next
            // main-actor turn so removed items cancel even a paused TCP send.
            Task { @MainActor [weak self] in self?.sharing.playlistDidChange() }
        }
        for (name, fullscreen) in [(NSWindow.didEnterFullScreenNotification, true), (NSWindow.didExitFullScreenNotification, false)] {
            fullscreenObservers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.isVideoFullscreen = fullscreen }
            })
        }
        timer = Timer.publish(every: 0.5, on: .main, in: .common).autoconnect().sink { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
    }

    func tell(_ message: String) {
        notice = message
        noticeDeadline = Date().addingTimeInterval(3)
    }

    func openFiles() {
        let picker = NSOpenPanel()
        picker.title = "Add media to Luma"
        picker.prompt = "Add to queue"
        picker.allowsMultipleSelection = true
        picker.canChooseDirectories = false
        picker.allowedContentTypes = LibraryStore.supportedExtensions.compactMap { UTType(filenameExtension: $0) }
        if picker.runModal() == .OK { importFiles(picker.urls) }
    }

    func playBundledDemo(_ name: String, extension suffix: String) {
        guard let url = Bundle.main.url(forResource: name, withExtension: suffix) else { return }
        library.add(urls: [url])
        if let item = library.items.first(where: { $0.url.standardizedFileURL == url.standardizedFileURL }) { play(item); pendingResume = nil }
    }
    func playBitmapSubtitleDemo() {
        playBundledDemo("Luma-PGS", extension: "mkv")
        let selection = library.selectedID
        Task { @MainActor [weak self] in
            for _ in 0..<200 {
                guard let self, self.library.selectedID == selection, self.engine.hasMedia, self.engine.errorMessage == nil else { return }
                if let track = self.engine.subtitleTracks.first(where: { $0.id >= 3_000_000 }) {
                    self.engine.setSubtitleTrack(track.id); return
                }
                try? await Task.sleep(for: .milliseconds(100))
            }
        }
    }

    func importFiles(_ urls: [URL]) {
        let media = urls.filter { LibraryStore.supportedExtensions.contains($0.pathExtension.lowercased()) }
        guard !media.isEmpty else { tell("Choose a video or audio file"); return }
        let added = library.add(urls: media)
        if !engine.hasMedia || engine.state == "error", let first = added.first ?? library.items.first { play(first) }
        else { tell("Added \(added.count) \(added.count == 1 ? "file" : "files") to queue") }
    }

    func play(_ item: MediaItem) {
        guard FileManager.default.fileExists(atPath: item.url.path) else { tell("This file has moved or is unavailable"); return }
        saveProgress()
        library.select(item.id)
        pendingResume = item.resumeTime > 3 ? item.resumeTime : nil
        handledEnd = false
        engine.load(item.url)
    }

    func saveProgress() {
        if let id = library.selectedID, engine.hasMedia, pendingResume == nil,
           engine.duration > 0, ["playing", "paused", "ended"].contains(engine.state) {
            library.updateProgress(id: id, time: engine.currentTime, duration: engine.duration)
        }
        library.flush()
    }

    private func tick() {
        if notice != nil, Date() > noticeDeadline { notice = nil }
        if let resume = pendingResume, engine.isSeekable, engine.duration > 0, engine.isPlaying {
            pendingResume = nil
            engine.seek(to: min(resume, max(0, engine.duration - 1)))
            tell("Resumed at \(clockLabel(resume))")
        }
        if let id = library.selectedID, engine.hasMedia, pendingResume == nil, engine.duration > 0, engine.state != "opening" {
            library.updateProgress(id: id, time: engine.currentTime, duration: engine.duration)
        }
        if engine.state == "ended", !handledEnd {
            handledEnd = true
            if let id = library.selectedID { library.resetProgress(id: id) }
            if loopMode == 1, let item = library.selectedItem { play(item) }
            else { next(wrap: loopMode == 2) }
        }
    }

    func next(wrap: Bool = false) {
        guard !library.items.isEmpty else { return }
        let index = library.items.firstIndex { $0.id == library.selectedID } ?? -1
        if index + 1 < library.items.count { play(library.items[index + 1]) }
        else if wrap { play(library.items[0]) }
    }

    func previous() {
        if engine.currentTime > 3 { engine.seek(to: 0); return }
        guard let index = library.items.firstIndex(where: { $0.id == library.selectedID }), index > 0 else { return }
        play(library.items[index - 1])
    }

    func toggle() {
        if engine.hasMedia, ["ended", "stopped"].contains(engine.state), let item = library.selectedItem { play(item) }
        else if engine.hasMedia { engine.togglePause() }
        else if let item = library.selectedItem ?? library.items.first { play(item) }
        else { openFiles() }
    }

    func bookmark() {
        guard let id = library.selectedID, engine.hasMedia else { return }
        library.addBookmark(id: id, time: engine.currentTime)
        showInspector = true
        tell("Saved moment at \(clockLabel(engine.currentTime))")
    }

    func jumpToBookmark(_ bookmark: MediaBookmark, in item: MediaItem) {
        if engine.hasMedia, library.selectedID == item.id, engine.isSeekable {
            engine.seek(to: bookmark.time)
        } else {
            play(item)
            pendingResume = bookmark.time
        }
    }

    func resetProgress(_ item: MediaItem) {
        if library.selectedID == item.id, engine.hasMedia {
            pendingResume = 0
            if engine.isSeekable { engine.seek(to: 0); pendingResume = nil }
        }
        library.resetProgress(id: item.id)
    }

    func remove(_ item: MediaItem) {
        if library.selectedID == item.id { engine.unload(); pendingResume = nil }
        library.remove(item.id)
    }

    func addSubtitle() {
        let picker = NSOpenPanel()
        picker.title = "Add subtitle file"
        picker.allowedContentTypes = ["srt", "vtt"].compactMap { UTType(filenameExtension: $0) }
        if picker.runModal() == .OK, let url = picker.url { engine.addSubtitle(url); tell("Added \(url.lastPathComponent)") }
    }

    func reveal(_ item: MediaItem) { NSWorkspace.shared.activateFileViewerSelecting([item.url]) }
    func toggleVideoFullscreen() {
        guard let window = NSApp.keyWindow ?? NSApp.mainWindow else { return }
        // Keep the embedded NSView in the same SwiftUI hierarchy while the
        // surrounding chrome disappears; the playback engine retains this view.
        isVideoFullscreen = !window.styleMask.contains(.fullScreen)
        window.toggleFullScreen(nil)
    }
    func startSharing() {
        sharing.start { [weak self] in
            guard let self else { return [] }
            return self.library.items.map { item in
                SharedMedia(id: item.id.uuidString.lowercased(), url: item.url, title: item.title,
                    durationSeconds: item.id == self.library.selectedID && self.engine.hasMedia ? self.engine.duration : item.duration,
                    resumeSeconds: item.id == self.library.selectedID && self.engine.hasMedia ? self.engine.currentTime : item.resumeTime)
            }
        }
    }
    func shutdown() { sharingLibraryObserver?.cancel(); sharing.stop(); saveProgress(); engine.shutdown() }
}

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        AppUpdater.shared.startIfConfigured(canPromptAutomatically: {
            let player = PlayerCoordinator.shared
            return !player.engine.isPlaying && !player.isVideoFullscreen && player.engine.state != "opening"
        })
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        let paths = CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") && FileManager.default.fileExists(atPath: $0) }
        if !paths.isEmpty {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                PlayerCoordinator.shared.importFiles(paths.map { URL(fileURLWithPath: $0) })
            }
        }
    }
    func application(_ application: NSApplication, open urls: [URL]) { PlayerCoordinator.shared.importFiles(urls) }
    func applicationWillTerminate(_ notification: Notification) {
        AppUpdater.shared.stop()
        PlayerCoordinator.shared.shutdown()
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

#if !LUMA_UI_TEST
@main struct LumaPlayerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    var body: some Scene {
        WindowGroup("Luma Player") { PlayerView(player: .shared) }
            .defaultSize(width: 1180, height: 760)
            .windowStyle(.hiddenTitleBar)
            .commands {
                CommandGroup(replacing: .appInfo) {
                    Button("About Luma Player") {
                        NSApp.orderFrontStandardAboutPanel(options: [
                            .credits: NSAttributedString(string: "Made by sdas\nFree to download\nPlayback powered by VideoLAN libVLC · Third-party components retain their licenses")
                        ])
                    }
                }
                CommandGroup(after: .appInfo) { CheckForUpdatesCommand() }
                CommandGroup(replacing: .newItem) {
                    Button("Open Media…") { PlayerCoordinator.shared.openFiles() }.keyboardShortcut("o")
                    Button("Share Playlist on Wi-Fi…") { PlayerCoordinator.shared.showSharing = true }
                }
                CommandMenu("Playback") {
                    Button("Play / Pause") { PlayerCoordinator.shared.toggle() }.keyboardShortcut(.space, modifiers: [])
                    Button("Back 10 Seconds") { let e = PlayerCoordinator.shared.engine; e.seek(to: e.currentTime - 10) }.keyboardShortcut(.leftArrow, modifiers: [])
                    Button("Forward 10 Seconds") { let e = PlayerCoordinator.shared.engine; e.seek(to: e.currentTime + 10) }.keyboardShortcut(.rightArrow, modifiers: [])
                    Divider()
                    Button("Previous File") { PlayerCoordinator.shared.previous() }.keyboardShortcut(.leftArrow, modifiers: .command)
                    Button("Next File") { PlayerCoordinator.shared.next() }.keyboardShortcut(.rightArrow, modifiers: .command)
                    Button("Save Moment") { PlayerCoordinator.shared.bookmark() }.keyboardShortcut("b")
                    Button("Add Subtitles…") { PlayerCoordinator.shared.addSubtitle() }.keyboardShortcut("s", modifiers: [.command, .shift])
                    Divider()
                    Button("Video Full Screen") { PlayerCoordinator.shared.toggleVideoFullscreen() }.keyboardShortcut("f", modifiers: [.command, .control])
                    Button("Exit Video Full Screen") {
                        if PlayerCoordinator.shared.isVideoFullscreen { PlayerCoordinator.shared.toggleVideoFullscreen() }
                    }.keyboardShortcut(.escape, modifiers: []).disabled(!PlayerCoordinator.shared.isVideoFullscreen)
                }
                CommandGroup(replacing: .help) {
                    Button("Format Support…") { PlayerCoordinator.shared.showFormatSupport = true }
                    Button("Play Sample Clip") {
                        PlayerCoordinator.shared.playBundledDemo("Luma-Demo", extension: "mp4")
                    }
                    Button("Play Original Video Decoder Demo") { PlayerCoordinator.shared.playBundledDemo("Luma-RLE8", extension: "avi") }
                    Button("Play Original Audio Decoder Demo") { PlayerCoordinator.shared.playBundledDemo("Luma-IMA", extension: "wav") }
                    Button("Play Original FLAC Decoder Demo") { PlayerCoordinator.shared.playBundledDemo("Luma-Silence", extension: "flac") }
                    Button("Play Original MJPEG Decoder Demo") { PlayerCoordinator.shared.playBundledDemo("Luma-MJPEG", extension: "avi") }
                    Button("Play Original MS Video 1 Demo") { PlayerCoordinator.shared.playBundledDemo("Luma-MSVideo1-16", extension: "avi") }
                    Button("Play Original MS ADPCM Demo") { PlayerCoordinator.shared.playBundledDemo("Luma-MSADPCM", extension: "wav") }
                    Button("Play Bitmap Subtitle Demo") { PlayerCoordinator.shared.playBitmapSubtitleDemo() }
                    Button("Play Matroska Compatibility Demo") { PlayerCoordinator.shared.playBundledDemo("Luma-AVC-AAC", extension: "mkv") }
                    Button("Keyboard Shortcuts") { PlayerCoordinator.shared.showShortcuts = true }.keyboardShortcut("/", modifiers: .command)
                }
            }
    }
}
#endif

@MainActor final class VideoHostView: NSView {
    var onDoubleClick: (() -> Void)?
    var onTogglePlayback: (() -> Void)?
    private var mouseMonitor: Any?
    private var keyboardMonitor: Any?
    override var acceptsFirstResponder: Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if window != nil, mouseMonitor == nil {
            // The playback canvas embeds its own AppKit subview. Monitor this window's
            // drawable area so a double-click reaches our fullscreen action
            // even when the decoder's view is the event recipient.
            mouseMonitor = NSEvent.addLocalMonitorForEvents(matching: .leftMouseDown) { [weak self] event in
                guard let self, let window = self.window, event.window === window,
                      self.bounds.contains(self.convert(event.locationInWindow, from: nil)) else { return event }
                window.makeFirstResponder(self)
                guard event.clickCount == 2 else { return event }
                self.onDoubleClick?()
                return nil
            }
            keyboardMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
                guard let self, let window = self.window, event.window === window, window.isKeyWindow,
                      event.keyCode == 49,
                      event.modifierFlags.intersection([.command, .control, .option, .shift]).isEmpty,
                      window.attachedSheet == nil, NSApp.modalWindow == nil,
                      !((window.firstResponder as? NSTextView)?.isEditable ?? false),
                      !((window.firstResponder as? NSTextField)?.isEditable ?? false) else { return event }
                if !event.isARepeat { self.onTogglePlayback?() }
                return nil
            }
        } else if window == nil, let mouseMonitor {
            NSEvent.removeMonitor(mouseMonitor)
            self.mouseMonitor = nil
            if let keyboardMonitor { NSEvent.removeMonitor(keyboardMonitor); self.keyboardMonitor = nil }
        }
    }

    deinit { if let mouseMonitor { NSEvent.removeMonitor(mouseMonitor) }; if let keyboardMonitor { NSEvent.removeMonitor(keyboardMonitor) } }
}

struct VideoSurface: NSViewRepresentable {
    let engine: PlaybackEngine
    let onDoubleClick: () -> Void
    let onTogglePlayback: () -> Void
    func makeNSView(context: Context) -> VideoHostView {
        let view = VideoHostView()
        view.identifier = NSUserInterfaceItemIdentifier("LumaVideoHost")
        view.onDoubleClick = onDoubleClick
        view.onTogglePlayback = onTogglePlayback
        view.wantsLayer = true
        view.layer?.backgroundColor = NSColor.black.cgColor
        engine.attachVideoView(view)
        return view
    }
    func updateNSView(_ nsView: VideoHostView, context: Context) { nsView.onDoubleClick = onDoubleClick; nsView.onTogglePlayback = onTogglePlayback }
}

struct GlyphButton: View {
    @Environment(\.colorScheme) private var colorScheme
    private var labelColor: Color { colorScheme == .dark ? .white : .black }
    let symbol: String
    let label: String
    var active = false
    var action: () -> Void
    var body: some View {
        Button(action: action) { Image(systemName: symbol).font(.system(size: 15, weight: .medium)).frame(width: 34, height: 34).contentShape(Rectangle()) }
            .buttonStyle(.plain).foregroundStyle(active ? ember : labelColor.opacity(0.75))
            .background(active ? ember.opacity(0.13) : labelColor.opacity(0.035), in: RoundedRectangle(cornerRadius: 10)).overlay(RoundedRectangle(cornerRadius: 10).stroke(.white.opacity(0.12), lineWidth: 0.5))
            .help(label).accessibilityLabel(label)
    }
}

@MainActor final class PlayerViewState: ObservableObject {
    @Published var isTargeted = false
    @Published var seekValue: Double = 0
    @Published var isSeeking = false
    @Published var showSidebar = true
}

struct PlayerView: View {
    @Environment(\.colorScheme) private var colorScheme
    private var labelColor: Color { colorScheme == .dark ? .white : .black }
    private var muted: Color { labelColor.opacity(0.55) }
    @ObservedObject private var player: PlayerCoordinator
    @ObservedObject private var engine: PlaybackEngine
    @ObservedObject private var library: LibraryStore
    @StateObject private var ui = PlayerViewState()

    init(player: PlayerCoordinator) {
        self.player = player
        self.engine = player.engine
        self.library = player.library
    }

    var body: some View {
        VStack(spacing: 0) {
            if !player.isVideoFullscreen {
                header
            }
            HStack(spacing: 0) {
                if ui.showSidebar && !player.isVideoFullscreen { sidebar.frame(width: 258) }
                VStack(spacing: 0) {
                    if !player.isVideoFullscreen { nowPlaying }
                    stage
                    if !player.isVideoFullscreen { controls }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
                if player.showInspector && !player.isVideoFullscreen { inspector.frame(width: 250) }
            }
            if !player.isVideoFullscreen { footer }
        }
        .background { if player.isVideoFullscreen { Color.black } else { ZStack { Color(nsColor: .windowBackgroundColor); NativeWindowMaterial(); LinearGradient(colors: [ember.opacity(0.055), Color.clear, Color.purple.opacity(0.035)], startPoint: .topLeading, endPoint: .bottomTrailing).allowsHitTesting(false) } } }.foregroundStyle(labelColor)
        .frame(minWidth: player.isVideoFullscreen ? 0 : player.showInspector && ui.showSidebar ? 1200 : 960, minHeight: player.isVideoFullscreen ? 0 : 600)
        .ignoresSafeArea(edges: player.isVideoFullscreen ? .all : [])
        .overlay { if ui.isTargeted { RoundedRectangle(cornerRadius: 12).stroke(ember, lineWidth: 3).padding(4).allowsHitTesting(false) } }
        .onDrop(of: [UTType.fileURL], isTargeted: $ui.isTargeted, perform: dropFiles)
        .sheet(isPresented: $player.showShortcuts) { shortcutSheet }
        .sheet(isPresented: $player.showFormatSupport) { CodecSupportView() }
        .sheet(isPresented: $player.showSharing) { LocalSharingView(player: player, server: player.sharing) }
        .onExitCommand { if player.isVideoFullscreen { player.toggleVideoFullscreen() } }
        .onChange(of: engine.currentTime) { _, value in if !ui.isSeeking { ui.seekValue = value } }
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(systemName: "play.circle.fill").font(.system(size: 23, weight: .regular)).symbolRenderingMode(.hierarchical).foregroundStyle(ember)
            VStack(alignment: .leading, spacing: 1) {
                Text("Luma").font(.system(size: 15, weight: .semibold))
                Text("Media Player").font(.system(size: 10)).foregroundStyle(muted)
            }
            Spacer()
            GlyphButton(symbol: "wifi", label: "Share playlist with Android on this network") { player.showSharing = true }
            Button(action: player.openFiles) {
                Label("Open Media", systemImage: "plus").font(.system(size: 12, weight: .medium)).padding(.horizontal, 15).padding(.vertical, 9)
            }.buttonStyle(.plain).glassSurface(cornerRadius: 18).help("Open media · ⌘O")
            Divider().frame(height: 20).padding(.horizontal, 4)
            GlyphButton(symbol: "sidebar.left", label: "Toggle queue", active: ui.showSidebar) { withAnimation(.easeInOut(duration: 0.18)) { ui.showSidebar.toggle() } }
            GlyphButton(symbol: "sidebar.right", label: "Media details and saved moments", active: player.showInspector) { withAnimation(.easeInOut(duration: 0.18)) { player.showInspector.toggle() } }
        }.padding(.leading, 82).padding(.trailing, 22).frame(height: 66)
            .background(Color(nsColor: .windowBackgroundColor).ignoresSafeArea(edges: .top))
            .overlay(alignment: .bottom) { Rectangle().fill(labelColor.opacity(0.08)).frame(height: 0.5) }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack { Text("Up Next").font(.system(size: 15, weight: .semibold)).foregroundStyle(muted); Spacer(); Text("\(library.items.count)").font(.system(size: 11, weight: .medium, design: .monospaced)).foregroundStyle(muted).padding(.horizontal, 7).padding(.vertical, 3).background(.white.opacity(0.05), in: Capsule()) }.padding(.horizontal, 20).padding(.top, 25).padding(.bottom, 17)
            HStack(spacing: 8) { Image(systemName: "magnifyingglass").foregroundStyle(muted); TextField("Search queue", text: $library.query).textFieldStyle(.plain).font(.system(size: 12)); if !library.query.isEmpty { Button { library.query = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(muted) }.buttonStyle(.plain) } }.padding(10).background(labelColor.opacity(0.045), in: RoundedRectangle(cornerRadius: 10)).padding(.horizontal, 14).padding(.bottom, 12)
            if library.items.isEmpty {
                VStack(alignment: .leading, spacing: 12) {
                    Image(systemName: "rectangle.stack.badge.plus").font(.system(size: 27, weight: .light)).foregroundStyle(muted)
                    Text("Your queue starts here").font(.system(size: 17, weight: .medium)).lineSpacing(3)
                    Text("Drop video or audio files here. Your queue and place are saved automatically.").font(.system(size: 12)).foregroundStyle(muted).lineSpacing(4)
                }.padding(24).padding(.top, 28)
                Spacer()
            } else {
                ScrollView {
                    LazyVStack(spacing: 5) { ForEach(library.filteredItems) { item in queueRow(item) } }.padding(.horizontal, 10).padding(.bottom, 16)
                    if library.filteredItems.isEmpty { Text("No matching files").font(.system(size: 12)).foregroundStyle(muted).padding(20) }
                }
            }
            Rectangle().fill(labelColor.opacity(0.06)).frame(height: 1)
            HStack { Image(systemName: "arrow.down.doc").foregroundStyle(ember); Text("Drop files anywhere").font(.system(size: 11)).foregroundStyle(muted); Spacer(); Menu { Button("Add Media…", action: player.openFiles); Button("Clear Queue", role: .destructive) { engine.unload(); library.clear() }.disabled(library.items.isEmpty) } label: { Image(systemName: "ellipsis").frame(width: 22, height: 22) }.menuStyle(.borderlessButton).fixedSize() }.padding(18)
        }.glassSurface(cornerRadius: 20).padding(.leading, 12).padding(.vertical, 10).padding(.trailing, 2)
    }

    private func queueRow(_ item: MediaItem) -> some View {
        let selected = library.selectedID == item.id
        return Button { player.play(item) } label: {
            HStack(spacing: 11) {
                ZStack { RoundedRectangle(cornerRadius: 8).fill(selected ? ember.opacity(0.16) : labelColor.opacity(0.045)); Image(systemName: selected && engine.isPlaying ? "waveform" : "play.rectangle").font(.system(size: 17, weight: .light)).foregroundStyle(selected ? ember : muted) }.frame(width: 40, height: 45)
                VStack(alignment: .leading, spacing: 5) {
                    Text(item.title).font(.system(size: 13, weight: selected ? .semibold : .medium)).lineLimit(2).multilineTextAlignment(.leading).foregroundStyle(selected ? labelColor : labelColor.opacity(0.8))
                    HStack(spacing: 5) { Text(item.url.pathExtension.uppercased()); Text("·"); Text(!item.isAvailable ? "File unavailable" : item.resumeTime > 0 ? "Resume \(clockLabel(item.resumeTime))" : item.duration > 0 ? clockLabel(item.duration) : "Ready to play") }.font(.system(size: 9)).foregroundStyle(muted).lineLimit(1)
                }
                Spacer(minLength: 0)
                if selected { Circle().fill(ember).frame(width: 5, height: 5) }
            }.padding(10).background(selected ? ember.opacity(0.1) : .clear, in: RoundedRectangle(cornerRadius: 10)).overlay(RoundedRectangle(cornerRadius: 10).stroke(selected ? ember.opacity(0.18) : .clear, lineWidth: 1))
        }.buttonStyle(.plain).contextMenu {
            Button("Play") { player.play(item) }
            Button("Show in Finder") { player.reveal(item) }
            Button("Reset Saved Position") { player.resetProgress(item) }
            Divider()
            Button("Move Earlier") { library.move(id: item.id, offset: -1) }
            Button("Move Later") { library.move(id: item.id, offset: 1) }
            Divider()
            Button("Remove from Queue", role: .destructive) { player.remove(item) }
        }
    }

    private var nowPlaying: some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 7) {
                Text(engine.hasMedia ? "Now Playing" : "Welcome to Luma").font(.system(size: 11, weight: .medium)).foregroundStyle(muted)
                Text(engine.hasMedia ? library.selectedItem?.title ?? "Ready to play" : "A little more cinema.").font(.system(size: 23, weight: .semibold)).lineLimit(1)
            }
            Spacer(minLength: 15)
            if engine.hasMedia {
                Text(engine.state.capitalized).font(.system(size: 11, weight: .medium)).foregroundStyle(muted).padding(.horizontal, 11).padding(.vertical, 6).background(.white.opacity(0.04), in: Capsule())
            }
        }.padding(.horizontal, 24).padding(.top, 15).padding(.bottom, 18)
    }

    private var stage: some View {
        ZStack {
            VideoSurface(engine: engine, onDoubleClick: player.toggleVideoFullscreen, onTogglePlayback: player.toggle)
            if !engine.hasMedia {
                ZStack {
                    LinearGradient(colors: [Color(red: 0.11, green: 0.15, blue: 0.23), Color(red: 0.025, green: 0.035, blue: 0.065)], startPoint: .topLeading, endPoint: .bottomTrailing)
                    RadialGradient(colors: [Color.blue.opacity(0.18), .clear], center: .init(x: 0.65, y: 0.35), startRadius: 0, endRadius: 380)
                    VStack(spacing: 20) {
                        ZStack {
                            RoundedRectangle(cornerRadius: 28).fill(.ultraThinMaterial).frame(width: 94, height: 94).shadow(color: .black.opacity(0.22), radius: 22, y: 12)
                            RoundedRectangle(cornerRadius: 28).stroke(LinearGradient(colors: [.white.opacity(0.5), .white.opacity(0.05)], startPoint: .topLeading, endPoint: .bottomTrailing), lineWidth: 0.75).frame(width: 94, height: 94)
                            Image(systemName: "play.fill").font(.system(size: 32, weight: .regular)).foregroundStyle(.white).offset(x: 4)
                        }.padding(.bottom, 9)
                        Text("Make time for a great watch.").font(.system(size: 23, weight: .medium)).tracking(-0.6)
                        Text("Drop a video or audio file here to get started.").font(.system(size: 12)).foregroundStyle(Color.white.opacity(0.6))
                        Button(action: player.openFiles) { Label("Open Media", systemImage: "folder").font(.system(size: 12, weight: .medium)).padding(.horizontal, 18).padding(.vertical, 10) }.buttonStyle(.plain).glassSurface(cornerRadius: 20).padding(.top, 5)
                    }
                }
            }
            if engine.state == "opening" || engine.state == "buffering" { ProgressView().controlSize(.large).padding(20).background(.black.opacity(0.5), in: RoundedRectangle(cornerRadius: 16)) }
            if let error = engine.errorMessage {
                VStack(spacing: 12) { Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(ember); Text("Playback needs attention").font(.headline); Text(error).font(.system(size: 12)).foregroundStyle(Color.white.opacity(0.6)).multilineTextAlignment(.center); HStack { Button("Dismiss") { engine.errorMessage = nil }; Button("Open another file", action: player.openFiles) } }.padding(30).frame(maxWidth: 380).background(panel, in: RoundedRectangle(cornerRadius: 16))
            }
            if let notice = player.notice { VStack { Spacer(); Text(notice).font(.system(size: 12, weight: .medium)).padding(.horizontal, 16).padding(.vertical, 10).background(.ultraThinMaterial, in: Capsule()).padding(20) }.allowsHitTesting(false) }
        }.clipShape(RoundedRectangle(cornerRadius: player.isVideoFullscreen ? 0 : 20)).overlay(RoundedRectangle(cornerRadius: player.isVideoFullscreen ? 0 : 20).stroke(.white.opacity(player.isVideoFullscreen ? 0 : 0.065), lineWidth: 1)).padding(.horizontal, player.isVideoFullscreen ? 0 : 20).frame(maxWidth: .infinity, maxHeight: .infinity).foregroundStyle(.white).environment(\.colorScheme, .dark)
            .help(player.isVideoFullscreen ? "Double-click or press Escape to leave full screen" : "Double-click for video full screen")
    }

    private var controls: some View {
        VStack(spacing: 17) {
            HStack(spacing: 12) {
                Text(clockLabel(ui.isSeeking ? ui.seekValue : engine.currentTime)).frame(width: 48, alignment: .leading)
                Slider(value: $ui.seekValue, in: 0...max(engine.duration, 1), onEditingChanged: { editing in ui.isSeeking = editing; if !editing { engine.seek(to: ui.seekValue) } }).tint(ember).disabled(!engine.isSeekable).accessibilityLabel("Playback position")
                Text(clockLabel(engine.duration)).frame(width: 48, alignment: .trailing)
            }.font(.system(size: 10, weight: .medium, design: .monospaced)).foregroundStyle(muted)
            HStack(spacing: 9) {
                Menu {
                    ForEach([0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0], id: \.self) { rate in Button("\(rate.formatted())×\(abs(engine.rate - rate) < 0.01 ? "  ✓" : "")") { engine.setRate(rate) } }
                } label: { Text("\(engine.rate.formatted())×").font(.system(size: 12, weight: .semibold)).frame(width: 40) }.menuStyle(.borderlessButton).fixedSize().help("Playback speed").disabled(!engine.hasMedia)
                Menu {
                    Button("Add Subtitle File…", action: player.addSubtitle)
                    Divider()
                    ForEach(engine.subtitleTracks) { track in Button("\(track.name)\(track.id == engine.selectedSubtitleTrack ? "  ✓" : "")") { engine.setSubtitleTrack(track.id) } }
                    if engine.subtitleTracks.isEmpty { Text("No subtitle tracks") }
                    if let note = engine.subtitleRenderingNote { Text(note).font(.caption) }
                    Divider()
                    Button("Subtitles 0.5s Earlier") { engine.setSubtitleDelay(engine.subtitleDelay - 0.5); player.tell("Subtitle delay: \(engine.subtitleDelay.formatted())s") }
                    Button("Subtitles 0.5s Later") { engine.setSubtitleDelay(engine.subtitleDelay + 0.5); player.tell("Subtitle delay: \(engine.subtitleDelay.formatted())s") }
                    Button("Reset Subtitle Timing") { engine.setSubtitleDelay(0) }
                } label: { Image(systemName: "captions.bubble").font(.system(size: 16)).frame(width: 26) }.menuStyle(.borderlessButton).fixedSize().help("Subtitles and timing").disabled(!engine.hasMedia)
                Menu {
                    ForEach(engine.audioTracks) { track in Button("\(track.name)\(track.id == engine.selectedAudioTrack ? "  ✓" : "")") { engine.setAudioTrack(track.id) } }
                    if engine.audioTracks.isEmpty { Text("No audio tracks") }
                } label: { Image(systemName: "waveform").font(.system(size: 15)).frame(width: 24) }.menuStyle(.borderlessButton).fixedSize().help("Audio track").disabled(!engine.hasMedia)
                Spacer(minLength: 8)
                GlyphButton(symbol: "backward.end.fill", label: "Previous file · ⌘←", action: player.previous)
                GlyphButton(symbol: "gobackward.10", label: "Back 10 seconds · ←") { engine.seek(to: engine.currentTime - 10) }
                Button(action: player.toggle) { Image(systemName: engine.isPlaying ? "pause.fill" : "play.fill").font(.system(size: 20, weight: .semibold)).offset(x: engine.isPlaying ? 0 : 2).foregroundStyle(.white).frame(width: 51, height: 51).background(LinearGradient(colors: [Color.accentColor.opacity(0.8), Color.accentColor], startPoint: .top, endPoint: .bottom), in: Circle()).overlay(Circle().stroke(.white.opacity(0.4), lineWidth: 0.75)).shadow(color: Color.accentColor.opacity(0.25), radius: 10, y: 4) }.buttonStyle(.plain).help("Play / pause · Space").accessibilityLabel(engine.isPlaying ? "Pause" : "Play")
                GlyphButton(symbol: "goforward.10", label: "Forward 10 seconds · →") { engine.seek(to: engine.currentTime + 10) }
                GlyphButton(symbol: "forward.end.fill", label: "Next file · ⌘→") { player.next() }
                Spacer(minLength: 8)
                GlyphButton(symbol: player.loopMode == 1 ? "repeat.1" : "repeat", label: ["Repeat off", "Repeat current file", "Repeat queue"][player.loopMode], active: player.loopMode != 0) { player.loopMode = (player.loopMode + 1) % 3; player.tell(["Repeat off", "Repeat current file", "Repeat queue"][player.loopMode]) }
                HStack(spacing: 5) {
                    GlyphButton(symbol: engine.volume == 0 ? "speaker.slash" : "speaker.wave.2", label: "Toggle mute") { engine.setVolume(engine.volume == 0 ? 80 : 0) }
                    Slider(value: Binding(get: { engine.volume }, set: { engine.setVolume($0) }), in: 0...100).frame(width: 64).tint(labelColor.opacity(0.65)).accessibilityLabel("Volume")
                }
                GlyphButton(symbol: "arrow.up.left.and.arrow.down.right", label: "Video full screen · Double-click video") { player.toggleVideoFullscreen() }
            }
        }.padding(.horizontal, 20).padding(.vertical, 16).glassSurface(cornerRadius: 24).padding(.horizontal, 20).padding(.top, 15).padding(.bottom, 16)
    }

    private var inspector: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack { Text("Details").font(.system(size: 15, weight: .semibold)).foregroundStyle(muted); Spacer(); GlyphButton(symbol: "xmark", label: "Close details") { player.showInspector = false } }
            if let item = library.selectedItem {
                VStack(alignment: .leading, spacing: 8) { Text(item.title).font(.system(size: 16, weight: .medium)); Text(engine.backendName).font(.system(size: 11)).foregroundStyle(muted); if let note = engine.subtitleRenderingNote { Text(note).font(.system(size: 11)).foregroundStyle(muted) }; Text(item.url.lastPathComponent).font(.system(size: 11)).foregroundStyle(muted).textSelection(.enabled); Button("Show in Finder ↗") { player.reveal(item) }.buttonStyle(.plain).font(.system(size: 11)).foregroundStyle(ember) }
                HStack { Text("Saved Moments").font(.system(size: 12, weight: .semibold)).foregroundStyle(muted); Spacer(); Button(action: player.bookmark) { Image(systemName: "plus.circle").foregroundStyle(ember) }.buttonStyle(.plain).help("Save current moment · ⌘B") }
                if item.bookmarks.isEmpty { Text("Mark a scene, a favorite line, or where you want to come back.\n\nPress ⌘B while watching.").font(.system(size: 12)).foregroundStyle(muted).lineSpacing(4) }
                ScrollView {
                    VStack(spacing: 8) { ForEach(item.bookmarks) { bookmark in
                        HStack { Button { player.jumpToBookmark(bookmark, in: item) } label: { HStack(spacing: 9) { Image(systemName: "bookmark.fill").foregroundStyle(ember); VStack(alignment: .leading, spacing: 4) { Text(bookmark.label).font(.system(size: 11)); Text(clockLabel(bookmark.time)).font(.system(size: 10, design: .monospaced)).foregroundStyle(muted) }; Spacer() } }.buttonStyle(.plain); Button { library.removeBookmark(id: item.id, bookmarkID: bookmark.id) } label: { Image(systemName: "xmark").font(.system(size: 9)).foregroundStyle(muted) }.buttonStyle(.plain) }.padding(10).background(labelColor.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
                    } }
                }
            } else { Text("Open a file to see its details and save your favorite moments.").font(.system(size: 12)).foregroundStyle(muted).lineSpacing(4); Spacer() }
        }.padding(18).glassSurface(cornerRadius: 20).padding(.trailing, 12).padding(.vertical, 10).padding(.leading, 2)
    }

    private var footer: some View {
        HStack(spacing: 7) {
            Circle().fill(engine.errorMessage == nil ? Color.green.opacity(0.65) : ember).frame(width: 5, height: 5)
            Text(engine.hasMedia ? "\(library.selectedItem?.url.pathExtension.uppercased() ?? "MEDIA")  /  \(engine.state.capitalized)" : "Ready when you are").font(.system(size: 10)).foregroundStyle(muted)
            if let error = library.persistenceError { Text(error).font(.system(size: 10)).foregroundStyle(ember).lineLimit(1) }
            Spacer()
            Button { player.showShortcuts = true } label: { HStack(spacing: 6) { Image(systemName: "command"); Text("Shortcuts") }.font(.system(size: 10)).foregroundStyle(muted) }.buttonStyle(.plain)
            Text("·").foregroundStyle(muted).padding(.horizontal, 5)
            Text(engine.hasMedia ? engine.backendName : "Luma Player").font(.system(size: 10)).foregroundStyle(muted)
        }.padding(.horizontal, 22).frame(height: 33).background(.clear)
    }

    private var shortcutSheet: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("A few good shortcuts.").font(.system(size: 25, weight: .medium))
            ForEach([("Open media", "⌘ O"), ("Play / pause", "Space"), ("Seek 10 seconds", "← / →"), ("Previous / next file", "⌘ ← / →"), ("Save a moment", "⌘ B"), ("Add subtitles", "⌘ ⇧ S"), ("Full screen", "⌃ ⌘ F")], id: \.0) { title, key in HStack { Text(title).foregroundStyle(muted); Spacer(); Text(key).font(.system(size: 12, weight: .medium, design: .monospaced)) } }
            Button("Got it") { player.showShortcuts = false }.buttonStyle(.borderedProminent).tint(ember).frame(maxWidth: .infinity, alignment: .trailing).padding(.top, 8)
        }.padding(32).frame(width: 410).background(.regularMaterial)
    }

    private func dropFiles(_ providers: [NSItemProvider]) -> Bool {
        let accepted = providers.filter { $0.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) }
        for provider in accepted {
            _ = provider.loadObject(ofClass: NSURL.self) { value, _ in
                if let url = value as? URL { Task { @MainActor in player.importFiles([url]) } }
            }
        }
        return !accepted.isEmpty
    }
}
