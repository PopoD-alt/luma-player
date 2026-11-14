import AppKit
import AVFoundation
import AudioToolbox
import Foundation

@main struct VideoSmoke {
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
        let root = URL(fileURLWithPath: CommandLine.arguments[1])
        let window = NSWindow(contentRect: NSRect(x: 100, y: 100, width: 640, height: 360), styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.orderFront(nil)
        let host = NSView(frame: NSRect(x: 0, y: 0, width: 640, height: 360))
        window.contentView = host
        let engine = PlaybackEngine()
        engine.setVolume(0); engine.attachVideoView(host)
        defer { engine.shutdown(); window.close() }
        engine.load(root.appendingPathComponent("Luma-Demo.mp4"))
        try await wait { engine.isPlaying && engine.duration > 0 }
        guard let output = host.subviews.first as? PlaybackCanvas else { fatalError("No native drawable") }
        try await wait { output.movieLayer.isReadyForDisplay }
        engine.pause(); engine.seek(to: 1)
        engine.addSubtitle(root.appendingPathComponent("Luma-Demo.srt"))
        precondition(engine.subtitleTracks.count >= 2 && engine.selectedSubtitleTrack == 1_000_000)
        engine.setSubtitleDelay(0.5); precondition(engine.subtitleDelay == 0.5)
        try await wait { !output.caption.stringValue.isEmpty }
        engine.setSubtitleTrack(-1); precondition(output.caption.stringValue.isEmpty)
        print("PASS: system MP4 video output, seek, original SRT renderer/timing/off")
        engine.load(root.appendingPathComponent("Luma-RGB.avi"))
        try await wait { engine.isPlaying && output.imageLayer.contents != nil }
        precondition(engine.backendName.contains("Luma RGB AVI"))
        engine.pause(); engine.seek(to: 0.5)
        try await Task.sleep(for: .milliseconds(100))
        let image = output.imageLayer.contents as! CGImage
        let pixel = image.dataProvider!.data! as Data
        precondition(Array(pixel.prefix(4)) == [0,255,0,255])
        window.setContentSize(NSSize(width: 1000, height: 700))
        try await wait { output.bounds.width == 1000 && output.bounds.height == 700 }
        engine.togglePause(); try await wait { engine.isPlaying }
        print("PASS: original RGB AVI output, exact frame after seek, native resize, resume")
        engine.setRate(3)
        try await wait { engine.state == "ended" }
        try await wait {
            guard let contents = output.imageLayer.contents else { return false }
            let image = contents as! CGImage
            guard let data = image.dataProvider?.data else { return false }
            return Array((data as Data).prefix(4)) == [0,0,255,255]
        }
        engine.setRate(1)
        print("PASS: ended AVI displays its final frame")
        engine.load(root.appendingPathComponent("Luma-RLE8.avi"))
        try await wait { engine.isPlaying && engine.backendName.contains("RLE8") && output.imageLayer.contents != nil }
        engine.pause(); engine.seek(to: 1)
        try await Task.sleep(for: .milliseconds(100))
        let compressedImage = output.imageLayer.contents as! CGImage
        let compressedPixel = compressedImage.dataProvider!.data! as Data
        precondition(Array(compressedPixel.prefix(4)) == [0,0,255,255])
        print("PASS: original compressed RLE8 AVI playback and exact frame seek")
        engine.load(root.appendingPathComponent("Luma-MJPEG.avi"))
        try await wait { engine.isPlaying && engine.backendName.contains("MJPEG") && output.imageLayer.contents != nil }
        engine.pause(); engine.seek(to: 0.5)
        try await Task.sleep(for: .milliseconds(150))
        let jpeg = output.imageLayer.contents as! CGImage
        precondition(jpeg.width == 127 && jpeg.height == 95)
        engine.togglePause()
        engine.seek(to: 3)
        engine.stop()
        try await Task.sleep(for: .milliseconds(200))
        precondition(engine.state == "stopped" && !engine.isPlaying)
        print("PASS: original baseline MJPEG AVI playback and seek")
        for depth in [8, 16] {
            engine.load(root.appendingPathComponent("Luma-MSVideo1-\(depth).avi"))
            try await wait { engine.isPlaying && engine.backendName.contains("Video 1") && output.imageLayer.contents != nil }
            engine.pause()
            for index in [0, 7, 2, 11, 1, 8] {
                let expected = try Data(contentsOf: root.appendingPathComponent("msvideo1/\(depth)-frame-\(String(format: "%02d", index)).rgba"))
                engine.seek(to: Double(index) * 0.5)
                try await wait {
                    guard let content = output.imageLayer.contents else { return false }
                    let image = content as! CGImage
                    guard let data = image.dataProvider?.data else { return false }
                    return (data as Data) == expected
                }
            }
            engine.togglePause(); try await wait { engine.isPlaying }
            print("PASS: original MS Video 1 \(depth)-bit native output, exact delta frames after forward/backward seek, resume")
        }
        engine.load(root.appendingPathComponent("Luma-AVC-AAC.mkv"))
        try await wait { engine.isPlaying && engine.backendName.contains("Matroska") && output.movieLayer.isReadyForDisplay }
        engine.pause(); engine.seek(to: 1.5)
        engine.togglePause(); try await wait { engine.isPlaying }
        print("PASS: original Matroska remux plus system AVC/AAC playback, seek and resume")
        let nativeRoot = root.appendingPathComponent("NativeFormats", isDirectory: true)
        if FileManager.default.fileExists(atPath: nativeRoot.path) {
            for file in try FileManager.default.contentsOfDirectory(at: nativeRoot, includingPropertiesForKeys: nil).filter({ $0.pathExtension == "mov" }).sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
                engine.load(file)
                try await wait {
                    if let error = engine.errorMessage { throw NSError(domain: "NativeFormatPlayback", code: 1, userInfo: [NSLocalizedDescriptionKey: error]) }
                    return engine.isPlaying && engine.duration > 0 && output.movieLayer.isReadyForDisplay
                }
                engine.pause(); engine.seek(to: 0.4); engine.togglePause()
                try await wait { engine.isPlaying && engine.currentTime > 0.45 }
                print("PASS: native format actual player output and seek: \(file.lastPathComponent)")
            }
        }

