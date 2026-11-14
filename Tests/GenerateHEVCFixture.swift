import AVFoundation
import CoreMedia
import CoreVideo
import Foundation

/// Original sample pixels come from Luma-Demo.mp4. Apple's encoder supplies the
/// HEVC bitstreams; no VLC/FFmpeg or downloaded media is involved. Main10 uses
/// a10-bit pixel-buffer conversion of the original8-bit source, not an HDR claim.
@main
struct GenerateHEVCFixture {
    static func main() async throws {
        let directory = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Tests/Fixtures", isDirectory: true)
        let inputURL = directory.appendingPathComponent("Luma-Demo.mp4")
        for (depth, profile, pixelFormat) in [
            (8, kVTProfileLevel_HEVC_Main_AutoLevel as String, kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange),
            (10, kVTProfileLevel_HEVC_Main10_AutoLevel as String, kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange),
        ] {
            let asset = AVURLAsset(url: inputURL)
            guard let track = try await asset.loadTracks(withMediaType: .video).first else { throw failure("Missing original demo video") }
            let reader = try AVAssetReader(asset: asset)
            let output = AVAssetReaderTrackOutput(track: track, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: pixelFormat])
            reader.add(output)
            let target = directory.appendingPathComponent("Luma-HEVC\(depth).mov")
            if FileManager.default.fileExists(atPath: target.path) { try FileManager.default.removeItem(at: target) }
            let writer = try AVAssetWriter(outputURL: target, fileType: .mov)
            let writerInput = AVAssetWriterInput(mediaType: .video, outputSettings: [
                AVVideoCodecKey: AVVideoCodecType.hevc,
                AVVideoWidthKey: 960, AVVideoHeightKey: 540,
                AVVideoCompressionPropertiesKey: [AVVideoProfileLevelKey: profile,
                    AVVideoAverageBitRateKey: 750_000, AVVideoAllowFrameReorderingKey: false,
                    AVVideoMaxKeyFrameIntervalKey: 30],
            ])
            guard writer.canAdd(writerInput) else { throw failure("Native HEVC\(depth) encoder unavailable") }
            writer.add(writerInput)
            guard writer.startWriting(), reader.startReading() else { throw writer.error ?? reader.error ?? failure("Native HEVC fixture cannot start") }
            writer.startSession(atSourceTime: .zero)
            var frames = 0
            while frames < 60, let sample = output.copyNextSampleBuffer() {
                let deadline = Date().addingTimeInterval(15)
                while !writerInput.isReadyForMoreMediaData {
                    guard writer.status == .writing, Date() < deadline else { throw writer.error ?? failure("HEVC encoder timed out") }
                    try await Task.sleep(for: .milliseconds(2))
                }
                guard writerInput.append(sample) else { throw writer.error ?? failure("HEVC sample append failed") }
                frames += 1
            }
            reader.cancelReading()
            writerInput.markAsFinished()
            await writer.finishWriting()
            guard writer.status == .completed, frames == 60 else { throw writer.error ?? failure("HEVC fixture failed or ended early") }
            let encoded = AVURLAsset(url: target)
            let encodedTrack = try await encoded.loadTracks(withMediaType: .video).first!
            let formats = try await encodedTrack.load(.formatDescriptions)
            guard let format = formats.first,
                  let atoms = CMFormatDescriptionGetExtension(format, extensionKey: kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms) as? [String: Any],
                  let hvcc = atoms["hvcC"] as? Data else { throw failure("Native HEVC fixture has no hvcC") }
            try hvcc.write(to: directory.appendingPathComponent("Luma-HEVC\(depth).hvcC"))
            print("Generated \(target.lastPathComponent): \(frames) native HEVC frames, hvcC luma depth \(Int(hvcc[17] & 7) + 8), \(hvcc.count) configuration bytes")
        }
    }
    static func failure(_ text: String) -> NSError { NSError(domain: "LumaHEVCFixture", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }
}
