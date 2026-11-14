import Foundation
import CoreGraphics

/// Original experimental implementations. No VLC/FFmpeg source or libraries.
/// Container layouts follow RIFF/WAVE/AVI specifications; IMA follows the
/// standard predictor/index algorithm. This is deliberately not a universal decoder.
enum CodecFailure: Error, LocalizedError {
    case malformed(String), unsupported(String), capacity
    var errorDescription: String? {
        switch self {
        case .malformed(let detail): return "Malformed media: \(detail)"
        case .unsupported(let detail): return detail
        case .capacity: return "This file exceeds the experimental decoder’s memory limit."
        }
    }
}

struct MediaBytes {
    let data: Data
    func require(_ offset: Int, _ count: Int) throws {
        guard offset >= 0, count >= 0, offset <= data.count, count <= data.count - offset else { throw CodecFailure.malformed("truncated chunk") }
    }
    func u16(_ offset: Int) throws -> Int {
        try require(offset, 2)
        return Int(data[offset]) | Int(data[offset + 1]) << 8
    }
    func u32(_ offset: Int) throws -> Int {
        try require(offset, 4)
        return Int(data[offset]) | Int(data[offset + 1]) << 8 | Int(data[offset + 2]) << 16 | Int(data[offset + 3]) << 24
    }
    func i32(_ offset: Int) throws -> Int { Int(Int32(bitPattern: UInt32(try u32(offset)))) }
    func fourCC(_ offset: Int) throws -> String {
        try require(offset, 4)
        return String(bytes: data[offset..<offset + 4], encoding: .ascii) ?? "????"
    }
    func chunks(start: Int, end: Int) throws -> [(String, Range<Int>)] {
        try Task.checkCancellation()
        try require(start, end - start)
        var offset = start
        var result: [(String, Range<Int>)] = []
        while offset < end {
            if result.count & 0x3ff == 0 { try Task.checkCancellation() }
            guard end - offset >= 8 else { throw CodecFailure.malformed("partial RIFF chunk header") }
            let size = try u32(offset + 4)
            guard size <= end - offset - 8 else { throw CodecFailure.malformed("RIFF chunk exceeds its parent") }
            let range = offset + 8..<offset + 8 + size
            result.append((try fourCC(offset), range))
            guard result.count <= 1_000_000 else { throw CodecFailure.capacity }
            offset = range.upperBound + (size & 1)
            guard offset <= end else { throw CodecFailure.malformed("missing RIFF padding") }
        }
        return result
    }
}

struct DecodedWave {
    let channels: Int
    let sampleRate: Int
    let samples: [Int16]
    let codecName: String
    var duration: Double { Double(samples.count) / Double(channels * sampleRate) }
    func pcmWave() -> Data {
        var out = Data()
        func text(_ value: String) { out.append(contentsOf: value.utf8) }
        func little(_ value: Int, _ width: Int) { for index in 0..<width { out.append(UInt8(truncatingIfNeeded: value >> (index * 8))) } }
        text("RIFF"); little(36 + samples.count * 2, 4); text("WAVEfmt "); little(16, 4)
        little(1, 2); little(channels, 2); little(sampleRate, 4); little(sampleRate * channels * 2, 4)
        little(channels * 2, 2); little(16, 2); text("data"); little(samples.count * 2, 4)
        for sample in samples { little(Int(sample), 2) }
        return out
    }
}

struct OriginalWaveDecoder {
    // IMA's standardized quantizer step and index tables, not third-party code.
    static let steps = [7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767]
    static let indexChanges = [-1,-1,-1,-1,2,4,6,8]
    static let sampleLimit = 64_000_000