        // Optional local files are checked without adding them to the saved library.
        for path in CommandLine.arguments.dropFirst(2) {
            engine.load(URL(fileURLWithPath: path))
            try await wait {
                if let error = engine.errorMessage { throw NSError(domain: "LocalMedia", code: 1, userInfo: [NSLocalizedDescriptionKey: error]) }
                return engine.isPlaying && engine.duration > 0 && output.movieLayer.isReadyForDisplay
            }
            let asset = engine.player.currentItem!.asset
            if engine.audioTracks.count > 1, let group = try await asset.loadMediaSelectionGroup(for: .audible) {
                for track in engine.audioTracks {
                    engine.setAudioTrack(track.id)
                    guard engine.player.currentItem!.currentMediaSelection.selectedMediaOption(in: group) == group.options[Int(track.id)] else { throw NSError(domain: "AudioSelection", code: 1) }
                }
                engine.setAudioTrack(0)
                print("PASS: every alternate audio track is independently selectable")
            }
            let embedded = engine.subtitleTracks.filter { $0.id >= 2_000_000 }
            for track in embedded {
                engine.setSubtitleTrack(track.id)
                precondition(engine.selectedSubtitleTrack == track.id)
                engine.setSubtitleDelay(0.5); precondition(engine.subtitleDelay == 0.5)
            }
            engine.setSubtitleTrack(-1); precondition(output.caption.stringValue.isEmpty)
            if !embedded.isEmpty { print("PASS: \(embedded.count) embedded text tracks, selection, delay and Off") }
            guard let audio = try await asset.loadTracks(withMediaType: .audio).first else { fatalError("Local media lost audio") }
            let formats = try await audio.load(.formatDescriptions)
            let audioFormat = CMAudioFormatDescriptionGetRichestDecodableFormat(formats.first!)?.pointee.mASBD
                ?? CMAudioFormatDescriptionGetStreamBasicDescription(formats.first!)!.pointee
            for fraction in [0.0, 0.5, 0.95] {
                let position = engine.duration * fraction
                let reader = try AVAssetReader(asset: asset)
                reader.timeRange = CMTimeRange(start: CMTime(seconds: position, preferredTimescale: 600), duration: CMTime(seconds: 1, preferredTimescale: 600))
                let pcm = AVAssetReaderTrackOutput(track: audio, outputSettings: [AVFormatIDKey: kAudioFormatLinearPCM,
                    AVSampleRateKey: audioFormat.mSampleRate, AVNumberOfChannelsKey: audioFormat.mChannelsPerFrame,
                    AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false, AVLinearPCMIsNonInterleaved: false])
                reader.add(pcm)
                guard reader.startReading() else { throw reader.error! }
                var samples = 0
                while let buffer = pcm.copyNextSampleBuffer() { samples += CMSampleBufferGetNumSamples(buffer) }
                guard reader.status == .completed && samples > 0 else { throw reader.error ?? NSError(domain: "LocalAudioDecode", code: 1) }
                engine.pause(); engine.seek(to: position); engine.togglePause()
                try await wait { engine.isPlaying && engine.currentTime > position + 0.1 }
            }
            print("PASS: local MKV video/audio at beginning, middle and near end; \(Int(audioFormat.mSampleRate)) Hz, \(audioFormat.mChannelsPerFrame) channels; \(engine.duration)s; \(engine.backendName)")
        }

    }
    @MainActor static func wait(_ condition: () throws -> Bool) async throws {
        for _ in 0..<1200 { if try condition() { return }; try await Task.sleep(for: .milliseconds(100)) }
        throw NSError(domain: "VideoSmokeTimeout", code: 1)
    }
}
