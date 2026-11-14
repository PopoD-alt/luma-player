import Foundation
import AudioToolbox
import CryptoKit

/// Independent hand-assembled compressed vectors are compared with exact
/// arithmetic goldens and Apple's native decoder. Apple's complete-block WAVE
/// decoder ignores fact and discards short final blocks; those two behaviors
/// are tested against independent goldens, then a native PCM WAVE round-trip.
@main
struct MSADPCMSmoke {
    struct Manifest: Decodable { let vectors: [Vector] }
    struct Vector: Decodable {
        let name: String
        let channels: Int, sample_rate: Int, block_align: Int, samples_per_block: Int
        let coefficients: [[Int]]
        let sample_count: Int, expected_samples: [Int16]
        let expected_pcm_sha256: String
        let native_reference_requested: Bool
    }
    enum Failure: Error { case check(String), native(String, OSStatus) }

    static func main() async throws {
        let root = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Tests/Fixtures/ms-adpcm")
        let manifest = try JSONDecoder().decode(Manifest.self, from: Data(contentsOf: root.appendingPathComponent("manifest.json")))
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("luma-ms-adpcm-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }
        var nativeReferences = 0, roundTrips = 0
        for vector in manifest.vectors {
            let blocks = try Data(contentsOf: root.appendingPathComponent(vector.name + ".blocks"))
            let pairs = vector.coefficients.map { ($0[0], $0[1]) }
            let result = try OriginalMSADPCM.decode(blocks: blocks, channels: vector.channels, blockAlign: vector.block_align,
                samplesPerBlock: vector.samples_per_block, coefficients: pairs, sampleCount: vector.sample_count)
            try check(result == vector.expected_samples, "Exact PCM mismatch: \(vector.name)")
            let pcm = littlePCM(result)
            let goldenPCM = try Data(contentsOf: root.appendingPathComponent(vector.name + ".pcm"))
            try check(pcm == goldenPCM, "Golden PCM bytes: \(vector.name)")
            try check(SHA256.hash(data: pcm).map { String(format: "%02x", $0) }.joined() == vector.expected_pcm_sha256,
                      "Golden PCM hash: \(vector.name)")
            if vector.native_reference_requested {
                let native = try nativePCM(root.appendingPathComponent(vector.name + ".wav"))
                // Native MS ADPCM does not honor fact, so compare the entire
                // requested prefix, rather than claiming native fact proof.
                try check(native.channels == vector.channels && native.rate == Double(vector.sample_rate), "Native format: \(vector.name)")
                try check(Array(native.samples.prefix(result.count)) == result, "Native compressed reference: \(vector.name)")
                let complete = blocks.count / vector.block_align
                try check(native.samples.count == complete * vector.samples_per_block * vector.channels,
                          "Native complete-block length: \(vector.name)")
                nativeReferences += 1
            }
            let wave = pcmWave(result, channels: vector.channels, rate: vector.sample_rate)
            let url = temporary.appendingPathComponent(vector.name + ".wav")
            try wave.write(to: url)
            let roundTrip = try nativePCM(url)
            try check(roundTrip.samples == result && roundTrip.channels == vector.channels && roundTrip.rate == Double(vector.sample_rate),
                      "Native lossless PCM round-trip: \(vector.name)")
            roundTrips += 1
            print("PASS: \(vector.name), \(result.count / vector.channels) exact frames / \(vector.channels) channel(s)")
        }
        try malformedTests()
        try predictorTests()
        try await cancellationTests()
        print("PASS: \(manifest.vectors.count) independent compressed goldens; \(nativeReferences) native compressed reference prefixes; \(roundTrips) exact native PCM WAVE round-trips")
        print("PASS: all seven standard predictors, custom coefficients, partial mono/stereo blocks, signed initial delta, fact trimming, malformed input, overflow bounds and cancellation")
        print("NOTE: native MS ADPCM drops short blocks and ignores fact; exact independent goldens establish those Luma behaviors")
    }

    static func malformedTests() throws {
        let coefficients = OriginalMSADPCM.standardCoefficients
        let header = encodedHeader(predictors: [0], deltas: [16], newest: [100], oldest: [84])
        func decode(_ data: Data = Data(), channels: Int = 1, align: Int = 7, spp: Int = 2,
                    pairs: [(Int, Int)]? = nil, count: Int? = nil) throws -> [Int16] {
            try OriginalMSADPCM.decode(blocks: data, channels: channels, blockAlign: align,
                samplesPerBlock: spp, coefficients: pairs ?? coefficients, sampleCount: count)
        }
        func rejects(_ name: String, _ action: () throws -> [Int16]) throws {
            do { _ = try action(); throw Failure.check("Accepted malformed \(name)") }
            catch is MSADPCMError { }
        }
        for channels in [0,3,Int.max] { try rejects("channel count \(channels)") { try decode(header, channels: channels) } }
        for align in [-1,0,6,65536,Int.max] { try rejects("block alignment \(align)") { try decode(header, align: align) } }
        for spp in [-1,0,1,3,65536,Int.max] { try rejects("samplesPerBlock \(spp)") { try decode(header, spp: spp) } }
        try rejects("empty block data") { try decode() }
        try rejects("empty coefficient table") { try decode(header, pairs: []) }
        try rejects("oversized coefficient table") { try decode(header, pairs: Array(repeating: (256,0), count: 257)) }
        for pair in [(32768,0),(-32769,0),(0,Int.max),(Int.min,0)] {
            try rejects("coefficient outside signed16") { try decode(header, pairs: [pair]) }
        }
        var invalid = header; invalid[0] = 7
        try rejects("undeclared predictor") { try decode(invalid) }
        invalid[0] = 255
        try rejects("maximum undeclared predictor") { try decode(invalid) }
        for length in 1..<7 { try rejects("truncated mono header \(length)") { try decode(Data(header.prefix(length))) } }
        let stereo = encodedHeader(predictors: [0,0], deltas: [16,16], newest: [100,-100], oldest: [84,-84])
        for length in 1..<14 { try rejects("truncated stereo header \(length)") { try decode(Data(stereo.prefix(length)), channels: 2, align: 14) } }
        try rejects("negative fact") { try decode(header, count: -1) }
        try rejects("fact beyond available") { try decode(header, count: 3) }
        try check(try decode(header) == [84,100], "Mono header-only block")
        try check(try decode(stereo, channels: 2, align: 14) == [84,-84,100,-100], "Stereo header-only block")
        try check(try decode(header, count: 0).isEmpty, "Zero fact count")
        try check(try decode(stereo, channels: 2, align: 14, count: 1) == [84,-84], "Fact units are frames per channel")
        let partialStereo = stereo + Data([0x1f,0x1f])
        try check(try decode(partialStereo, channels: 2, align: 256, spp: 244) == [84,-84,100,-100,116,-116,132,-132], "Stereo partial final block")
        // A malformed later block still fails even when fact would trim it.
        let invalidTail = header + Data([255,16,0,100,0,84,0])
        try rejects("malformed block after fact trim") { try decode(invalidTail, count: 1) }
        let explosive = encodedHeader(predictors: [0], deltas: [32767], newest: [0], oldest: [0]) + Data(repeating: 0x77, count: 249)
        do {
            _ = try decode(explosive, align: 256, spp: 500)
            throw Failure.check("Adaptive overflow not rejected")
        } catch MSADPCMError.capacity { }
        var padded = Data([0,0,0]); padded.append(header)
        try check(try decode(padded[3...]) == [84,100], "Data with nonzero startIndex")
    }

    static func predictorTests() throws {
        let expected: [[Int16]] = [
            [128,256,272,272], [128,256,400,544], [128,256,16,0],
            [128,256,240,244], [128,256,256,240], [128,256,372,460], [128,256,292,215]
        ]
        for predictor in 0..<7 {
            let data = encodedHeader(predictors: [UInt8(predictor)], deltas: [16], newest: [256], oldest: [128]) + Data([0x10])
            let result = try OriginalMSADPCM.decode(blocks: data, channels: 1, blockAlign: 256, samplesPerBlock: 500,
                coefficients: OriginalMSADPCM.standardCoefficients)
            try check(result == expected[predictor], "Standard predictor \(predictor)")
        }
        let table = Array(repeating: (256,0), count: 255) + [(0,256)]
        let custom = encodedHeader(predictors: [255], deltas: [16], newest: [-25], oldest: [75]) + Data([0])
        try check(try OriginalMSADPCM.decode(blocks: custom, channels: 1, blockAlign: 256, samplesPerBlock: 500,
            coefficients: table) == [75,-25,75,-25], "Maximum declared custom predictor index")
    }

    static func cancellationTests() async throws {
        let header = encodedHeader(predictors: [0], deltas: [16], newest: [0], oldest: [0])
        let cancelled = Task.detached {
            withUnsafeCurrentTask { $0?.cancel() }
            return try OriginalMSADPCM.decode(blocks: header, channels: 1, blockAlign: 7, samplesPerBlock: 2,
                coefficients: OriginalMSADPCM.standardCoefficients)
        }
        do { _ = try await cancelled.value; throw Failure.check("Entry cancellation ignored") }
        catch is CancellationError { }
        var large = Data()
        let safeBlock = header + Data(repeating: 0, count: 249)
        for _ in 0..<16384 { large.append(safeBlock) }
        let stream = large
        let running = Task.detached {
            try OriginalMSADPCM.decode(blocks: stream, channels: 1, blockAlign: 256, samplesPerBlock: 500,
                coefficients: OriginalMSADPCM.standardCoefficients)
        }
        try await Task.sleep(nanoseconds: 2_000_000)
        running.cancel()
        do { _ = try await running.value; throw Failure.check("Stream cancellation ignored") }
        catch is CancellationError { }
    }

    static func encodedHeader(predictors: [UInt8], deltas: [UInt16], newest: [Int16], oldest: [Int16]) -> Data {
        var result = Data(predictors)
        for value in deltas { append16(value, to: &result) }
        for value in newest { append16(UInt16(bitPattern: value), to: &result) }
        for value in oldest { append16(UInt16(bitPattern: value), to: &result) }
        return result
    }
    static func littlePCM(_ samples: [Int16]) -> Data {
        var data = Data(); data.reserveCapacity(samples.count * 2)
        for sample in samples { append16(UInt16(bitPattern: sample), to: &data) }
        return data
    }
    static func append16(_ value: UInt16, to data: inout Data) { data.append(UInt8(truncatingIfNeeded: value)); data.append(UInt8(value >> 8)) }
    static func append32(_ value: UInt32, to data: inout Data) { for shift in stride(from: 0, to: 32, by: 8) { data.append(UInt8(truncatingIfNeeded: value >> shift)) } }
    static func pcmWave(_ samples: [Int16], channels: Int, rate: Int) -> Data {
        let pcm = littlePCM(samples)
        var wave = Data("RIFF".utf8); append32(UInt32(36 + pcm.count), to: &wave)
        wave.append(Data("WAVEfmt ".utf8)); append32(16, to: &wave); append16(1, to: &wave)
        append16(UInt16(channels), to: &wave); append32(UInt32(rate), to: &wave); append32(UInt32(rate * channels * 2), to: &wave)
        append16(UInt16(channels * 2), to: &wave); append16(16, to: &wave)
        wave.append(Data("data".utf8)); append32(UInt32(pcm.count), to: &wave); wave.append(pcm)
        return wave
    }

    static func nativePCM(_ url: URL) throws -> (samples: [Int16], channels: Int, rate: Double) {
        func checked(_ status: OSStatus, _ operation: String) throws {
            if status != noErr { throw Failure.native(operation + ": " + url.lastPathComponent, status) }
        }
        var opened: ExtAudioFileRef?
        try checked(ExtAudioFileOpenURL(url as CFURL, &opened), "ExtAudioFileOpenURL")
        guard let file = opened else { throw Failure.check("Native file handle missing") }
        defer { ExtAudioFileDispose(file) }
        var source = AudioStreamBasicDescription(), size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        try checked(ExtAudioFileGetProperty(file, kExtAudioFileProperty_FileDataFormat, &size, &source), "Native ASBD")
        let channels = Int(source.mChannelsPerFrame)
        try check(channels > 0 && channels <= 2, "Unexpected native channel count")
        var client = AudioStreamBasicDescription(mSampleRate: source.mSampleRate, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked, mBytesPerPacket: UInt32(channels * 2),
            mFramesPerPacket: 1, mBytesPerFrame: UInt32(channels * 2), mChannelsPerFrame: UInt32(channels), mBitsPerChannel: 16, mReserved: 0)
        try checked(ExtAudioFileSetProperty(file, kExtAudioFileProperty_ClientDataFormat, UInt32(MemoryLayout.size(ofValue: client)), &client), "Native PCM client")
        var samples = [Int16](), buffer = [Int16](repeating: 0, count: 1024 * channels)
        while true {
            var frames: UInt32 = 1024
            try buffer.withUnsafeMutableBytes { raw in
                var list = AudioBufferList(mNumberBuffers: 1, mBuffers: AudioBuffer(mNumberChannels: UInt32(channels), mDataByteSize: UInt32(raw.count), mData: raw.baseAddress))
                try checked(ExtAudioFileRead(file, &frames, &list), "Native decode")
            }
            if frames == 0 { break }
            samples.append(contentsOf: buffer.prefix(Int(frames) * channels))
            try check(samples.count < 1_000_000, "Unexpected native decode size")
        }
        return (samples, channels, source.mSampleRate)
    }
    static func check(_ condition: Bool, _ detail: String) throws { if !condition { throw Failure.check(detail) } }
}
