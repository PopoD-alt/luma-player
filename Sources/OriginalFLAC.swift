import Foundation
import CryptoKit

/// Signed, unscaled sample values at the source bit depth. Derived independently
/// from RFC 9639; no VLC, FFmpeg, libFLAC, or platform FLAC decoder is used.
struct DecodedFLAC {
    let channels: Int
    let sampleRate: Int
    let bitsPerSample: Int
    let samples: [Int32]
    let codecName = "Luma original FLAC"
    var duration: Double { Double(samples.count / channels) / Double(sampleRate) }

    /// Pad at the least-significant end to a whole-byte PCM container. This is
    /// lossless even for 12/20-bit sources; the original samples remain above.
    func pcmWave() -> Data {
        let containerBits = ((bitsPerSample + 7) / 8) * 8
        let bytesPerSample = containerBits / 8
        let payloadSize = samples.count * bytesPerSample
        let extended = channels > 2 || containerBits != bitsPerSample
        let fmtSize = extended ? 40 : 16
        var out = Data()
        out.reserveCapacity(28 + fmtSize + payloadSize + (payloadSize & 1))
        func text(_ string: String) { out.append(contentsOf: string.utf8) }
        func little(_ value: Int64, _ count: Int) {
            for index in 0..<count { out.append(UInt8(truncatingIfNeeded: value >> (index * 8))) }
        }
        text("RIFF"); little(Int64(20 + fmtSize + payloadSize + (payloadSize & 1)), 4); text("WAVE")
        text("fmt "); little(Int64(fmtSize), 4)
        little(extended ? 0xfffe : 1, 2); little(Int64(channels), 2); little(Int64(sampleRate), 4)
        little(Int64(sampleRate * channels * bytesPerSample), 4)
        little(Int64(channels * bytesPerSample), 2); little(Int64(containerBits), 2)
        if extended {
            let masks = [0x4, 0x3, 0x7, 0x33, 0x37, 0x3f, 0x70f, 0x63f]
            little(22, 2); little(Int64(bitsPerSample), 2); little(Int64(masks[channels - 1]), 4)
            out.append(contentsOf: [1,0,0,0,0,0,0x10,0,0x80,0,0,0xaa,0,0x38,0x9b,0x71])
        }
        text("data"); little(Int64(payloadSize), 4)
        let padding = containerBits - bitsPerSample
        for sample in samples {
            let aligned = Int64(sample) << padding
            little(containerBits == 8 ? aligned + 128 : aligned, bytesPerSample)
        }
        if payloadSize & 1 != 0 { out.append(0) }
        return out
    }
}

enum OriginalFLACDecoder {
    /// Full-file prototype: at most 256 MiB input and 64 million interleaved
    /// samples (256 MB of Int32 PCM); frame scratch storage is separately bounded.
    static let inputLimit = 268_435_456
    static let sampleLimit = 64_000_000

