import AppKit
import AVFoundation
import Combine

struct PlaybackTrack: Identifiable, Equatable { let id: Int32; let name: String }

@MainActor final class PlaybackCanvas: NSView {
    let movieLayer = AVPlayerLayer()
    let imageLayer = CALayer()
    let bitmapLayer = CALayer()
    private var bitmapEvent: PGSDisplayEvent?
    let caption = NSTextField(wrappingLabelWithString: "")
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        movieLayer.videoGravity = .resizeAspect
        imageLayer.contentsGravity = .resizeAspect
        layer?.addSublayer(movieLayer); layer?.addSublayer(imageLayer); layer?.addSublayer(bitmapLayer)
        caption.alignment = .center
        caption.textColor = .white
        caption.font = .systemFont(ofSize: 22, weight: .semibold)
        caption.shadow = NSShadow()
        caption.shadow?.shadowColor = NSColor.black
        caption.shadow?.shadowBlurRadius = 4
        addSubview(caption)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }
    override func layout() {
        super.layout()
        CATransaction.begin(); CATransaction.setDisableActions(true)
        movieLayer.frame = bounds; imageLayer.frame = bounds; bitmapLayer.frame = bounds
        layoutBitmaps()
        CATransaction.commit()
        caption.frame = NSRect(x: 30, y: 24, width: max(0, bounds.width - 60), height: 80)
    }
    func displayPGS(_ event: PGSDisplayEvent?) {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        bitmapEvent = event; bitmapLayer.sublayers = []
        if let event {
            for bitmap in event.bitmaps {
                guard let provider = CGDataProvider(data: bitmap.rgba as CFData),
                      let image = CGImage(width: bitmap.width, height: bitmap.height, bitsPerComponent: 8, bitsPerPixel: 32,
                        bytesPerRow: bitmap.width * 4, space: CGColorSpaceCreateDeviceRGB(),
                        bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue), provider: provider,
                        decode: nil, shouldInterpolate: true, intent: .defaultIntent) else { continue }
                let output = CALayer(); output.contents = image; output.contentsGravity = .resize
                bitmapLayer.addSublayer(output)
            }
        }
        layoutBitmaps(); CATransaction.commit()
    }
    private func layoutBitmaps() {
        guard let event = bitmapEvent, let layers = bitmapLayer.sublayers, layers.count == event.bitmaps.count else { return }
        let video = movieLayer.isHidden || movieLayer.videoRect.isEmpty ? bounds : movieLayer.videoRect
        let scaleX = video.width / CGFloat(event.canvasWidth), scaleY = video.height / CGFloat(event.canvasHeight)
        for (bitmap, output) in zip(event.bitmaps, layers) {
            output.frame = CGRect(x: video.minX + CGFloat(bitmap.x) * scaleX,
                y: video.minY + CGFloat(event.canvasHeight - bitmap.y - bitmap.height) * scaleY,
                width: CGFloat(bitmap.width) * scaleX, height: CGFloat(bitmap.height) * scaleY)
        }
    }
}

/// Own transport/UI implementation. RIFF/FLAC/JPEG codecs are original Luma code;
/// other supported compressed formats explicitly use macOS's system decoder.
@MainActor final class PlaybackEngine: ObservableObject {
    @Published private(set) var state = "idle"
    @Published private(set) var isPlaying = false
    @Published private(set) var hasMedia = false
    @Published private(set) var isSeekable = false
    @Published private(set) var currentTime: Double = 0
    @Published private(set) var duration: Double = 0
    @Published private(set) var volume: Double = 80
    @Published private(set) var rate: Double = 1
    @Published private(set) var subtitleDelay: Double = 0
    @Published private(set) var audioTracks: [PlaybackTrack] = []
    @Published private(set) var subtitleTracks: [PlaybackTrack] = []
    @Published private(set) var selectedAudioTrack: Int32 = -1
    @Published private(set) var selectedSubtitleTrack: Int32 = -1
    @Published private(set) var subtitleRenderingNote: String?
    @Published private(set) var backendName = "Luma experimental"
    @Published var errorMessage: String?
    let engineVersion = "Luma experimental"
    let player = AVPlayer()
    private var canvas: PlaybackCanvas?
    private var ticker: AnyCancellable?
    private var loading: Task<Void, Never>?
    private var generation = UUID()
    private var temporaryAudio: URL?
    private var avi: OriginalAVI?
    private var anchor = Date()
    private var anchorTime: Double = 0
    private var lastFrame = -1
    private var frameTask: Task<Void, Never>?
    private var frameGeneration = UUID()
    private var endObserver: NSObjectProtocol?
    private var audioGroup: AVMediaSelectionGroup?
    private var textGroup: AVMediaSelectionGroup?
    private var cues: [SubtitleCue] = []
    private var externalCues: [SubtitleCue] = []
    private var matroskaSubtitles: [Int32: MatroskaTextTrack] = [:]
    private var bitmapSubtitles: [Int32: PGSSubtitleTrack] = [:]
    private var bitmapEventIndex: Int?
    private let externalTrack: Int32 = 1_000_000

