import Foundation

enum MSADPCMError: Error, LocalizedError {
    case malformed(String), unsupported(String), capacity(String)
    var errorDescription: String? {
        switch self {
        case .malformed(let detail): return "Malformed Microsoft ADPCM: \(detail)"
        case .unsupported(let detail): return "Unsupported Microsoft ADPCM: \(detail)"
        case .capacity(let detail): return "Microsoft ADPCM resource limit: \(detail)"
        }
    }
}

/// Original Microsoft ADPCM sample reconstruction. No native or third-party
/// decoder is called. Coefficients are declared signed 8.8 fixed-point values;
/// intermediate prediction uses arithmetic rounding, verified independently
/// against Apple's native Microsoft ADPCM decoder on negative-fraction vectors.
///
/// Primary format: Microsoft Multimedia Data Standards Update, rev3 (1994),
/// pp22–26, https://elm-chan.org/junk/adpcm/RIFF_NEW.pdf
/// Header documentation: https://github.com/microsoft/DirectXTK/wiki/Wave-Formats
/// Quantizer constants: AL_SOFT_MSADPCM public format specification,
/// https://openal.org/pipermail/openal/2014-July/000182.html
enum OriginalMSADPCM {
    static let standardCoefficients: [(Int, Int)] = [
        (256,0), (512,-256), (0,0), (192,64), (240,0), (460,-208), (392,-232)
    ]
    static let sampleLimit = 64_000_000
    private static let adaptation: [Int64] = [230,230,230,230,307,409,512,614,768,614,512,409,307,230,230,230]

    /// `sampleCount` is the WAVE fact count in sample frames per channel, not
    /// interleaved scalar samples. A short final block must retain a complete
    /// channel header; only available nibbles are decoded, never fabricated.
    static func decode(blocks: Data, channels: Int, blockAlign: Int, samplesPerBlock: Int,
                       coefficients: [(Int, Int)], sampleCount: Int? = nil) throws -> [Int16] {
        try Task.checkCancellation()
        guard channels == 1 || channels == 2 else { throw MSADPCMError.unsupported("only mono and stereo blocks are implemented") }
        guard blocks.count <= 268_435_456 else { throw MSADPCMError.capacity("compressed input exceeds 256 MiB") }
        let headerBytes = 7 * channels
        guard (headerBytes...65535).contains(blockAlign), (2...65535).contains(samplesPerBlock),
              samplesPerBlock == 2 + (blockAlign - headerBytes) * 2 / channels else {
            throw MSADPCMError.malformed("block alignment and SamplesPerBlock disagree")
        }
        guard (1...256).contains(coefficients.count),
              coefficients.allSatisfy({ (-32768...32767).contains($0.0) && (-32768...32767).contains($0.1) }) else {
            throw MSADPCMError.malformed("coefficient table must contain 1–256 signed 16-bit pairs")
        }
        guard !blocks.isEmpty else { throw MSADPCMError.malformed("empty block data") }
        let complete = blocks.count / blockAlign, remainder = blocks.count % blockAlign
        guard remainder == 0 || remainder >= headerBytes else { throw MSADPCMError.malformed("final block has a truncated channel header") }
        let partialFrames = remainder == 0 ? 0 : 2 + (remainder - headerBytes) * 2 / channels
        let totalFrames = complete * samplesPerBlock + partialFrames
        guard totalFrames <= sampleLimit / channels else { throw MSADPCMError.capacity("decoded output exceeds 64 million scalar samples") }
        let requestedFrames = sampleCount ?? totalFrames
        guard requestedFrames >= 0, requestedFrames <= totalFrames else { throw MSADPCMError.malformed("fact sample count exceeds available decoded frames or is negative") }
        let targetSamples = requestedFrames * channels
        var output = [Int16]()
        output.reserveCapacity(targetSamples)
        return try blocks.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) -> [Int16] in
            func signed16(_ offset: Int) -> Int64 {
                Int64(Int16(bitPattern: UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8))
            }
            var offset = 0, processedNibbles = 0
            while offset < bytes.count {
                try Task.checkCancellation()
                let length = min(blockAlign, bytes.count - offset)
                var states = [ChannelState]()
                for channel in 0..<channels {
                    let predictor = Int(bytes[offset + channel])
                    guard predictor < coefficients.count else { throw MSADPCMError.malformed("predictor index exceeds the declared coefficient table") }
                    let pair = coefficients[predictor]
                    states.append(ChannelState(coefficient1: Int64(pair.0), coefficient2: Int64(pair.1),
                        delta: signed16(offset + channels + channel * 2),
                        newest: signed16(offset + channels * 3 + channel * 2),
                        oldest: signed16(offset + channels * 5 + channel * 2)))
                }
                // The wire header stores sample1 before sample2, but the oldest
                // sample2 is the first audible frame, across every channel.
                for state in states where output.count < targetSamples { output.append(Int16(state.oldest)) }
                for state in states where output.count < targetSamples { output.append(Int16(state.newest)) }
                for cursor in offset + headerBytes..<offset + length {
                    let packed = bytes[cursor]
                    for (index, nibble) in [Int(packed >> 4), Int(packed & 15)].enumerated() {
                        if processedNibbles & 0xfff == 0 { try Task.checkCancellation() }
                        processedNibbles += 1
                        let channel = channels == 1 ? 0 : index
                        let sample = try states[channel].advance(nibble)
                        if output.count < targetSamples { output.append(sample) }
                    }
                }
                offset += length
            }
            try Task.checkCancellation()
            return output
        }
    }

    private struct ChannelState {
        let coefficient1: Int64, coefficient2: Int64
        var delta: Int64, newest: Int64, oldest: Int64
        mutating func advance(_ nibble: Int) throws -> Int16 {
            // A hostile sequence can grow the adaptive scale without bound.
            // Reject when exact fixed-width arithmetic would no longer fit;
            // silently clipping the scale would change later decoded samples.
            guard delta <= Int64.max / 768 else { throw MSADPCMError.capacity("adaptive scale exceeds exact 64-bit arithmetic") }
            let prediction = (newest * coefficient1 + oldest * coefficient2) >> 8
            let correction = Int64(nibble < 8 ? nibble : nibble - 16) * delta
            let result = Int16(clamping: prediction + correction)
            oldest = newest; newest = Int64(result)
            delta = max(16, (delta * adaptation[nibble]) >> 8)
            return result
        }
    }
}