    static func decode(_ source: Data) throws -> DecodedFLAC {
        try Task.checkCancellation()
        guard source.count <= inputLimit else { throw CodecFailure.capacity }
        // Rebase a Data slice without allocating a second whole-file byte array.
        let data = source.startIndex == 0 ? source : Data(source)
        guard data.count >= 4, data.prefix(4) == Data("fLaC".utf8) else {
            throw CodecFailure.unsupported("The original FLAC decoder requires a native .flac stream; Ogg/MP4 FLAC and ID3 wrappers are not implemented.")
        }
        var bits = FLACBits(data: data, position: 32)
        var info: StreamInfo?
        var metadataCount = 0
        while true {
            if metadataCount & 0x3ff == 0 { try Task.checkCancellation() }
            let last = try bits.read(1) != 0
            let type = Int(try bits.read(7))
            let length = Int(try bits.read(24))
            guard type != 127 else { throw CodecFailure.malformed("FLAC forbidden metadata type") }
            guard metadataCount != 0 || type == 0 else { throw CodecFailure.malformed("FLAC STREAMINFO must be first") }
            guard length <= bits.remaining / 8 else { throw CodecFailure.malformed("truncated FLAC metadata") }
            metadataCount += 1
            guard metadataCount <= 100_000 else { throw CodecFailure.capacity }
            if type == 0 {
                guard info == nil, length == 34 else { throw CodecFailure.malformed("duplicate or incorrectly sized FLAC STREAMINFO") }
                let minBlock = Int(try bits.read(16)), maxBlock = Int(try bits.read(16))
                let minFrame = Int(try bits.read(24)), maxFrame = Int(try bits.read(24))
                let rate = Int(try bits.read(20)), channels = Int(try bits.read(3)) + 1
                let depth = Int(try bits.read(5)) + 1, total = try bits.read(36)
                var md5: [UInt8] = []
                for _ in 0..<16 { md5.append(UInt8(try bits.read(8))) }
                guard minBlock >= 16, minBlock <= maxBlock, (4...32).contains(depth), rate > 0,
                      maxFrame == 0 || minFrame <= maxFrame else { throw CodecFailure.malformed("invalid FLAC STREAMINFO limits") }
                guard total <= UInt64(sampleLimit / channels) else { throw CodecFailure.capacity }
                info = StreamInfo(minBlock: minBlock, maxBlock: maxBlock, minFrame: minFrame,
                                  maxFrame: maxFrame, sampleRate: rate, channels: channels,
                                  depth: depth, total: total, md5: md5)
            } else {
                // Ancillary metadata does not affect sample decoding. No image,
                // URL or application block is executed or fetched.
                try bits.skip(length * 8)
            }
            if last { break }
        }
        guard let info else { throw CodecFailure.malformed("missing FLAC STREAMINFO") }
        var output: [Int32] = []
        if info.total > 0 { output.reserveCapacity(Int(info.total) * info.channels) }
        var frames: UInt64 = 0, sampleFrames: UInt64 = 0
        var strategy: Bool?
        var previousBlock: Int?
        var firstBlock: Int?
        var hash = Insecure.MD5()
        let verifyMD5 = info.md5.contains { $0 != 0 }
        while bits.remaining > 0 {
            try Task.checkCancellation()
            let frameStart = bits.position / 8
            if let previousBlock, previousBlock < info.minBlock {
                throw CodecFailure.malformed("nonfinal FLAC block smaller than STREAMINFO minimum")
            }
            if let previousBlock, strategy == false, previousBlock != firstBlock {
                throw CodecFailure.malformed("FLAC fixed block size changed before final frame")
            }
            let header = try frameHeader(&bits, info: info)
            if let strategy, strategy != header.variable { throw CodecFailure.malformed("FLAC blocking strategy changed") }
            strategy = header.variable
            guard header.number == (header.variable ? sampleFrames : frames) else {
                throw CodecFailure.malformed("FLAC frame/sample number is not sequential")
            }
            guard header.block <= info.maxBlock else { throw CodecFailure.malformed("FLAC block exceeds STREAMINFO maximum") }
            guard header.block <= (sampleLimit - output.count) / info.channels else { throw CodecFailure.capacity }
            if info.total > 0, UInt64(header.block) > info.total - min(info.total, sampleFrames) {
                throw CodecFailure.malformed("FLAC frames exceed declared sample count")
            }
            if firstBlock == nil { firstBlock = header.block }
            if !header.variable, header.block > firstBlock! { throw CodecFailure.malformed("FLAC fixed block grew") }
            var channels: [[Int64]] = []
            for channel in 0..<info.channels {
                let isSide = (header.assignment == 8 && channel == 1)
                    || (header.assignment == 9 && channel == 0)
                    || (header.assignment == 10 && channel == 1)
                channels.append(try subframe(&bits, count: header.block, depth: info.depth + (isSide ? 1 : 0)))
            }
            let padding = (8 - bits.position % 8) % 8
            guard try bits.read(padding) == 0 else { throw CodecFailure.malformed("FLAC nonzero frame padding") }
            let footerStart = bits.position / 8
            let expectedCRC = UInt16(try bits.read(16))
            guard try crc16(data, frameStart..<footerStart) == expectedCRC else { throw CodecFailure.malformed("FLAC frame CRC16 mismatch") }
            let frameSize = bits.position / 8 - frameStart
            guard (info.minFrame == 0 || frameSize >= info.minFrame),
                  (info.maxFrame == 0 || frameSize <= info.maxFrame) else { throw CodecFailure.malformed("FLAC frame size differs from STREAMINFO") }
            let lower = -(Int64(1) << (info.depth - 1)), upper = (Int64(1) << (info.depth - 1)) - 1
            var md5Bytes = Data()
            let sampleBytes = (info.depth + 7) / 8
            if verifyMD5 { md5Bytes.reserveCapacity(header.block * info.channels * sampleBytes) }
            for index in 0..<header.block {
                if index & 0x1fff == 0 { try Task.checkCancellation() }
                if header.assignment == 8 { channels[1][index] = channels[0][index] - channels[1][index] }
                else if header.assignment == 9 { channels[0][index] += channels[1][index] }
                else if header.assignment == 10 {
                    let side = channels[1][index]
                    let doubledMid = channels[0][index] * 2 + (side & 1)
                    channels[0][index] = (doubledMid + side) >> 1
                    channels[1][index] = (doubledMid - side) >> 1
                }
                for channel in 0..<info.channels {
                    let sample = channels[channel][index]
                    guard sample >= lower, sample <= upper else { throw CodecFailure.malformed("FLAC decoded sample exceeds declared bit depth") }
                    output.append(Int32(sample))
                    if verifyMD5 {
                        // MD5 uses sign extension, unlike WAVE's least-significant
                        // padding for non-byte-aligned sample depths.
                        for byte in 0..<sampleBytes { md5Bytes.append(UInt8(truncatingIfNeeded: sample >> (byte * 8))) }
                    }
                }
            }
            if verifyMD5 { hash.update(data: md5Bytes) }
            previousBlock = header.block
            frames += 1
            sampleFrames += UInt64(header.block)
        }
        try Task.checkCancellation()
        guard frames > 0, !output.isEmpty else { throw CodecFailure.malformed("FLAC has no audio frames") }
        guard info.total == 0 || sampleFrames == info.total else { throw CodecFailure.malformed("FLAC sample count does not match STREAMINFO") }
        if verifyMD5, Array(hash.finalize()) != info.md5 { throw CodecFailure.malformed("FLAC decoded audio MD5 mismatch") }
        return DecodedFLAC(channels: info.channels, sampleRate: info.sampleRate, bitsPerSample: info.depth, samples: output)
    }