    static func decode(_ data: Data) throws -> DecodedWave {
        try Task.checkCancellation()
        guard data.count <= 268_435_456 else { throw CodecFailure.capacity }
        let bytes = MediaBytes(data: data)
        guard try bytes.fourCC(0) == "RIFF", try bytes.fourCC(8) == "WAVE" else { throw CodecFailure.malformed("not a RIFF WAVE file") }
        let end = try bytes.u32(4) + 8
        let chunks = try bytes.chunks(start: 12, end: end)
        guard let format = chunks.first(where: { $0.0 == "fmt " })?.1,
              let audio = chunks.first(where: { $0.0 == "data" })?.1, format.count >= 16 else { throw CodecFailure.malformed("missing WAVE format or samples") }
        let tag = try bytes.u16(format.lowerBound)
        let channels = try bytes.u16(format.lowerBound + 2)
        let sampleRate = try bytes.u32(format.lowerBound + 4)
        let alignment = try bytes.u16(format.lowerBound + 12)
        let bits = try bytes.u16(format.lowerBound + 14)
        guard (1...8).contains(channels), (1000...384000).contains(sampleRate), alignment > 0 else { throw CodecFailure.malformed("invalid channel/rate/alignment") }
        var samples: [Int16] = []
        let name: String
        switch tag {
        case 1:
            guard [8,16,24,32].contains(bits), alignment == channels * bits / 8,
                  audio.count % alignment == 0 else { throw CodecFailure.unsupported("PCM WAVE requires aligned 8/16/24/32-bit integer samples.") }
            let width = bits / 8
            guard audio.count / width <= sampleLimit else { throw CodecFailure.capacity }
            samples.reserveCapacity(audio.count / width)
            for offset in stride(from: audio.lowerBound, to: audio.upperBound, by: width) {
                if samples.count & 0xffff == 0 { try Task.checkCancellation() }
                var value: Int32
                if bits == 8 { value = (Int32(data[offset]) - 128) << 8 }
                else if bits == 16 { value = Int32(Int16(bitPattern: UInt16(try bytes.u16(offset)))) }
                else if bits == 24 {
                    let raw = Int32(data[offset]) | Int32(data[offset + 1]) << 8 | Int32(data[offset + 2]) << 16
                    value = (raw << 8) >> 16
                } else { value = Int32(bitPattern: UInt32(try bytes.u32(offset))) >> 16 }
                samples.append(Int16(clamping: value))
            }
            name = "Luma PCM \(bits)-bit → 16-bit"
        case 6, 7:
            guard bits == 8, alignment == channels, audio.count % channels == 0 else { throw CodecFailure.malformed("invalid G.711 alignment") }
            guard audio.count <= sampleLimit else { throw CodecFailure.capacity }
            samples.reserveCapacity(audio.count)
            for offset in audio {
                if samples.count & 0xffff == 0 { try Task.checkCancellation() }
                samples.append(tag == 6 ? decodeALaw(data[offset]) : decodeMuLaw(data[offset]))
            }
            name = tag == 6 ? "Luma G.711 A-law" : "Luma G.711 μ-law"
        case 2:
            guard channels <= 2, bits == 4, format.count >= 22 else { throw CodecFailure.malformed("Microsoft ADPCM format") }
            let extra = try bytes.u16(format.lowerBound + 16)
            let declared = try bytes.u16(format.lowerBound + 18)
            let count = try bytes.u16(format.lowerBound + 20)
            guard (1...256).contains(count), extra >= 4 + count * 4,
                  format.count >= 18 + extra else { throw CodecFailure.malformed("Microsoft ADPCM coefficient table or extension") }
            var coefficients: [(Int, Int)] = []
            for index in 0..<count {
                let offset = format.lowerBound + 22 + index * 4
                coefficients.append((Int(Int16(bitPattern: UInt16(try bytes.u16(offset)))), Int(Int16(bitPattern: UInt16(try bytes.u16(offset + 2))))))
            }
            var sampleCount: Int?
            if let fact = chunks.first(where: { $0.0 == "fact" })?.1 {
                guard fact.count >= 4 else { throw CodecFailure.malformed("Microsoft ADPCM fact count") }
                sampleCount = try bytes.u32(fact.lowerBound)
            }
            samples = try OriginalMSADPCM.decode(blocks: Data(data[audio]), channels: channels, blockAlign: alignment,
                samplesPerBlock: declared, coefficients: coefficients, sampleCount: sampleCount)
            name = "Luma Microsoft ADPCM"
        case 0x11:
            guard channels <= 2, bits == 4, format.count >= 20, alignment >= channels * 4,
                  audio.count % alignment == 0 else { throw CodecFailure.malformed("invalid IMA ADPCM blocks") }
            let extra = try bytes.u16(format.lowerBound + 16)
            guard extra >= 2, format.count >= 18 + extra else { throw CodecFailure.malformed("IMA format extension") }
            let declared = try bytes.u16(format.lowerBound + 18)
            let expected = ((alignment - channels * 4) * 2 / channels) + 1
            guard declared == expected, (alignment - channels * 4) % (channels * 4) == 0 else { throw CodecFailure.malformed("IMA block sample count mismatch") }
            guard audio.count / alignment * expected * channels <= sampleLimit else { throw CodecFailure.capacity }
            for start in stride(from: audio.lowerBound, to: audio.upperBound, by: alignment) {
                try Task.checkCancellation()
                var predictors: [Int] = [], indices: [Int] = [], decoded = [[Int16]](repeating: [], count: channels)
                for channel in 0..<channels {
                    let header = start + channel * 4
                    let predictor = Int(Int16(bitPattern: UInt16(try bytes.u16(header))))
                    let index = Int(data[header + 2])
                    guard index <= 88, data[header + 3] == 0 else { throw CodecFailure.malformed("IMA predictor header") }
                    predictors.append(predictor); indices.append(index); decoded[channel].append(Int16(predictor))
                }
                var cursor = start + channels * 4
                while cursor < start + alignment {
                    if (cursor - start) & 0x7fff == 0 { try Task.checkCancellation() }
                    for channel in 0..<channels {
                        for offset in cursor..<cursor + 4 {
                            let packed = Int(data[offset])
                            for nibble in [packed & 15, packed >> 4] {
                                let step = steps[indices[channel]]
                                var delta = step >> 3
                                if nibble & 1 != 0 { delta += step >> 2 }
                                if nibble & 2 != 0 { delta += step >> 1 }
                                if nibble & 4 != 0 { delta += step }
                                predictors[channel] = min(32767, max(-32768, predictors[channel] + (nibble & 8 == 0 ? delta : -delta)))
                                indices[channel] = min(88, max(0, indices[channel] + indexChanges[nibble & 7]))
                                decoded[channel].append(Int16(predictors[channel]))
                            }
                        }
                        cursor += 4
                    }
                }
                for frame in 0..<expected {
                    if samples.count & 0xffff == 0 { try Task.checkCancellation() }
                    for channel in 0..<channels { samples.append(decoded[channel][frame]) }
                }
            }
            // The fact chunk trims encoder padding in the last complete block.
            if let fact = chunks.first(where: { $0.0 == "fact" })?.1, fact.count >= 4 {
                let frames = try bytes.u32(fact.lowerBound)
                guard frames <= samples.count / channels else { throw CodecFailure.malformed("fact exceeds decoded samples") }
                samples = Array(samples.prefix(frames * channels))
            }
            name = "Luma IMA ADPCM"
        default:
            throw CodecFailure.unsupported("WAVE codec \(tag) is not implemented by the experimental Luma decoder.")
        }
        try Task.checkCancellation()
        guard !samples.isEmpty else { throw CodecFailure.malformed("empty audio") }
        return DecodedWave(channels: channels, sampleRate: sampleRate, samples: samples, codecName: name)
    }

