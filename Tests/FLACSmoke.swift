import Foundation

@main
struct FLACSmoke {
    struct Golden: Decodable {
        let name: String, file: String
        let sampleRate: Int, channels: Int, bitsPerSample: Int
        let samples: [Int32]
        let features: [String]
    }
    enum Failure: Error { case check(String) }

    static func main() async throws {
        let root = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Tests/Fixtures/flac")
        let goldens = try JSONDecoder().decode([Golden].self, from: Data(contentsOf: root.appendingPathComponent("manifest.json")))
        var features = Set<String>()
        for golden in goldens {
            let encoded = try Data(contentsOf: root.appendingPathComponent(golden.file))
            let decoded = try OriginalFLACDecoder.decode(encoded)
            try check(decoded.channels == golden.channels && decoded.sampleRate == golden.sampleRate && decoded.bitsPerSample == golden.bitsPerSample,
                      "Metadata mismatch in \(golden.name)")
            try check(decoded.samples == golden.samples, "Exact PCM mismatch in \(golden.name)")
            let expectedDuration = Double(golden.samples.count / golden.channels) / Double(golden.sampleRate)
            try check(abs(decoded.duration - expectedDuration) < 1e-12, "Duration mismatch")
            try verifyWave(decoded.pcmWave(), original: decoded)
            features.formUnion(golden.features)
            print("PASS: \(golden.file), \(golden.samples.count) exact samples + lossless PCM WAVE")
        }
        let constant = try Data(contentsOf: root.appendingPathComponent("constant-8.flac"))
        for length in 0..<constant.count {
            try rejects(Data(constant.prefix(length)), "truncation at byte \(length)")
        }
        let start = try audioStart(constant)
        var corrupt = constant
        corrupt[start + 6] ^= 1
        try rejects(corrupt, "header CRC8", containing: "CRC8")
        corrupt = constant; corrupt[corrupt.count - 1] ^= 1
        try rejects(corrupt, "frame CRC16", containing: "CRC16")
        corrupt = constant; corrupt[26] ^= 1
        try rejects(corrupt, "audio MD5", containing: "MD5")
        corrupt = constant; corrupt[8] = 0; corrupt[9] = 1
        try rejects(corrupt, "invalid STREAMINFO block limits")
        corrupt = constant; corrupt[4] = 0xff
        try rejects(corrupt, "forbidden metadata type")
        corrupt = constant; corrupt[start + 7] |= 0x80
        try rejects(corrupt, "reserved subframe bit", containing: "reserved subframe")
        corrupt = constant; corrupt[start + 7] = 4
        try rejects(corrupt, "reserved subframe encoding", containing: "Reserved FLAC subframe")
        corrupt = constant; corrupt[start + 4] = 0x80
        try rejects(corrupt, "invalid coded number", containing: "coded number")
        corrupt = constant
        corrupt[21] |= 15
        for offset in 22...25 { corrupt[offset] = 0xff }
        do {
            _ = try OriginalFLACDecoder.decode(corrupt)
            throw Failure.check("Huge declared output was accepted")
        } catch CodecFailure.capacity { }
        let originalCount = constant[25]
        corrupt = constant; corrupt[25] = originalCount + 1
        try rejects(corrupt, "declared total exceeds actual samples", containing: "sample count")
        corrupt = constant; corrupt[25] = originalCount - 1
        try rejects(corrupt, "actual samples exceed declared total", containing: "sample count")
        corrupt = constant; corrupt.append(0)
        try rejects(corrupt, "trailing garbage")

        let fixed = try Data(contentsOf: root.appendingPathComponent("fixed-order-1.flac"))
        let fixedStart = try audioStart(fixed)
        corrupt = fixed
        // Fixed-order1 header is followed by one signed16-bit warmup; then
        // the residual method's first two bits must be00 or01.
        corrupt[fixedStart + 10] |= 0xc0
        try rejects(corrupt, "reserved residual method", containing: "residual coding")
        let lpc = try Data(contentsOf: root.appendingPathComponent("lpc-order2.flac"))
        let lpcStart = try audioStart(lpc)
        corrupt = lpc; corrupt[lpcStart + 12] |= 0x08
        try rejects(corrupt, "negative LPC shift", containing: "negative LPC")

        // Sliced Data has nonzero indices; it must decode identically.
        var padded = Data([0,0,0]); padded.append(constant)
        let slice = padded[3...]
        try check(try OriginalFLACDecoder.decode(slice).samples == OriginalFLACDecoder.decode(constant).samples, "Data slice handling")
        let cancelled = Task.detached {
            withUnsafeCurrentTask { $0?.cancel() }
            return try OriginalFLACDecoder.decode(constant)
        }
        do {
            _ = try await cancelled.value
            throw Failure.check("Cancelled FLAC worker completed instead of cancelling")
        } catch is CancellationError { }
        print("PASS: \(goldens.count) golden streams; \(features.count) feature labels; exact8/16/24/32-bit and padded PCM; \(constant.count) truncated inputs; CRC8/CRC16/MD5, limits, reserved encodings, bad numbering and malformed totals rejected")
        print("PASS: cancelled FLAC worker propagates CancellationError")
    }