    private struct StreamInfo {
        let minBlock: Int, maxBlock: Int, minFrame: Int, maxFrame: Int
        let sampleRate: Int, channels: Int, depth: Int
        let total: UInt64
        let md5: [UInt8]
    }
    private struct FrameHeader {
        let variable: Bool, block: Int, assignment: Int
        let number: UInt64
    }

    private static func frameHeader(_ bits: inout FLACBits, info: StreamInfo) throws -> FrameHeader {
        guard bits.position % 8 == 0 else { throw CodecFailure.malformed("unaligned FLAC frame") }
        let start = bits.position / 8
        guard try bits.read(15) == 0x7ffc else { throw CodecFailure.malformed("FLAC frame sync missing") }
        let variable = try bits.read(1) != 0
        let blockCode = Int(try bits.read(4)), rateCode = Int(try bits.read(4))
        let assignment = Int(try bits.read(4)), depthCode = Int(try bits.read(3))
        guard try bits.read(1) == 0 else { throw CodecFailure.malformed("FLAC reserved frame bit is nonzero") }
        guard blockCode != 0, rateCode != 15, assignment <= 10, depthCode != 3 else {
            throw CodecFailure.unsupported("Reserved FLAC frame parameters are not supported.")
        }
        let number = try codedNumber(&bits, variable: variable)
        let block: Int
        switch blockCode {
        case 1: block = 192
        case 2...5: block = 144 << blockCode
        case 6: block = Int(try bits.read(8)) + 1
        case 7: block = Int(try bits.read(16)) + 1
        default: block = 1 << blockCode
        }
        guard block <= 65535 else { throw CodecFailure.malformed("FLAC forbidden block size 65536") }
        let rate: Int
        switch rateCode {
        case 0: rate = info.sampleRate
        case 1...11: rate = [0,88200,176400,192000,8000,16000,22050,24000,32000,44100,48000,96000][rateCode]
        case 12: rate = Int(try bits.read(8)) * 1000
        case 13: rate = Int(try bits.read(16))
        default: rate = Int(try bits.read(16)) * 10
        }
        let depth = [info.depth,8,12,0,16,20,24,32][depthCode]
        let channels = assignment < 8 ? assignment + 1 : 2
        guard rate == info.sampleRate, depth == info.depth, channels == info.channels else {
            throw CodecFailure.unsupported("FLAC streams that change sample rate, bit depth, or channel count are not supported.")
        }
        let end = bits.position / 8
        let expected = UInt8(try bits.read(8))
        guard crc8(bits.data, start..<end) == expected else { throw CodecFailure.malformed("FLAC header CRC8 mismatch") }
        return FrameHeader(variable: variable, block: block, assignment: assignment, number: number)
    }