    static func decodeMuLaw(_ byte: UInt8) -> Int16 {
        let code = Int(byte ^ 0xff)
        let magnitude = (((code & 15) << 3) + 132) << ((code >> 4) & 7)
        return Int16(clamping: code & 128 == 0 ? magnitude - 132 : 132 - magnitude)
    }
    static func decodeALaw(_ byte: UInt8) -> Int16 {
        let code = Int(byte ^ 0x55), exponent = (code >> 4) & 7
        var magnitude = (code & 15) << 4
        magnitude += exponent == 0 ? 8 : 264
        if exponent > 1 { magnitude <<= exponent - 1 }
        return Int16(clamping: code & 128 == 0 ? -magnitude : magnitude)
    }
}

/// Only one decoded Video 1 checkpoint is retained. The lock protects its
/// immutable value and learned keyframe positions, not the decoding work.
private final class MSVideo1AVICache {
    private let lock = NSLock()
    private var frame: MSVideo1Frame?
    private var index = -1
    private var keyframes: Set<Int> = []

    func snapshot(for target: Int) -> (frame: MSVideo1Frame?, index: Int, keyframe: Int?) {
        lock.lock()
        defer { lock.unlock() }
        return (frame, index, keyframes.lazy.filter { $0 <= target }.max())
    }