    init() {
        player.volume = Float(volume / 100)
        ticker = Timer.publish(every: 0.04, on: .main, in: .common).autoconnect().sink { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
    }

    func attachVideoView(_ view: NSView) {
        if canvas?.superview === view { return }
        canvas?.removeFromSuperview()
        let output = PlaybackCanvas(frame: view.bounds)
        output.autoresizingMask = [.width, .height]
        output.movieLayer.player = player
        view.addSubview(output)
        canvas = output
        bitmapEventIndex = nil
        lastFrame = -1
    }

    func load(_ url: URL) {
        unload()
        guard url.isFileURL, FileManager.default.fileExists(atPath: url.path) else {
            errorMessage = "Choose an available local media file."; state = "error"; return
        }
        hasMedia = true; state = "opening"
        let token = generation
        loading = Task { [self] in
            do {
                var playbackURL = url
                if url.pathExtension.lowercased() == "wav" {
                    let worker = Task.detached(priority: .userInitiated) {
                        let decoded = try OriginalWaveDecoder.decode(Data(contentsOf: url, options: .mappedIfSafe))
                        try Task.checkCancellation()
                        return (decoded.pcmWave(), decoded.codecName)
                    }
                    let prepared = try await withTaskCancellationHandler(operation: { try await worker.value }, onCancel: { worker.cancel() })
                    guard token == generation, !Task.isCancelled else { return }
                    let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("Luma-\(UUID().uuidString).wav")
                    try prepared.0.write(to: temporary, options: .atomic)
                    temporaryAudio = temporary; playbackURL = temporary; backendName = prepared.1
                } else if url.pathExtension.lowercased() == "flac" {
                    let worker = Task.detached(priority: .userInitiated) {
                        let decoded = try OriginalFLACDecoder.decode(Data(contentsOf: url, options: .mappedIfSafe))
                        try Task.checkCancellation()
                        return (decoded.pcmWave(), decoded.codecName)
                    }
                    let prepared = try await withTaskCancellationHandler(operation: { try await worker.value }, onCancel: { worker.cancel() })
                    guard token == generation, !Task.isCancelled else { return }
                    let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("Luma-\(UUID().uuidString).wav")
                    try prepared.0.write(to: temporary, options: .atomic)
                    temporaryAudio = temporary; playbackURL = temporary; backendName = prepared.1
                } else if ["mkv", "mka"].contains(url.pathExtension.lowercased()) {
                    let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("Luma-\(UUID().uuidString).mov")
                    var retained = false
                    defer { if !retained { try? FileManager.default.removeItem(at: temporary) } }
                    let result = try await MatroskaRemuxer.remux(input: url, output: temporary)
                    guard token == generation, !Task.isCancelled else { return }
                    temporaryAudio = temporary; retained = true; playbackURL = temporary
                    backendName = result.codecSummary
                    matroskaSubtitles = Dictionary(uniqueKeysWithValues: result.subtitles.map { ($0.id, $0) })
                    bitmapSubtitles = Dictionary(uniqueKeysWithValues: result.bitmapSubtitles.map { ($0.id, $0) })
                } else if url.pathExtension.lowercased() == "avi" {
                    let worker = Task.detached(priority: .userInitiated) {
                        try OriginalAVI.open(Data(contentsOf: url, options: .mappedIfSafe))
                    }
                    let video = try await withTaskCancellationHandler(operation: { try await worker.value }, onCancel: { worker.cancel() })
                    guard token == generation, !Task.isCancelled else { return }
                    avi = video; duration = video.duration; isSeekable = true
                    backendName = video.codecName
                    canvas?.movieLayer.isHidden = true; canvas?.imageLayer.isHidden = false
                    play(); return
                } else { backendName = "macOS system decoding" }
                let asset = AVURLAsset(url: playbackURL)
                let playable = try await asset.load(.isPlayable)
                guard token == generation, !Task.isCancelled else { return }
                guard playable else { throw CodecFailure.unsupported("This codec is not supported yet. Luma’s new engine is experimental; try MP4/MOV, supported audio, or uncompressed RGB AVI.") }
                let assetDuration = try await asset.load(.duration).seconds
                guard token == generation, !Task.isCancelled else { return }
                duration = assetDuration.isFinite ? max(0, assetDuration) : 0
                let item = AVPlayerItem(asset: asset)
                player.replaceCurrentItem(with: item)
                canvas?.movieLayer.isHidden = false; canvas?.imageLayer.isHidden = true
                let loadedAudioGroup = try await asset.loadMediaSelectionGroup(for: .audible)
                let loadedTextGroup = try await asset.loadMediaSelectionGroup(for: .legible)
                guard token == generation, !Task.isCancelled else { return }
                audioGroup = loadedAudioGroup; textGroup = loadedTextGroup
                audioTracks = audioGroup?.options.enumerated().map { PlaybackTrack(id: Int32($0.offset), name: $0.element.displayName) } ?? []
                subtitleTracks = [PlaybackTrack(id: -1, name: "Off")] + (textGroup?.options.enumerated().map { PlaybackTrack(id: Int32($0.offset), name: $0.element.displayName) } ?? [])
                subtitleTracks += matroskaSubtitles.values.sorted { $0.id < $1.id }.map { PlaybackTrack(id: $0.id, name: $0.name) }
                subtitleTracks += bitmapSubtitles.values.sorted { $0.id < $1.id }.map { PlaybackTrack(id: $0.id, name: $0.name) }
                if let group = audioGroup {
                    let selected = group.defaultOption ?? group.options.first
                    item.select(selected, in: group)
                    selectedAudioTrack = Int32(selected.flatMap { option in group.options.firstIndex(of: option) } ?? -1)
                }
                if let group = textGroup { item.select(nil, in: group) }
                let localText = matroskaSubtitles.values.sorted { $0.id < $1.id }
                let localBitmap = bitmapSubtitles.values.sorted { $0.id < $1.id }
                if let id = localText.first(where: \.isForced)?.id ?? localBitmap.first(where: \.isForced)?.id
                    ?? localText.first(where: \.isDefault)?.id ?? localBitmap.first(where: \.isDefault)?.id { setSubtitleTrack(id) }
                endObserver = NotificationCenter.default.addObserver(forName: AVPlayerItem.didPlayToEndTimeNotification, object: item, queue: .main) { [weak self = self] _ in
                    MainActor.assumeIsolated { self?.finish() }
                }
                isSeekable = duration > 0
                play()
            } catch {
                guard token == generation, !Task.isCancelled else { return }
                errorMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                state = "error"; isPlaying = false
            }
        }
    }

    private func play() {
        if avi != nil {
            anchor = Date(); anchorTime = currentTime; state = "playing"; isPlaying = true
        } else { player.playImmediately(atRate: Float(rate)); state = "buffering" }
    }
    func togglePause() {
        guard hasMedia, state != "opening", state != "error" else { return }
        if isPlaying { pause() }
        else { if state == "ended" || state == "stopped" { seek(to: 0) }; play() }
    }
    func pause() {
        poll(); player.pause(); anchorTime = currentTime; anchor = Date(); isPlaying = false; state = "paused"
    }
    func stop() {
        cancelFrame()
        player.pause(); isPlaying = false; state = "stopped"; currentTime = 0; anchorTime = 0
        player.seek(to: .zero); canvas?.caption.stringValue = ""; bitmapEventIndex = nil; canvas?.displayPGS(nil)
    }
    func unload() {
        generation = UUID(); loading?.cancel(); loading = nil
        cancelFrame()
        player.pause(); player.replaceCurrentItem(with: nil)
        if let endObserver { NotificationCenter.default.removeObserver(endObserver); self.endObserver = nil }
        if let temporaryAudio { try? FileManager.default.removeItem(at: temporaryAudio); self.temporaryAudio = nil }
        avi = nil; lastFrame = -1; anchorTime = 0
        hasMedia = false; isPlaying = false; isSeekable = false; currentTime = 0; duration = 0; state = "idle"
        cues = []; externalCues = []; matroskaSubtitles = [:]; subtitleRenderingNote = nil
        bitmapSubtitles = [:]; bitmapEventIndex = nil; canvas?.displayPGS(nil)
        audioGroup = nil; textGroup = nil; audioTracks = []; subtitleTracks = []
        selectedAudioTrack = -1; selectedSubtitleTrack = -1; subtitleDelay = 0; errorMessage = nil
        canvas?.imageLayer.contents = nil; canvas?.caption.stringValue = ""
    }
    func seek(to seconds: Double) {
        guard isSeekable, seconds.isFinite else { return }
        let time = min(duration, max(0, seconds))
        currentTime = time; anchorTime = time; anchor = Date(); lastFrame = -1
        cancelFrame()
        if avi == nil { player.seek(to: CMTime(seconds: time, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) }
    }
    func setVolume(_ value: Double) {
        guard value.isFinite else { return }; volume = min(100, max(0, value)); player.volume = Float(volume / 100)
    }
    func setRate(_ value: Double) {
        guard value.isFinite else { return }; poll(); anchorTime = currentTime; anchor = Date(); rate = min(3, max(0.25, value))
        if avi == nil, state == "playing" || state == "buffering" { player.rate = Float(rate) }
    }
    func setAudioTrack(_ id: Int32) {
        guard let group = audioGroup, let item = player.currentItem, id >= 0, Int(id) < group.options.count else { return }
        item.select(group.options[Int(id)], in: group); selectedAudioTrack = id
    }
    func setSubtitleTrack(_ id: Int32) {
        bitmapEventIndex = nil; canvas?.displayPGS(nil)
        if let track = bitmapSubtitles[id] {
            if let group = textGroup { player.currentItem?.select(nil, in: group) }
            cues = []; canvas?.caption.stringValue = ""; selectedSubtitleTrack = track.id
            subtitleRenderingNote = "Original PGS bitmap rendering. Blu-ray 3D and HDR extensions remain unsupported."
            return
        }
        if let track = matroskaSubtitles[id] {
            if let group = textGroup { player.currentItem?.select(nil, in: group) }
            cues = track.cues.map { SubtitleCue(start: $0.start, end: $0.end, text: $0.text) }
            selectedSubtitleTrack = id; subtitleRenderingNote = track.renderingNote
            canvas?.caption.stringValue = ""; return
        }
        if id == externalTrack && !externalCues.isEmpty {
            cues = externalCues; subtitleRenderingNote = nil
            if let group = textGroup { player.currentItem?.select(nil, in: group) }; selectedSubtitleTrack = id; return
        }
        if id == -1 {
            if let group = textGroup { player.currentItem?.select(nil, in: group) }; selectedSubtitleTrack = -1; subtitleRenderingNote = nil; cues = []; canvas?.caption.stringValue = ""; return
        }
        guard let group = textGroup, id >= 0, Int(id) < group.options.count else { return }
        player.currentItem?.select(group.options[Int(id)], in: group); selectedSubtitleTrack = id; subtitleRenderingNote = nil; cues = []; canvas?.caption.stringValue = ""
    }
    func addSubtitle(_ url: URL) {
        do {
            guard hasMedia else { throw CodecFailure.unsupported("Open a video first.") }
            externalCues = try SubtitleCue.read(url)
            subtitleTracks.removeAll { $0.id == externalTrack }
            if !subtitleTracks.contains(where: { $0.id == -1 }) { subtitleTracks.insert(PlaybackTrack(id: -1, name: "Off"), at: 0) }
            subtitleTracks.append(PlaybackTrack(id: externalTrack, name: url.lastPathComponent))
            setSubtitleTrack(externalTrack)
        } catch { errorMessage = error.localizedDescription }
    }
    func setSubtitleDelay(_ seconds: Double) {
        guard seconds.isFinite else { return }
        guard selectedSubtitleTrack == externalTrack || matroskaSubtitles[selectedSubtitleTrack] != nil || bitmapSubtitles[selectedSubtitleTrack] != nil else { errorMessage = "Timing adjustment is available for Luma-rendered subtitles."; return }
        subtitleDelay = min(60, max(-60, seconds))
    }
    func shutdown() { unload(); ticker?.cancel(); ticker = nil; canvas?.removeFromSuperview(); canvas = nil }
    private func finish() { currentTime = duration; isPlaying = false; state = "ended"; player.pause(); canvas?.caption.stringValue = ""; bitmapEventIndex = nil; canvas?.displayPGS(nil) }
    private func cancelFrame() {
        frameGeneration = UUID()
        frameTask?.cancel(); frameTask = nil
    }
    private func requestFrame(_ video: OriginalAVI, index: Int, time: Double) {
        guard frameTask == nil else { return }
        let token = frameGeneration
        lastFrame = index
        frameTask = Task { [weak self] in
            let decoding = Task.detached(priority: .userInitiated) { try Task.checkCancellation(); return try video.image(at: time) }
            do {
                let image = try await withTaskCancellationHandler(operation: { try await decoding.value }, onCancel: { decoding.cancel() })
                guard let self, token == self.frameGeneration else { return }
                self.frameTask = nil
                self.canvas?.imageLayer.contents = image
                // A slow decode may have occupied the worker when the last
                // poll ended playback. Render the actual final frame next.
                if self.state == "ended", index != video.frames.count - 1 {
                    self.requestFrame(video, index: video.frames.count - 1, time: self.duration)
                }
            } catch {
                guard let self, token == self.frameGeneration else { return }
                self.frameTask = nil
                if error is CancellationError { return }
                self.state = "error"; self.isPlaying = false; self.errorMessage = error.localizedDescription
            }
        }
    }
    private func poll() {
        guard hasMedia, !["opening", "error", "idle", "stopped", "ended"].contains(state) else { return }
        if let avi {
            if isPlaying { currentTime = min(duration, anchorTime + Date().timeIntervalSince(anchor) * rate) }
            let frame = min(avi.frames.count - 1, Int(currentTime / avi.frameDuration))
            if frame != lastFrame {
                requestFrame(avi, index: frame, time: currentTime)
            }
            if currentTime >= duration { finish() }
        } else {
            if player.currentItem?.status == .failed { state = "error"; isPlaying = false; errorMessage = player.currentItem?.error?.localizedDescription ?? "The system decoder could not play this file."; return }
            let time = player.currentTime().seconds
            if time.isFinite { currentTime = max(0, time) }
            if state != "paused" {
                isPlaying = player.timeControlStatus == .playing
                state = isPlaying ? "playing" : "buffering"
            }
        }
        if selectedSubtitleTrack == externalTrack || matroskaSubtitles[selectedSubtitleTrack] != nil {
            let time = currentTime - subtitleDelay
            canvas?.caption.stringValue = cues.filter { $0.start <= time && time < $0.end }.map(\.text).joined(separator: "\n")
        }
        if let track = bitmapSubtitles[selectedSubtitleTrack] {
            let time = currentTime - subtitleDelay
            var low = 0, high = track.events.count
            while low < high { let middle = (low + high) / 2; if track.events[middle].time <= time { low = middle + 1 } else { high = middle } }
            var desired: Int? = low > 0 ? low - 1 : nil
            if let index = desired, let end = track.events[index].endTime, time >= end { desired = nil }
            if desired != bitmapEventIndex { bitmapEventIndex = desired; canvas?.displayPGS(desired.map { track.events[$0] }) }
        }
    }
}