    private static func codedNumber(_ bits: inout FLACBits, variable: Bool) throws -> UInt64 {
        let first = UInt8(try bits.read(8))
        if first & 0x80 == 0 { return UInt64(first) }
        var length = 0
        var mask: UInt8 = 0x80
        while mask != 0 && first & mask != 0 { length += 1; mask >>= 1 }
        guard (2...(variable ? 7 : 6)).contains(length) else { throw CodecFailure.malformed("invalid FLAC coded number prefix") }
        var value = UInt64(first & (0x7f >> length))
        for _ in 1..<length {
            let byte = try bits.read(8)
            guard byte & 0xc0 == 0x80 else { throw CodecFailure.malformed("invalid FLAC coded number continuation") }
            value = (value << 6) | (byte & 0x3f)
        }
        let minimum: [UInt64] = [0,0,0x80,0x800,0x10000,0x200000,0x4000000,0x80000000]
        guard value >= minimum[length], value < (UInt64(1) << (variable ? 36 : 31)) else {
            throw CodecFailure.malformed("overlong or out-of-range FLAC coded number")
        }
        return value
    }

    private static func subframe(_ bits: inout FLACBits, count: Int, depth: Int) throws -> [Int64] {
        try Task.checkCancellation()
        guard try bits.read(1) == 0 else { throw CodecFailure.malformed("FLAC reserved subframe bit is nonzero") }
        let type = Int(try bits.read(6)), hasWasted = try bits.read(1) != 0
        let wasted = hasWasted ? Int(try bits.unary(maximum: UInt64(depth - 2))) + 1 : 0
        let precision = depth - wasted
        guard precision > 0 else { throw CodecFailure.malformed("FLAC wasted bits exhaust sample depth") }
        var samples: [Int64]
        switch type {
        case 0:
            let value = try bits.signed(precision)
            samples = [Int64](repeating: value, count: count)
        case 1:
            samples = []
            samples.reserveCapacity(count)
            for index in 0..<count {
                if index & 0xffff == 0 { try Task.checkCancellation() }
                samples.append(try bits.signed(precision))
            }
        case 8...12, 32...63:
            let order = type < 32 ? type - 8 : type - 31
            guard order < count else { throw CodecFailure.malformed("FLAC predictor order is not smaller than block") }
            samples = [Int64](repeating: 0, count: count)
            for index in 0..<order { samples[index] = try bits.signed(precision) }
            var coefficients: [Int64]
            let shift: Int
            if type < 32 {
                coefficients = [[],[1],[2,-1],[3,-3,1],[4,-6,4,-1]][order]
                shift = 0
            } else {
                let code = Int(try bits.read(4))
                guard code != 15 else { throw CodecFailure.malformed("FLAC forbidden LPC coefficient precision") }
                shift = Int(try bits.signed(5))
                guard shift >= 0 else { throw CodecFailure.malformed("FLAC forbidden negative LPC shift") }
                coefficients = []
                for _ in 0..<order { coefficients.append(try bits.signed(code + 1)) }
            }
            let residuals = try residual(&bits, count: count, order: order)
            let lower = -(Int64(1) << (precision - 1)), upper = (Int64(1) << (precision - 1)) - 1
            for index in order..<count {
                if (index - order) & 0x3fff == 0 { try Task.checkCancellation() }
                var prediction: Int64 = 0
                for coefficient in 0..<order {
                    // Valid samples are at most 33-bit and coefficients at most
                    // 15-bit, so a 32-term accumulator fits safely in Int64.
                    prediction += coefficients[coefficient] * samples[index - coefficient - 1]
                }
                let sample = (prediction >> shift) + residuals[index - order]
                guard sample >= lower, sample <= upper else { throw CodecFailure.malformed("FLAC predictor reconstructs an out-of-range sample") }
                samples[index] = sample
            }
        default:
            throw CodecFailure.unsupported("Reserved FLAC subframe type \(type) is not implemented.")
        }
        if wasted > 0 {
            try Task.checkCancellation()
            for index in samples.indices { samples[index] <<= wasted }
        }
        return samples
    }

