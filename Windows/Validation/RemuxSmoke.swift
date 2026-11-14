import Foundation
import AVFoundation
import CoreVideo
import CryptoKit

private func failure(_ text: String) -> NSError { NSError(domain: "WindowsRemuxValidation", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }

@main struct WindowsRemuxValidation {
    static func main() async throws {
        setbuf(stdout, nil)
        guard CommandLine.arguments.count == 3 else { throw failure("Provide exported remux directory and fixture directory") }
        let export = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let fixtures = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
        let reference = try await video(fixtures.appendingPathComponent("Luma-Demo.mp4"))
        guard reference.hashes.count == 360 else { throw failure("Unexpected native reference frame count") }
        for name in ["Luma-remux-video.mp4", "Luma-remux-av.mp4"] {
            let result = try await video(export.appendingPathComponent(name))
            guard result.hashes == reference.hashes, result.width == reference.width, result.height == reference.height, abs(result.first) < 0.002 else { throw failure("Remuxed video pixels, order or movie origin differ: \(name), frames=\(result.hashes.count), firstPTS=\(result.first)") }
            print("PASS: independent AVFoundation decode \(name): all 360 frames match original MP4 pixel hashes; edit-list movie origin is zero")
        }
        for (source, converted, count) in [("Luma-BFrames.mp4", "Luma-remux-bframes.mp4", 360), ("Luma-HEVC8.mov", "Luma-remux-hevc8.mp4", 60), ("Luma-HEVC10.mov", "Luma-remux-hevc10.mp4", 60)] {
            let original = try await video(fixtures.appendingPathComponent(source))
            let remuxed = try await video(export.appendingPathComponent(converted))
            guard original.hashes.count == count, remuxed.hashes == original.hashes, remuxed.width == original.width, remuxed.height == original.height, abs(remuxed.first) < 0.002 else { throw failure("Actual B-frame/HEVC decode mismatch: \(source), original=\(original.hashes.count), remux=\(remuxed.hashes.count), firstPTS=\(remuxed.first)") }
            print("PASS: independent \(source)→Matroska→MP4 decode: all \(count) frame pixel hashes and normalized movie origin match; this is native macOS proof, not a Windows codec-availability claim")
        }
        let asset = AVURLAsset(url: export.appendingPathComponent("Luma-remux-av.mp4"))
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else { throw failure("Remux lost AAC audio") }
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [AVFormatIDKey: kAudioFormatLinearPCM, AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false, AVLinearPCMIsNonInterleaved: false])
        let formats = try await track.load(.formatDescriptions)
        guard let format = formats.first, let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(format) else { throw failure("AAC format description unavailable") }
        let sampleRate = asbd.pointee.mSampleRate, channels = Int(asbd.pointee.mChannelsPerFrame)
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? failure("AAC decode did not start") }
        var frames = 0, first: Double?, firstSignal: Double?, peak = 0
        while let sample = output.copyNextSampleBuffer() {
            if first == nil { first = CMSampleBufferGetPresentationTimeStamp(sample).seconds }
            frames += CMSampleBufferGetNumSamples(sample)
            if let block = CMSampleBufferGetDataBuffer(sample) {
                let size = CMBlockBufferGetDataLength(block)
                var bytes = [UInt8](repeating: 0, count: size)
                guard CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: size, destination: &bytes) == noErr else { throw failure("AAC PCM data unavailable") }
                for i in stride(from: 0, to: bytes.count - 1, by: 2) {
                    let value = abs(Int(Int16(bitPattern: UInt16(bytes[i]) | UInt16(bytes[i + 1]) << 8)))
                    peak = max(peak, value)
                    if firstSignal == nil && value > 100 { firstSignal = CMSampleBufferGetPresentationTimeStamp(sample).seconds + Double(i / (channels * 2)) / sampleRate }
                }
            }
        }
        guard reader.status == .completed, frames > 140000, frames < 165000, peak > 100,
            let start = firstSignal, abs(start - 0.25) < 0.025 else { throw reader.error ?? failure("AAC samples, non-silence or 250ms A/V offset were not preserved: frames=\(frames), peak=\(peak), firstSignal=\(String(describing: firstSignal)), firstBuffer=\(String(describing: first))") }
        print("PASS: independent AAC decode yields \(frames) PCM frames; first non-silent signal at \(start)s preserves the 250ms A/V offset within 25ms. Native reader fills leading edits; exact AAC priming/trim conformance and Windows speakers remain separate checks")
    }

    static func video(_ url: URL) async throws -> (hashes: [String], first: Double, width: Int, height: Int) {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else { throw failure("Missing video track: \(url.lastPathComponent)") }
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? failure("Video decode did not start") }
        var hashes = [String](), first: Double?, width = 0, height = 0
        while let sample = output.copyNextSampleBuffer() {
            if first == nil { first = CMSampleBufferGetPresentationTimeStamp(sample).seconds }
            guard let image = CMSampleBufferGetImageBuffer(sample) else { throw failure("Video pixels unavailable") }
            width = CVPixelBufferGetWidth(image); height = CVPixelBufferGetHeight(image)
            guard width > 0, height > 0, width <= 8192, height <= 8192, width * height <= 16_777_216 else { throw failure("Video output geometry exceeds review bounds") }
            CVPixelBufferLockBaseAddress(image, .readOnly)
            guard let base = CVPixelBufferGetBaseAddress(image) else { CVPixelBufferUnlockBaseAddress(image, .readOnly); throw failure("Video pixels unavailable") }
            let stride = CVPixelBufferGetBytesPerRow(image)
            var data = Data()
            for row in 0..<height { data.append(base.advanced(by: row * stride).assumingMemoryBound(to: UInt8.self), count: width * 4) }
            CVPixelBufferUnlockBaseAddress(image, .readOnly)
            hashes.append(SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined())
        }
        guard reader.status == .completed else { throw reader.error ?? failure("Video decode incomplete") }
        return (hashes, first ?? .nan, width, height)
    }
}