    static func verifyWave(_ wave: Data, original: DecodedFLAC) throws {
        func little(_ offset: Int, _ count: Int) -> UInt64 {
            var value: UInt64 = 0
            for byte in 0..<count { value |= UInt64(wave[offset + byte]) << (byte * 8) }
            return value
        }
        try check(String(data: wave.prefix(4), encoding: .ascii) == "RIFF" && little(4,4) == UInt64(wave.count - 8), "Invalid RIFF length")
        try check(little(22,2) == UInt64(original.channels) && little(24,4) == UInt64(original.sampleRate), "WAVE channel/rate loss")
        let containerBits = Int(little(34,2)), bytes = containerBits / 8
        try check([8,16,24,32].contains(containerBits) && containerBits >= original.bitsPerSample, "WAVE loses precision")
        if little(20,2) == 0xfffe { try check(little(38,2) == UInt64(original.bitsPerSample), "WAVE valid depth mismatch") }
        let header = 20 + Int(little(16,4))
        try check(String(data: wave[header..<header + 4], encoding: .ascii) == "data", "WAVE data header")
        try check(little(header + 4,4) == UInt64(original.samples.count * bytes), "WAVE sample count mismatch")
        for index in original.samples.indices {
            let raw = little(header + 8 + index * bytes, bytes)
            let signed: Int64
            if containerBits == 8 { signed = Int64(raw) - 128 }
            else { signed = raw & (UInt64(1) << (containerBits - 1)) == 0 ? Int64(raw) : Int64(raw) - (Int64(1) << containerBits) }
            try check((signed >> (containerBits - original.bitsPerSample)) == Int64(original.samples[index]), "WAVE sample precision mismatch at \(index)")
        }
    }

    static func audioStart(_ data: Data) throws -> Int {
        var offset = 4
        while true {
            guard data.count - offset >= 4 else { throw Failure.check("Fixture metadata truncated") }
            let last = data[offset] & 0x80 != 0
            let size = Int(data[offset + 1]) << 16 | Int(data[offset + 2]) << 8 | Int(data[offset + 3])
            offset += 4 + size
            if last { return offset }
        }
    }

    static func rejects(_ data: Data, _ name: String, containing expected: String? = nil) throws {
        do {
            _ = try OriginalFLACDecoder.decode(data)
        } catch let error as CodecFailure {
            if let expected { try check(error.localizedDescription.contains(expected), "\(name) failed at unexpected boundary: \(error.localizedDescription)") }
            return
        }
        throw Failure.check("Invalid FLAC accepted: \(name)")
    }

    static func check(_ condition: @autoclosure () throws -> Bool, _ detail: String) throws {
        if try !condition() { throw Failure.check(detail) }
    }
}