    private static func residual(_ bits: inout FLACBits, count: Int, order: Int) throws -> [Int64] {
        let method = Int(try bits.read(2))
        guard method <= 1 else { throw CodecFailure.unsupported("Reserved FLAC residual coding method is not implemented.") }
        let partitionOrder = Int(try bits.read(4)), partitions = 1 << partitionOrder
        guard count % partitions == 0, count / partitions > order else {
            throw CodecFailure.malformed("invalid FLAC residual partition geometry")
        }
        let width = method == 0 ? 4 : 5, escape = (1 << width) - 1
        var samples: [Int64] = []
        samples.reserveCapacity(count - order)
        for partition in 0..<partitions {
            if partition & 0x3ff == 0 { try Task.checkCancellation() }
            let sampleCount = count / partitions - (partition == 0 ? order : 0)
            let parameter = Int(try bits.read(width))
            if parameter == escape {
                let rawBits = Int(try bits.read(5))
                for index in 0..<sampleCount {
                    if index & 0xffff == 0 { try Task.checkCancellation() }
                    samples.append(rawBits == 0 ? 0 : try bits.signed(rawBits))
                }
            } else {
                for index in 0..<sampleCount {
                    if index & 0xffff == 0 { try Task.checkCancellation() }
                    let quotient = try bits.unary(maximum: 0xffff_fffe >> parameter)
                    let folded = (quotient << parameter) | (try bits.read(parameter))
                    guard folded <= 0xffff_fffe else { throw CodecFailure.malformed("FLAC residual exceeds signed 32-bit magnitude") }
                    samples.append(folded & 1 == 0 ? Int64(folded >> 1) : -Int64(folded >> 1) - 1)
                }
            }
        }
        return samples
    }

    private static func crc8(_ data: Data, _ range: Range<Int>) -> UInt8 {
        var crc: UInt8 = 0
        for index in range {
            crc ^= data[index]
            for _ in 0..<8 { crc = crc & 0x80 != 0 ? (crc &<< 1) ^ 0x07 : crc &<< 1 }
        }
        return crc
    }
    private static func crc16(_ data: Data, _ range: Range<Int>) throws -> UInt16 {
        var crc: UInt16 = 0
        for index in range {
            if (index - range.lowerBound) & 0xffff == 0 { try Task.checkCancellation() }
            crc ^= UInt16(data[index]) << 8
            for _ in 0..<8 { crc = crc & 0x8000 != 0 ? (crc &<< 1) ^ 0x8005 : crc &<< 1 }
        }
        return crc
    }
}

/// MSB-first bounded reader. Unary runs are scanned a byte at a time and cannot
/// extend beyond the finite input or the residual's representable range.
private struct FLACBits {
    let data: Data
    var position: Int
    var remaining: Int { data.count * 8 - position }

    mutating func read(_ count: Int) throws -> UInt64 {
        guard count >= 0, count <= 36, count <= remaining else { throw CodecFailure.malformed("truncated FLAC bitstream") }
        var result: UInt64 = 0, todo = count
        while todo > 0 {
            let available = 8 - position % 8, take = min(available, todo)
            let value = (Int(data[position / 8]) >> (available - take)) & ((1 << take) - 1)
            result = (result << take) | UInt64(value)
            position += take
            todo -= take
        }
        return result
    }
    mutating func signed(_ count: Int) throws -> Int64 {
        guard (1...33).contains(count) else { throw CodecFailure.malformed("invalid FLAC signed value width") }
        let raw = try read(count)
        return raw & (UInt64(1) << (count - 1)) == 0 ? Int64(raw) : Int64(raw) - (Int64(1) << count)
    }
    mutating func skip(_ count: Int) throws {
        guard count >= 0, count <= remaining else { throw CodecFailure.malformed("truncated FLAC metadata") }
        position += count
    }
    mutating func unary(maximum: UInt64) throws -> UInt64 {
        var zeros: UInt64 = 0
        while remaining > 0 {
            if position & 0x7ffff == 0 { try Task.checkCancellation() }
            if position % 8 == 0, data[position / 8] == 0 {
                guard zeros <= maximum, maximum - zeros >= 8 else { throw CodecFailure.malformed("FLAC unary value exceeds limit") }
                zeros += 8
                position += 8
            } else if try read(1) == 1 { return zeros }
            else {
                guard zeros < maximum else { throw CodecFailure.malformed("FLAC unary value exceeds limit") }
                zeros += 1
            }
        }
        throw CodecFailure.malformed("unterminated FLAC unary value")
    }
}