    func store(_ value: MSVideo1Frame, at position: Int, keyframes discovered: Set<Int>) {
        lock.lock()
        defer { lock.unlock() }
        frame = value
        index = position
        keyframes.formUnion(discovered)
    }
}

struct OriginalAVI {
    let bytes: MediaBytes
    let frames: [Range<Int>]
    let width: Int
    let height: Int
    let bottomUp: Bool
    let bitDepth: Int
    let compression: Int
    let palette: [(UInt8, UInt8, UInt8)]
    let frameDuration: Double
    private let msVideo1Cache = MSVideo1AVICache()
    var duration: Double { Double(frames.count) * frameDuration }
    private static let msVideo1Compressions: Set<Int> = [0x4d415243, 0x6d617263, 0x4356534d, 0x6376736d, 0x4d414857, 0x6d616877]
    private var isMSVideo1: Bool { Self.msVideo1Compressions.contains(compression) }
    var codecName: String {
        if isMSVideo1 { return "Luma Microsoft Video 1 · \(bitDepth)-bit · video only" }
        if compression == 0x47504a4d || compression == 0x4745504a { return "Luma baseline MJPEG · video only" }
        if compression == 1 { return "Luma RLE8 AVI · video only" }
        return "Luma RGB AVI · video only"
    }

    static func open(_ data: Data) throws -> OriginalAVI {
        try Task.checkCancellation()
        guard data.count <= 536_870_912 else { throw CodecFailure.capacity }
        let bytes = MediaBytes(data: data)
        guard try bytes.fourCC(0) == "RIFF", try bytes.fourCC(8) == "AVI " else { throw CodecFailure.malformed("not AVI") }
        let top = try bytes.chunks(start: 12, end: bytes.u32(4) + 8)
        guard let header = top.first(where: { $0.0 == "LIST" && (try? bytes.fourCC($0.1.lowerBound)) == "hdrl" })?.1,
              let movie = top.first(where: { $0.0 == "LIST" && (try? bytes.fourCC($0.1.lowerBound)) == "movi" })?.1 else { throw CodecFailure.malformed("AVI lists missing") }
        let headers = try bytes.chunks(start: header.lowerBound + 4, end: header.upperBound)
        let streams = headers.filter { $0.0 == "LIST" && (try? bytes.fourCC($0.1.lowerBound)) == "strl" }
        var selected: (Int, Range<Int>, Range<Int>)?
        for (index, stream) in streams.enumerated() {
            if index & 0x3ff == 0 { try Task.checkCancellation() }
            let chunks = try bytes.chunks(start: stream.1.lowerBound + 4, end: stream.1.upperBound)
            if let strh = chunks.first(where: { $0.0 == "strh" })?.1,
               let strf = chunks.first(where: { $0.0 == "strf" })?.1,
               strh.count >= 48, (try bytes.fourCC(strh.lowerBound)) == "vids" { selected = (index, strh, strf); break }
        }
        guard let (index, stream, format) = selected, format.count >= 40 else { throw CodecFailure.unsupported("AVI has no supported video stream.") }
        let width = try bytes.i32(format.lowerBound + 4), signedHeight = try bytes.i32(format.lowerBound + 8)
        let bits = try bytes.u16(format.lowerBound + 14), compression = try bytes.u32(format.lowerBound + 16)
        let scale = try bytes.u32(stream.lowerBound + 20), rate = try bytes.u32(stream.lowerBound + 24)
        let msVideo1 = Self.msVideo1Compressions.contains(compression)
        guard try bytes.u16(format.lowerBound + 12) == 1,
              (compression == 0 && [24,32].contains(bits)) || (compression == 1 && bits == 8) || compression == 0x47504a4d || compression == 0x4745504a || (msVideo1 && [8,16].contains(bits)) else {
            throw CodecFailure.unsupported("The original AVI decoder supports RGB24/RGB32, RLE8 keyframes, baseline MJPEG and 8/16-bit Microsoft Video 1, not this AVI codec.")
        }
        guard width > 0, width <= 8192, signedHeight != 0, abs(signedHeight) <= 8192, scale > 0, rate > 0 else { throw CodecFailure.malformed("AVI dimensions or frame rate") }
        let height = abs(signedHeight)
        guard width * height <= 16_777_216 else { throw CodecFailure.capacity }
        let bitmapHeaderSize = msVideo1 ? try bytes.u32(format.lowerBound) : 40
        if msVideo1 {
            guard bitmapHeaderSize >= 40, bitmapHeaderSize <= format.count else { throw CodecFailure.malformed("Video 1 bitmap header") }
            guard signedHeight > 0, width % 4 == 0, height % 4 == 0 else {
                throw CodecFailure.unsupported("Microsoft Video 1 requires bottom-up dimensions that are multiples of four.")
            }
        }
        let stride = ((width * bits + 31) / 32) * 4
        var palette: [(UInt8, UInt8, UInt8)] = []
        if compression == 1 || (msVideo1 && bits == 8) {
            let count = try bytes.u32(format.lowerBound + 32)
            let colors = count == 0 ? 256 : count
            guard signedHeight > 0, colors <= 256, format.count >= bitmapHeaderSize + colors * 4 else { throw CodecFailure.malformed("AVI indexed palette or orientation") }
            for index in 0..<colors {
                let offset = format.lowerBound + bitmapHeaderSize + index * 4
                palette.append((data[offset + 2], data[offset + 1], data[offset]))
            }
        }
        let prefix = String(format: "%02d", index)
        var frames: [Range<Int>] = []
        func collect(_ range: Range<Int>, depth: Int) throws {
            try Task.checkCancellation()
            guard depth <= 16 else { throw CodecFailure.malformed("nested AVI records") }
            for (position, item) in try bytes.chunks(start: range.lowerBound, end: range.upperBound).enumerated() {
                if position & 0x3ff == 0 { try Task.checkCancellation() }
                let (id, chunk) = item
                if id == prefix + "db" || id == prefix + "dc" {
                    guard compression != 0 ? chunk.count >= 2 : chunk.count >= stride * height else { throw CodecFailure.malformed("truncated AVI frame") }
                    if msVideo1 && chunk.count > 33_554_432 { throw CodecFailure.capacity }
                    frames.append(chunk)
                } else if msVideo1 && id == prefix + "pc" {
                    throw CodecFailure.unsupported("AVI palette-change chunks are not implemented for Microsoft Video 1.")
                } else if id == "LIST", chunk.count >= 4, try bytes.fourCC(chunk.lowerBound) == "rec " {
                    try collect(chunk.lowerBound + 4..<chunk.upperBound, depth: depth + 1)
                }
            }
        }
        try collect(movie.lowerBound + 4..<movie.upperBound, depth: 0)
        try Task.checkCancellation()
        guard !frames.isEmpty else { throw CodecFailure.malformed("no AVI frames") }
        return OriginalAVI(bytes: bytes, frames: frames, width: width, height: height, bottomUp: signedHeight > 0, bitDepth: bits,
                           compression: compression, palette: palette, frameDuration: Double(scale) / Double(rate))
    }

