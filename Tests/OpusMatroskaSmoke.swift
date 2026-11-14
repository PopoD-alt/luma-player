import Foundation
import AudioToolbox
import AVFoundation
import CoreMedia

private struct OpusMatroskaFailure: Error, CustomStringConvertible { let description: String }
private func check(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
    if try condition() == false { throw OpusMatroskaFailure(description: message) }
}
private func little(_ value: UInt32, _ count: Int) -> Data {
    Data((0..<count).map { UInt8((value >> ($0 * 8)) & 255) })
}
private func head(preSkip: Int, channels: Int = 2, gain: Int16 = 0, family: UInt8 = 0) -> Data {
    Data("OpusHead".utf8) + Data([1, UInt8(channels)]) + little(UInt32(preSkip), 2)
        + little(48_000, 4) + little(UInt32(UInt16(bitPattern: gain)), 2) + Data([family])
}

private struct NativeOpusFixture {
    let packets: [Data]
    let priming: Int
    let remainder: Int
    let valid: Int
}

private struct NativePCM {
    let frames: Int
    let firstPTS: Double
    let samples: [Float]
    var rms: Double { sqrt(samples.reduce(0) { $0 + Double($1) * Double($1) } / Double(max(1, samples.count))) }
}

@main struct OpusMatroskaSmoke {
    static func main() async throws {
        setbuf(stdout, nil)
        let fixtureRoot = URL(fileURLWithPath: CommandLine.arguments.dropFirst().first ?? "Tests/Fixtures", isDirectory: true)
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("Luma-Opus-Matroska-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        print("Opus test directory: \(temporary.path)")
        defer {
            if ProcessInfo.processInfo.environment["LUMA_KEEP_OPUS_TEST_OUTPUT"] != "1" {
                try? FileManager.default.removeItem(at: temporary)
            }
        }
        let caf = temporary.appendingPathComponent("original-native-opus.caf")
        let encoder = Process()
        encoder.executableURL = URL(fileURLWithPath: "/usr/bin/afconvert")
        encoder.arguments = ["-f", "caff", "-d", "opus@48000", "-c", "2", "-b", "64000",
            fixtureRoot.appendingPathComponent("Luma-Tone.wav").path, caf.path]
        try encoder.run(); encoder.waitUntilExit()
        try check(encoder.terminationStatus == 0, "Apple's native Opus fixture encoder is unavailable")
        let original = try readNative(caf)
        let sourceConfig = try OpusConfiguration.parse(head(preSkip: original.priming))
        let packets = try repack(original.packets)
        let packetFrames = try packets.map { try sourceConfig.packetSampleFrames($0) }
        let total = packetFrames.reduce(0, +)
        try check(Set(packetFrames).contains(960) && Set(packetFrames).contains(1920), "Fixture must contain both20ms and40ms Opus packets")
        try check(total == original.valid + original.priming + original.remainder, "TOC timing differs from native CAF's original packet table")
        print("Original fixture: \(original.packets.count) native packets repacked as\(packets.count) mixed20/40ms packets, \(total) raw48kHz frames")

        let cases = [("native-preskip", original.priming, original.remainder),
                     ("multi-packet-preskip", original.priming + 1920, original.remainder),
                     ("multi-packet-tail", original.priming, original.remainder + 1920),
                     ("zero-preskip", 0, original.remainder)]
        var seekFailures = [String]()
        for (name, preSkip, tail) in cases {
            let input = temporary.appendingPathComponent(name + ".mkv")
            let output = temporary.appendingPathComponent(name + ".mov")
            try makeMatroska(packets: packets, preSkip: preSkip, padding: tail).write(to: input)
            let result = try await MatroskaRemuxer.remux(input: input, output: output)
            let pcm = try await decode(output)
            let expected = total - preSkip - tail
            print("\(name) raw result: nativePCM\(pcm.frames), expected\(expected), firstPTS\(pcm.firstPTS), movieDuration\(result.duration)")
            try check(result.codecSummary.contains("Opus"), "Production result lost the Opus route")
            try check(pcm.frames == expected, "\(name): decoded\(pcm.frames)frames, expected\(expected)")
            try check(abs(pcm.firstPTS) < 1.0 / 48_000, "\(name): effective origin shifted to\(pcm.firstPTS)")
            try check(abs(result.duration - Double(expected) / 48_000) < 1.0 / 48_000, "\(name): MOV duration\(result.duration) does not match trimmed samples")
            try check(pcm.rms > 0.01 && pcm.samples.allSatisfy(\.isFinite), "\(name): native Opus decoded silence/nonfinite PCM")

            let movieAsset = AVURLAsset(url: output)
            let audioTrack = try await movieAsset.loadTracks(withMediaType: .audio).first!
            let roll = audioTrack.makeSampleCursor(presentationTimeStamp: CMTime(value: 48_000, timescale: 48_000))?.samplesRequiredForDecoderRefresh ?? 0
            if roll <= 0 {
                seekFailures.append("\(name): output lost Opus decoder-refresh/roll metadata")
                print("SEEK METADATA FAILURE:\(seekFailures.last!)")
            }

            // Native range decoding exercises the MOV's seek/preroll path. Its
            // tail must agree with the same samples from uninterrupted decode.
            let seekFrames = 48_000
            let seek = try await decode(output, startFrame: seekFrames, frameCount: expected - seekFrames)
            try check(seek.frames == expected - seekFrames, "\(name): seek decoded\(seek.frames)frames, expected\(expected - seekFrames)")
            try check(abs(seek.firstPTS - 1) < 1.0 / 48_000, "\(name): seek did not start exactly at1s")
            let expectedTail = pcm.samples.dropFirst(seekFrames * 2)
            try check(expectedTail.count == seek.samples.count, "\(name): seek output length differs from uninterrupted tail")
            var squaredError = 0.0
            for (wanted, actual) in zip(expectedTail, seek.samples) {
                let error = Double(wanted) - Double(actual)
                squaredError += error * error
            }
            let difference = sqrt(squaredError / Double(max(1, seek.samples.count)))
            if difference >= 0.0001 {
                seekFailures.append("\(name): native seek/preroll PCM differs from uninterrupted decode (RMS\(difference))")
                print("SEEK FAILURE:\(seekFailures.last!)")
            }
            print("PASS:\(name), full EBML→production remux→nativePCM \(pcm.frames)frames, exact origin/duration; 1s seek\(seek.frames)frames; roll\(roll), PCM difference\(difference)")
        }

        for (name, bytes) in [
            ("gain", makeMatroska(packets: packets, preSkip: original.priming, padding: original.remainder, gain: 1536)),
            ("mapping-family", makeMatroska(packets: packets, preSkip: original.priming, padding: original.remainder, family: 1)),
            ("negative-padding", makeMatroska(packets: packets, preSkip: original.priming, padding: -original.remainder)),
            ("oversized-padding", makeMatroska(packets: packets, preSkip: original.priming, padding: total))
        ] {
            let input = temporary.appendingPathComponent("unsupported-\(name).mkv")
            let output = temporary.appendingPathComponent("unsupported-\(name).mov")
            try bytes.write(to: input)
            do {
                _ = try await MatroskaRemuxer.remux(input: input, output: output)
                throw OpusMatroskaFailure(description: "Unsupported/malformed Opus \(name) was accepted")
            } catch is OpusConfigurationError { }
            catch is MatroskaRemuxError { }
            try check(!FileManager.default.fileExists(atPath: output.path), "Rejected Opus \(name) left an output")
        }
        print("PASS: nonzero gain, unsupported mapping family, negative and excessive DiscardPadding fail explicitly without output")
        try check(seekFailures.isEmpty, seekFailures.joined(separator: "; "))
    }

    private static func readNative(_ url: URL) throws -> NativeOpusFixture {
        var opened: AudioFileID?
        let status = AudioFileOpenURL(url as CFURL, .readPermission, 0, &opened)
        guard status == noErr, let file = opened else { throw OpusMatroskaFailure(description: "CAF open\(status)") }
        defer { AudioFileClose(file) }
        var table = AudioFilePacketTableInfo(), size = UInt32(MemoryLayout<AudioFilePacketTableInfo>.size)
        try check(AudioFileGetProperty(file, kAudioFilePropertyPacketTableInfo, &size, &table) == noErr, "CAF packet table")
        var upper: UInt32 = 0, count: UInt64 = 0
        size = 4; try check(AudioFileGetProperty(file, kAudioFilePropertyPacketSizeUpperBound, &size, &upper) == noErr, "CAF maximum packet size")
        size = 8; try check(AudioFileGetProperty(file, kAudioFilePropertyAudioDataPacketCount, &size, &count) == noErr, "CAF packet count")
        try check(upper > 0 && upper < 100_000 && count > 0 && count < 1000, "Original Opus fixture bounds")
        var packets = [Data]()
        for index in 0..<count {
            var bytes = Data(count: Int(upper)), byteCount = upper, packetCount: UInt32 = 1, description = AudioStreamPacketDescription()
            let read = bytes.withUnsafeMutableBytes { AudioFileReadPacketData(file, false, &byteCount, &description, Int64(index), &packetCount, $0.baseAddress!) }
            try check(read == noErr && packetCount == 1, "CAF compressed packet extraction")
            packets.append(Data(bytes.prefix(Int(byteCount))))
        }
        return NativeOpusFixture(packets: packets, priming: Int(table.mPrimingFrames), remainder: Int(table.mRemainderFrames), valid: Int(table.mNumberValidFrames))
    }

    private static func repack(_ source: [Data]) throws -> [Data] {
        // Original RFC6716 framing only: join adjacent compatible single-frame
        // packets into code2 VBR packets without touching range-coded payloads.
        var result = [Data](), index = 0
        while index < source.count {
            if index % 3 == 0, index + 1 < source.count,
               let first = source[index].first, let second = source[index + 1].first,
               first & 3 == 0, second & 3 == 0, first & 0xfc == second & 0xfc {
                let left = Data(source[index].dropFirst()), right = Data(source[index + 1].dropFirst())
                try check(left.count <= 1275 && right.count <= 1275, "Opus single-frame fixture size")
                let size: Data
                if left.count < 252 { size = Data([UInt8(left.count)]) }
                else {
                    let low = 252 + left.count % 4
                    size = Data([UInt8(low), UInt8((left.count - low) / 4)])
                }
                result.append(Data([first & 0xfc | 2]) + size + left + right)
                index += 2
            } else { result.append(source[index]); index += 1 }
        }
        return result
    }

    private static func makeMatroska(packets: [Data], preSkip: Int, padding: Int, gain: Int16 = 0, family: UInt8 = 0) -> Data {
        func unsigned(_ value: UInt64) -> Data {
            var bytes = Data(), begun = false
            for shift in stride(from: 56, through: 0, by: -8) {
                let byte = UInt8(truncatingIfNeeded: value >> shift)
                if byte != 0 || begun || shift == 0 { bytes.append(byte); begun = true }
            }
            return bytes
        }
        func vint(_ value: UInt64) -> Data {
            for width in 1...8 where value < (UInt64(1) << (7 * width)) - 1 {
                let marked = value | UInt64(1) << (7 * width)
                return Data((0..<width).reversed().map { UInt8(truncatingIfNeeded: marked >> ($0 * 8)) })
            }
            preconditionFailure("Synthetic EBML value too large")
        }
        func element(_ id: UInt64, _ payload: Data) -> Data { unsigned(id) + vint(UInt64(payload.count)) + payload }
        func uint(_ id: UInt64, _ value: UInt64) -> Data { element(id, unsigned(value)) }
        func float(_ value: Double) -> Data { Data((0..<8).reversed().map { UInt8(truncatingIfNeeded: value.bitPattern >> ($0 * 8)) }) }
        let identification = head(preSkip: preSkip, gain: gain, family: family)
        let configuration = try! OpusConfiguration.parse(head(preSkip: preSkip))
        let delayNS = UInt64((Double(preSkip) * 1_000_000_000 / 48_000).rounded())
        let audio = element(0xe1, element(0xb5, float(48_000)) + uint(0x9f, 2))
        let track = uint(0xd7, 1) + uint(0x73c5, 1) + uint(0x83, 2)
            + element(0x86, Data("A_OPUS".utf8)) + element(0x63a2, identification)
            + uint(0x56aa, delayNS) + uint(0x56bb, 80_000_000) + audio
        var blocks = Data(), index = 0, elapsedFrames = 0
        while index < packets.count {
            // Keep enough packets in the final BlockGroup to exercise tail
            // padding that spans more than one compressed packet.
            let count = packets.count - index <= 5 ? packets.count - index : 3
            let selected = Array(packets[index..<index + count])
            let startMS = Int16(elapsedFrames / 48)
            var block = Data([0x81, UInt8(truncatingIfNeeded: startMS >> 8), UInt8(truncatingIfNeeded: startMS), count > 1 ? 0x82 : 0x80])
            if count > 1 {
                block.append(UInt8(count - 1))
                for packet in selected.dropLast() {
                    var size = packet.count
                    while size >= 255 { block.append(255); size -= 255 }
                    block.append(UInt8(size))
                }
            }
            for packet in selected { block.append(packet) }
            let frames = selected.reduce(0) { $0 + (try! configuration.packetSampleFrames($1)) }
            if index + count == packets.count {
                let signedPadding = Int64((Double(padding) * 1_000_000_000 / 48_000).rounded())
                let signed = UInt64(bitPattern: signedPadding)
                let paddingBytes = Data((0..<8).reversed().map { UInt8(truncatingIfNeeded: signed >> ($0 * 8)) })
                let group = element(0xa1, block) + uint(0x9b, UInt64(frames / 48)) + element(0x75a2, paddingBytes)
                blocks += element(0xa0, group)
            } else { blocks += element(0xa3, block) }
            elapsedFrames += frames; index += count
        }
        let ebml = uint(0x4286, 1) + uint(0x42f7, 1) + uint(0x42f2, 4) + uint(0x42f3, 8)
            + element(0x4282, Data("matroska".utf8)) + uint(0x4287, 4) + uint(0x4285, 2)
        let info = element(0x1549a966, uint(0x2ad7b1, 1_000_000))
        let tracks = element(0x1654ae6b, element(0xae, track))
        let cluster = element(0x1f43b675, uint(0xe7, 0) + blocks)
        return element(0x1a45dfa3, ebml) + element(0x18538067, info + tracks + cluster)
    }

    private static func decode(_ url: URL, startFrame: Int = 0, frameCount: Int? = nil) async throws -> NativePCM {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else { throw OpusMatroskaFailure(description: "Missing production Opus output audio") }
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 2, AVLinearPCMIsFloatKey: true,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsNonInterleaved: false])
        reader.add(output)
        if let frameCount { reader.timeRange = CMTimeRange(start: CMTime(value: Int64(startFrame), timescale: 48_000), duration: CMTime(value: Int64(frameCount), timescale: 48_000)) }
        guard reader.startReading() else { throw reader.error ?? OpusMatroskaFailure(description: "Native production Opus decoder cannot start") }
        var frames = 0, first: Double?, samples = [Float]()
        while let sample = output.copyNextSampleBuffer() {
            if first == nil { first = CMSampleBufferGetPresentationTimeStamp(sample).seconds }
            frames += CMSampleBufferGetNumSamples(sample)
            guard let block = CMSampleBufferGetDataBuffer(sample) else { throw OpusMatroskaFailure(description: "Native Opus returned no PCM bytes") }
            let count = CMBlockBufferGetDataLength(block)
            var bytes = Data(count: count)
            let status = bytes.withUnsafeMutableBytes { CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: count, destination: $0.baseAddress!) }
            try check(status == noErr && count % 8 == 0, "Native Opus stereo PCM representation")
            bytes.withUnsafeBytes { raw in
                for offset in stride(from: 0, to: count, by: 4) { samples.append(raw.loadUnaligned(fromByteOffset: offset, as: Float.self)) }
            }
        }
        guard reader.status == .completed else { throw reader.error ?? OpusMatroskaFailure(description: "Native production Opus decode incomplete") }
        try check(samples.count == frames * 2, "Native Opus PCM channel/frame count")
        return NativePCM(frames: frames, firstPTS: first ?? .nan, samples: samples)
    }
}
