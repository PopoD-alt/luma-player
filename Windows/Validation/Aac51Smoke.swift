import Foundation
import AVFoundation
import AudioToolbox

private func failure(_ text: String) -> NSError {
    NSError(domain: "LumaOriginalAac51Validation", code: 1,
            userInfo: [NSLocalizedDescriptionKey: text])
}

/// Independent native decoding of Luma's original AAC-LC zero-band fixture.
/// This validates that one authored silent six-channel stream is decodable;
/// it is not proof of a general AAC decoder, Windows playback or speaker output.
@main struct Aac51Smoke {
    static func main() async throws {
        setbuf(stdout, nil)
        guard CommandLine.arguments.count == 2 else { throw failure("Provide the original AAC5.1 silence M4A file") }
        let url = URL(fileURLWithPath: CommandLine.arguments[1])
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .audio).first,
              let sourceFormat = try await track.load(.formatDescriptions).first,
              let sourcePointer = CMAudioFormatDescriptionGetStreamBasicDescription(sourceFormat) else {
            throw failure("Compressed AAC source format unavailable")
        }
        let source = sourcePointer.pointee
        guard source.mFormatID == kAudioFormatMPEG4AAC,
              source.mSampleRate == 48_000, source.mChannelsPerFrame == 6 else {
            throw failure("Source is not native AAC48kHz/6channels: format=\(source.mFormatID), rate=\(source.mSampleRate), channels=\(source.mChannelsPerFrame)")
        }
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 48_000,
            AVNumberOfChannelsKey: 6,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ])
        output.alwaysCopiesSampleData = false
        guard reader.canAdd(output) else { throw failure("Native reader rejects six-channel PCM output") }
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? failure("Native AAC decode did not start") }
        var frames = 0, buffers = 0, bytesTotal = 0, nonzeroBytes = 0
        var first: Double?
        while let sample = output.copyNextSampleBuffer() {
            guard let format = CMSampleBufferGetFormatDescription(sample),
                  let pointer = CMAudioFormatDescriptionGetStreamBasicDescription(format) else {
                throw failure("Decoded PCM format unavailable")
            }
            let decoded = pointer.pointee
            guard decoded.mFormatID == kAudioFormatLinearPCM,
                  decoded.mChannelsPerFrame == 6, decoded.mSampleRate == 48_000,
                  decoded.mBitsPerChannel == 16, decoded.mBytesPerFrame == 12,
                  decoded.mFormatFlags & kAudioFormatFlagIsFloat == 0,
                  decoded.mFormatFlags & kAudioFormatFlagIsNonInterleaved == 0 else {
                throw failure("Native output was not interleaved PCM16/6channels: \(decoded)")
            }
            let count = CMSampleBufferGetNumSamples(sample)
            guard count > 0, count <= 1_048_576,
                  let block = CMSampleBufferGetDataBuffer(sample) else {
                throw failure("Decoded PCM samples/data unavailable")
            }
            let size = CMBlockBufferGetDataLength(block)
            guard size == count * 6 * 2 else { throw failure("Decoded byte/frame count disagrees with six-channel PCM") }
            var bytes = [UInt8](repeating: 0, count: size)
            guard CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: size, destination: &bytes) == noErr else {
                throw failure("Unable to inspect actual native PCM bytes")
            }
            nonzeroBytes += bytes.reduce(0) { $0 + ($1 == 0 ? 0 : 1) }
            frames += count; bytesTotal += size; buffers += 1
            if first == nil { first = CMSampleBufferGetPresentationTimeStamp(sample).seconds }
            guard frames <= 120_000 else { throw failure("Original two-second AAC fixture decoded beyond its expected bound") }
        }
        guard reader.status == .completed, frames >= 48_000, buffers > 0,
              nonzeroBytes == 0, let first, first.isFinite else {
            throw reader.error ?? failure("Native AAC5.1 decode incomplete/non-silent: frames=\(frames), buffers=\(buffers), nonzeroBytes=\(nonzeroBytes)")
        }
        print("PASS: independent AVFoundation decoder accepted original AAC-LC48kHz/5.1 source and produced \(frames) six-channel interleaved PCM16 frames (\(bytesTotal) bytes, \(buffers) buffers), all samples exactly zero; firstPTS=\(first)s")
        print("SCOPE: original zero-spectral-band silence fixture only; no general AAC compressed decoder, non-silent conformance, Windows decoding, A/V synchronization or audible-output claim")
        if let video = try await asset.loadTracks(withMediaType: .video).first {
            let videoReader = try AVAssetReader(asset: asset)
            let pixels = AVAssetReaderTrackOutput(track: video, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange])
            videoReader.add(pixels)
            guard videoReader.startReading() else { throw videoReader.error ?? failure("Main10 decoder startup failed") }
            var count = 0
            while let sample = pixels.copyNextSampleBuffer() {
                guard let image = CMSampleBufferGetImageBuffer(sample), CVPixelBufferGetWidth(image) == 1920,
                    CVPixelBufferGetHeight(image) == 804, CVPixelBufferGetPixelFormatType(image) == kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange,
                    abs(CMSampleBufferGetPresentationTimeStamp(sample).seconds - Double(count) / 25) < 0.0001 else { throw failure("Main10 P010 display geometry, pixel depth or25fps timing mismatch") }
                count += 1
                guard count <= 50 else { throw failure("Unexpected HEVC sample count") }
            }
            guard videoReader.status == .completed, count == 50 else { throw videoReader.error ?? failure("Incomplete Main10 decoding") }
            print("PASS: independent native decoder returned all 50 P010 10-bit frames at 1920x804 and 25fps from the supplied original HEVC/AAC5.1 MP4 fixture")
        }
    }
}