    func image(at time: Double) throws -> CGImage {
        try Task.checkCancellation()
        guard time.isFinite else { throw CodecFailure.malformed("invalid frame timestamp") }
        let frameIndex = min(frames.count - 1, max(0, Int(min(Double(frames.count - 1), max(0, time / frameDuration)))))
        if isMSVideo1 { return try msVideo1Image(at: frameIndex) }
        let frame = frames[frameIndex]
        if compression == 0x47504a4d || compression == 0x4745504a {
            let image = try OriginalJPEGDecoder.decode(Data(bytes.data[frame]))
            guard image.width == width, image.height == height else { throw CodecFailure.unsupported("MJPEG field/dimension changes are not implemented.") }
            return image
        }
        let stride = ((width * bitDepth + 31) / 32) * 4, pixelWidth = bitDepth / 8
        var rgba = Data(count: width * height * 4)
        let indices: [UInt8]? = compression == 1 ? try decodeRLE8(frame) : nil
        try rgba.withUnsafeMutableBytes { output in
            let target = output.bindMemory(to: UInt8.self)
            for y in 0..<height {
                if y & 7 == 0 { try Task.checkCancellation() }
                let sourceY = bottomUp ? height - 1 - y : y
                for x in 0..<width {
                    let source = frame.lowerBound + sourceY * stride + x * pixelWidth
                    let destination = (y * width + x) * 4
                    if let indices {
                        let color = palette[Int(indices[sourceY * width + x])]
                        target[destination] = color.0
                        target[destination + 1] = color.1
                        target[destination + 2] = color.2
                    } else {
                        target[destination] = bytes.data[source + 2]
                        target[destination + 1] = bytes.data[source + 1]
                        target[destination + 2] = bytes.data[source]
                    }
                    target[destination + 3] = 255
                }
            }
        }
        try Task.checkCancellation()
        let provider = CGDataProvider(data: rgba as CFData)!
        guard let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue),
                                  provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent) else { throw CodecFailure.malformed("frame output") }
        return image
    }

    private func msVideo1Image(at target: Int) throws -> CGImage {
        try Task.checkCancellation()
        let checkpoint = msVideo1Cache.snapshot(for: target)
        if checkpoint.index == target, let frame = checkpoint.frame { return try frame.image() }
        var start = checkpoint.keyframe ?? 0
        var previous: MSVideo1Frame?
        // A cached predecessor is useful only when it shortens the replay from
        // the closest already verified keyframe. Future checkpoints are never
        // used to decode an earlier frame.
        if checkpoint.index < target, checkpoint.index >= start, let cached = checkpoint.frame {
            start = checkpoint.index + 1
            previous = cached
        }
        var discovered: Set<Int> = []
        for index in start...target {
            try Task.checkCancellation()
            let decoded = try OriginalMSVideo1Decoder.decode(width: width, height: height, bitDepth: bitDepth,
                                                             data: Data(bytes.data[frames[index]]),
                                                             palette: palette, previousFrame: previous)
            if decoded.isKeyframe { discovered.insert(index) }
            previous = decoded
        }
        try Task.checkCancellation()
        guard let decoded = previous else { throw CodecFailure.malformed("Video 1 replay produced no frame") }
        msVideo1Cache.store(decoded, at: target, keyframes: discovered)
        return try decoded.image()
    }

    private func decodeRLE8(_ range: Range<Int>) throws -> [UInt8] {
        try Task.checkCancellation()
        var indices = [UInt8](repeating: 0, count: width * height)
        var cursor = range.lowerBound, x = 0, y = 0, written = 0, operations = 0
        func validate(_ value: UInt8) throws {
            guard Int(value) < palette.count else { throw CodecFailure.malformed("RLE8 palette index") }
        }
        while cursor < range.upperBound {
            if operations & 255 == 0 { try Task.checkCancellation() }
            operations += 1
            guard range.upperBound - cursor >= 2 else { throw CodecFailure.malformed("RLE8 opcode") }
            let count = Int(bytes.data[cursor]), value = bytes.data[cursor + 1]
            cursor += 2
            if count != 0 {
                try validate(value)
                guard y < height, count <= width - x else { throw CodecFailure.malformed("RLE8 run exceeds scanline") }
                for offset in 0..<count { indices[y * width + x + offset] = value }
                x += count; written += count
            } else if value == 0 {
                guard x == width else { throw CodecFailure.unsupported("RLE8 delta/partial frames are not implemented yet.") }
                x = 0; y += 1
            } else if value == 1 {
                guard written == width * height else { throw CodecFailure.unsupported("RLE8 requires complete keyframes in this version.") }
                return indices
            } else if value == 2 {
                throw CodecFailure.unsupported("RLE8 delta frames are not implemented yet.")
            } else {
                let size = Int(value)
                guard y < height, size <= width - x, size + (size & 1) <= range.upperBound - cursor else { throw CodecFailure.malformed("RLE8 literal exceeds frame") }
                for offset in 0..<size {
                    let literal = bytes.data[cursor + offset]
                    try validate(literal)
                    indices[y * width + x + offset] = literal
                }
                x += size; written += size; cursor += size + (size & 1)
            }
        }
        throw CodecFailure.malformed("RLE8 end marker missing")
    }
}
