import Foundation
import AVFoundation
import VideoToolbox

private func problem(_ message: String) -> NSError { NSError(domain: "LumaHevcCase", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }

@main struct GenerateHevcCase {
    static func main() async throws {
        guard CommandLine.arguments.count == 3 else { throw problem("Provide output directory and AAC5.1 file") }
        let folder = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        // The portable HevcIssueFixtures suite authors the silent AAC source;
        // native stereo export presets must not silently downmix it here.
        let audioURL = URL(fileURLWithPath: CommandLine.arguments[2])
        let audioAsset = AVURLAsset(url: audioURL)
        guard let audioTrack = try await audioAsset.loadTracks(withMediaType: .audio).first,
            let audioFormat = try await audioTrack.load(.formatDescriptions).first,
            let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(audioFormat), asbd.pointee.mChannelsPerFrame == 6,
            asbd.pointee.mSampleRate == 48000, asbd.pointee.mFormatID == kAudioFormatMPEG4AAC else { throw problem("Provide the authored AAC-LC48kHz5.1 M4A source") }
        let videoURL = folder.appendingPathComponent("Luma-1920x804-Main10-video.mp4")
        let outputURL = folder.appendingPathComponent("Luma-HEVC10-AAC51.mp4")
        for url in [videoURL, outputURL] { if FileManager.default.fileExists(atPath: url.path) { throw problem("Output already exists: \(url.lastPathComponent)") } }
        let writer = try AVAssetWriter(outputURL: videoURL, fileType: .mp4)
        let settings: [String: Any] = [AVVideoCodecKey: AVVideoCodecType.hevc, AVVideoWidthKey: 1920, AVVideoHeightKey: 804,
            AVVideoCompressionPropertiesKey: [AVVideoProfileLevelKey: kVTProfileLevel_HEVC_Main10_AutoLevel, AVVideoAverageBitRateKey: 1_999_000, AVVideoMaxKeyFrameIntervalKey: 25],
            AVVideoColorPropertiesKey: [AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2, AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2, AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2]]
        guard writer.canApply(outputSettings: settings, forMediaType: .video) else { throw problem("Native HEVC Main10 encoder unavailable") }
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange, kCVPixelBufferWidthKey as String: 1920, kCVPixelBufferHeightKey as String: 804, kCVPixelBufferIOSurfacePropertiesKey as String: [:]])
        writer.add(input)
        guard writer.startWriting() else { throw writer.error ?? problem("Cannot start native encoder") }
        writer.startSession(atSourceTime: .zero)
        for frame in 0..<50 {
            let deadline = Date().addingTimeInterval(20)
            while !input.isReadyForMoreMediaData {
                if writer.status == .failed || Date() > deadline { throw writer.error ?? problem("Encoder stalled") }
                try await Task.sleep(nanoseconds: 2_000_000)
            }
            guard let pool = adaptor.pixelBufferPool else { throw problem("P010 pixel pool unavailable") }
            var optional: CVPixelBuffer?
            guard CVPixelBufferPoolCreatePixelBuffer(nil, pool, &optional) == kCVReturnSuccess, let pixel = optional else { throw problem("P010 allocation failed") }
            CVPixelBufferLockBaseAddress(pixel, [])
            for plane in 0..<2 {
                guard let base = CVPixelBufferGetBaseAddressOfPlane(pixel, plane) else { throw problem("P010 plane missing") }
                let rows = CVPixelBufferGetHeightOfPlane(pixel, plane), width = CVPixelBufferGetWidthOfPlane(pixel, plane), stride = CVPixelBufferGetBytesPerRowOfPlane(pixel, plane)
                for row in 0..<rows {
                    let words = base.advanced(by: row * stride).assumingMemoryBound(to: UInt16.self)
                    if plane == 0 { for x in 0..<width { words[x] = UInt16(64 + (x + row + frame * 19) % 877) << 6 } }
                    else { for x in 0..<(width * 2) { words[x] = 512 << 6 } }
                }
            }
            CVPixelBufferUnlockBaseAddress(pixel, [])
            guard adaptor.append(pixel, withPresentationTime: CMTime(value: Int64(frame), timescale: 25)) else { throw writer.error ?? problem("Cannot append P010 frame") }
        }
        input.markAsFinished(); await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? problem("Native HEVC encode failed") }
        let composition = AVMutableComposition()
        for (url, type) in [(videoURL, AVMediaType.video), (audioURL, AVMediaType.audio)] {
            let asset = AVURLAsset(url: url)
            guard let source = try await asset.loadTracks(withMediaType: type).first, let destination = composition.addMutableTrack(withMediaType: type, preferredTrackID: kCMPersistentTrackID_Invalid) else { throw problem("Cannot create composed track") }
            let duration = try await asset.load(.duration)
            try destination.insertTimeRange(CMTimeRange(start: .zero, duration: min(duration, CMTime(seconds: 2, preferredTimescale: 48000))), of: source, at: .zero)
        }
        guard let exporter = AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetPassthrough) else { throw problem("Passthrough exporter unavailable") }
        exporter.outputURL = outputURL; exporter.outputFileType = .mp4; await exporter.export()
        guard exporter.status == .completed else { throw exporter.error ?? problem("AV mux failed") }
        print("Generated original HEVC Main10 1920x804/25fps BT.709 + AAC 48kHz5.1 MP4: \(outputURL.path)")
    }

}
