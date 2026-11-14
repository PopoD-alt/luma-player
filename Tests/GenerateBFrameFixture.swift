import Foundation
import AVFoundation
import CoreMedia
import CoreVideo
import VideoToolbox

/// Re-encodes only Luma's original synthetic demo. No downloaded/user media or
/// third-party codecs. Run the compiled generator with [fixture-directory].
@main struct GenerateBFrameFixture {
    struct Packet: Codable { let pts: Double; let dts: Double; let duration: Double; let keyframe: Bool }
    struct Manifest: Codable {
        let source: String, mediaFile: String, codec: String, encoderSelection: String, packetOrder: String
        let width: Int, height: Int, duration: Double, frameRate: Double
        let encodedFrames: Int, decodedFrames: Int, presentationTimestampInversions: Int
        let packets: [Packet]
    }
    static func failure(_ text: String) -> NSError { NSError(domain: "LumaBFrameFixture", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }
    static func main() async throws {
        setbuf(stdout, nil)
        let root = CommandLine.arguments.dropFirst().first.map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? URL(fileURLWithPath: #filePath).deletingLastPathComponent().appendingPathComponent("Fixtures", isDirectory: true)
        let sourceURL = root.appendingPathComponent("Luma-Demo.mp4")
        let outputURL = root.appendingPathComponent("Luma-BFrames.mp4")
        guard FileManager.default.fileExists(atPath: sourceURL.path) else { throw failure("Generate the original Luma-Demo.mp4 fixture first.") }
        let source = AVURLAsset(url: sourceURL)
        guard let track = try await source.loadTracks(withMediaType: .video).first else { throw failure("Original demo has no video track") }
        let size = try await track.load(.naturalSize)
        let fps = Double(try await track.load(.nominalFrameRate))
        guard fps > 0 else { throw failure("Original demo has no known frame rate") }
        var manifest: Manifest?
        for hardwareAllowed in [true, false] {
            let selection = hardwareAllowed ? "hardware allowed" : "software only"
            print("Encoding original B-frame fixture: \(selection)")
            do {
                let count = try await encode(source: source, track: track, output: outputURL, width: Int(size.width), height: Int(size.height), fps: fps, hardwareAllowed: hardwareAllowed)
                let packets = try await compressedPackets(outputURL)
                let inversions = zip(packets.dropFirst(), packets).filter { $0.pts < $1.pts }.count
                guard inversions > 0 else { print("Encoder emitted no reordered packets; trying next native encoder selection."); continue }
                guard packets.count == count, zip(packets.dropFirst(), packets).allSatisfy({ $0.dts > $1.dts }),
                      packets.allSatisfy({ $0.pts.isFinite && $0.dts.isFinite && $0.dts <= $0.pts + 0.000001 }) else {
                    throw failure("Encoded packet count or DTS validity failed")
                }
                let decoded = try await nativeDecodeCount(outputURL)
                guard decoded == count else { throw failure("Native decoder frame count differs from encoded count") }
                let duration = try await AVURLAsset(url: outputURL).load(.duration).seconds
                manifest = Manifest(source: sourceURL.lastPathComponent, mediaFile: outputURL.lastPathComponent,
                    codec: "H.264 High (Apple system encoder)", encoderSelection: selection, packetOrder: "decode order; retain this order when copying compressed packets",
                    width: Int(size.width), height: Int(size.height), duration: duration, frameRate: fps,
                    encodedFrames: count, decodedFrames: decoded, presentationTimestampInversions: inversions, packets: packets)
                break
            } catch {
                if !hardwareAllowed { throw error }
                print("Native encoder attempt failed: \(error.localizedDescription)")
            }
        }
        guard let manifest else {
            try? FileManager.default.removeItem(at: outputURL)
            throw failure("Neither available Apple encoder selection produced B-frame reordering; no mislabeled fixture was retained.")
        }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(manifest).write(to: root.appendingPathComponent("Luma-BFrames.json"), options: .atomic)
        print("PASS: \(manifest.width)×\(manifest.height), \(manifest.duration)s, \(manifest.frameRate)fps, \(manifest.encodedFrames) decode-order packets, \(manifest.presentationTimestampInversions) PTS inversions, \(manifest.decodedFrames) native decoded frames")
        print("Wrote \(outputURL.path) and Luma-BFrames.json")
    }

    static func encode(source: AVAsset, track: AVAssetTrack, output: URL, width: Int, height: Int, fps: Double, hardwareAllowed: Bool) async throws -> Int {
        if FileManager.default.fileExists(atPath: output.path) { try FileManager.default.removeItem(at: output) }
        let reader = try AVAssetReader(asset: source)
        let read = AVAssetReaderTrackOutput(track: track, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        reader.add(read)
        let writer = try AVAssetWriter(outputURL: output, fileType: .mp4)
        let settings: [String: Any] = [AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: width, AVVideoHeightKey: height,
            AVVideoEncoderSpecificationKey: [kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder as String: hardwareAllowed],
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 1_500_000, AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
                AVVideoExpectedSourceFrameRateKey: fps, AVVideoMaxKeyFrameIntervalKey: Int(fps * 2), AVVideoAllowFrameReorderingKey: true]]
        let write = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        write.expectsMediaDataInRealTime = false
        write.mediaTimeScale = 1_000_000_000
        guard writer.canAdd(write) else { throw failure("Native writer cannot add the H.264 encoder") }
        writer.add(write)
        guard reader.startReading(), writer.startWriting() else { throw reader.error ?? writer.error ?? failure("Cannot start native encoding") }
        writer.startSession(atSourceTime: .zero)
        var frames = 0
        while let sample = read.copyNextSampleBuffer() {
            if CMSampleBufferGetNumSamples(sample) == 0 { continue }
            while !write.isReadyForMoreMediaData {
                guard writer.status == .writing else { throw writer.error ?? failure("Native encoding stopped") }
                try await Task.sleep(nanoseconds: 2_000_000)
            }
            guard write.append(sample) else { throw writer.error ?? failure("Cannot append native encoder input") }
            frames += 1
        }
        guard reader.status == .completed else { throw reader.error ?? failure("Cannot decode original fixture") }
        write.markAsFinished(); await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? failure("Cannot finalize encoded fixture") }
        return frames
    }

    static func compressedPackets(_ url: URL) async throws -> [Packet] {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else { throw failure("Encoded fixture lost video") }
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? failure("Cannot inspect encoded packet timing") }
        var packets: [Packet] = []
        while let sample = output.copyNextSampleBuffer() {
            if CMSampleBufferGetNumSamples(sample) == 0 { continue }
            guard CMSampleBufferGetNumSamples(sample) == 1 else { throw failure("Fixture inspection expects one AVC picture per sample buffer") }
            let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[String: Any]]
            let nonSync = attachments?.first?[kCMSampleAttachmentKey_NotSync as String] as? Bool ?? false
            packets.append(Packet(pts: CMSampleBufferGetPresentationTimeStamp(sample).seconds,
                dts: CMSampleBufferGetDecodeTimeStamp(sample).seconds, duration: CMSampleBufferGetDuration(sample).seconds, keyframe: !nonSync))
        }
        guard reader.status == .completed else { throw reader.error ?? failure("Cannot read encoded packets") }
        return packets
    }

    static func nativeDecodeCount(_ url: URL) async throws -> Int {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else { throw failure("No video for decode verification") }
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? failure("Cannot start native fixture verification") }
        var count = 0, previousPTS = -Double.infinity
        while let sample = output.copyNextSampleBuffer() {
            if CMSampleBufferGetNumSamples(sample) == 0 { continue }
            let pts = CMSampleBufferGetPresentationTimeStamp(sample).seconds
            guard CMSampleBufferGetImageBuffer(sample) != nil, pts > previousPTS else { throw failure("Native fixture decode lacks pixels or presentation order") }
            previousPTS = pts; count += 1
        }
        guard reader.status == .completed else { throw reader.error ?? failure("Native fixture decode failed") }
        return count
    }
}
