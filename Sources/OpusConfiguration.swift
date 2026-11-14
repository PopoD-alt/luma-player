import Foundation
import CoreMedia

enum OpusConfigurationError: Error, LocalizedError {
    case malformed(String), unsupported(String), metadataMismatch(String), systemFormat(OSStatus)
    var errorDescription: String? {
        switch self {
        case .malformed(let detail): return "Malformed Opus data: \(detail)"
        case .unsupported(let detail): return "Unsupported Opus configuration: \(detail)"
        case .metadataMismatch(let detail): return "Matroska Opus metadata disagrees: \(detail)"
        case .systemFormat(let status): return "The system rejected the standard Opus sample entry (\(status))"
        }
    }
}

/// Original identification-header and packet-framing interpretation. This does
/// not decode compressed audio. The ISO sample-entry bridge lets CoreMedia
/// derive its own decoder configuration from the public dOps representation.
/// References: RFC 7845 §5.1; RFC 6716 §3; Opus ISOBMFF §4.3.
/// https://www.rfc-editor.org/rfc/rfc7845
/// https://www.rfc-editor.org/rfc/rfc6716
/// https://opus-codec.org/docs/opus_in_isobmff.html
struct OpusConfiguration {
    let channels: Int
    let preSkip: Int
    /// Original capture rate, metadata only. Opus packet timing is always 48kHz.
    let inputSampleRate: UInt32
    let outputGain: Int16
    let identificationHeader: Data
    var codecDelaySeconds: Double { Double(preSkip) / 48_000 }
    static let decodedSampleRate = 48_000
    static let recommendedSeekPreRollNanoseconds: UInt64 = 80_000_000

    static func parse(_ data: Data) throws -> OpusConfiguration {
        let b = Array(data)
        guard b.count >= 19, data.prefix(8) == Data("OpusHead".utf8) else {
            throw OpusConfigurationError.malformed("missing or truncated OpusHead")
        }
        guard b[8] == 1 else { throw OpusConfigurationError.unsupported("OpusHead version \(b[8])") }
        guard b[18] == 0 else { throw OpusConfigurationError.unsupported("channel mapping family \(b[18]); only family 0 is implemented") }
        guard (1...2).contains(b[9]), b.count == 19 else {
            throw OpusConfigurationError.malformed("family 0 requires a 19-byte mono/stereo OpusHead")
        }
        let preSkip = Int(b[10]) | Int(b[11]) << 8
        let rate = UInt32(b[12]) | UInt32(b[13]) << 8 | UInt32(b[14]) << 16 | UInt32(b[15]) << 24
        let gain = Int16(bitPattern: UInt16(b[16]) | UInt16(b[17]) << 8)
        return OpusConfiguration(channels: Int(b[9]), preSkip: preSkip,
            inputSampleRate: rate, outputGain: gain, identificationHeader: data)
    }

    func validateMatroska(sampleRate: Double, channels: Int, codecDelayNanoseconds: UInt64?) throws {
        let declaredRate = inputSampleRate == 0 ? 48_000 : Double(inputSampleRate)
        guard sampleRate == declaredRate, channels == self.channels else {
            throw OpusConfigurationError.metadataMismatch("SamplingFrequency must match OpusHead's original input rate, and Channels its output channels")
        }
        guard let delay = codecDelayNanoseconds,
              abs(Double(delay) - Double(preSkip) * 1_000_000_000 / 48_000) <= 1 else {
            throw OpusConfigurationError.metadataMismatch("CodecDelay must express OpusHead pre-skip at 48 kHz")
        }
    }

    var dOpsPayload: Data {
        Data([0, UInt8(channels)]) + Self.bigEndian(UInt32(preSkip), 2)
            + Self.bigEndian(inputSampleRate, 4)
            + Self.bigEndian(UInt32(UInt16(bitPattern: outputGain)), 2) + Data([0])
    }

    func makeFormatDescription() throws -> CMAudioFormatDescription {
        // The probed macOS MOV decoder accepts this public configuration but
        // does not apply a nonzero OpusHead output gain. Do not silently change
        // loudness; that case requires native PCM conversion plus explicit gain.
        guard outputGain == 0 else {
            throw OpusConfigurationError.unsupported("nonzero Opus output gain requires PCM normalization")
        }
        let dOps = Self.bigEndian(UInt32(dOpsPayload.count + 8), 4) + Data("dOps".utf8) + dOpsPayload
        // ISO AudioSampleEntry: reserved, data-reference index, version/revision/
        // vendor reserved fields, channels, sample width, reserved, 16.16 rate.
        let body = Data(repeating: 0, count: 6) + Self.bigEndian(1, 2)
            + Data(repeating: 0, count: 8) + Self.bigEndian(UInt32(channels), 2)
            + Self.bigEndian(16, 2) + Self.bigEndian(0, 4)
            + Self.bigEndian(48_000 << 16, 4) + dOps
        let entry = Self.bigEndian(UInt32(body.count + 8), 4) + Data("Opus".utf8) + body
        var result: CMAudioFormatDescription?
        let status = entry.withUnsafeBytes { raw in
            CMAudioFormatDescriptionCreateFromBigEndianSoundDescriptionData(allocator: nil,
                bigEndianSoundDescriptionData: raw.baseAddress!.assumingMemoryBound(to: UInt8.self),
                size: entry.count, flavor: .isoFamily, formatDescriptionOut: &result)
        }
        guard status == noErr, let result else { throw OpusConfigurationError.systemFormat(status) }
        return result
    }

    /// Validates the public packet framing and returns decoded sample frames per
    /// channel. Payload range-code validity remains the native decoder's job.
    func packetSampleFrames(_ data: Data) throws -> Int {
        guard let toc = data.first else { throw OpusConfigurationError.malformed("empty Opus packet") }
        let bytes = Array(data)
        let configuration = Int(toc >> 3)
        let frameSamples: Int
        if configuration >= 16 { frameSamples = 120 << (configuration & 3) }
        else if configuration >= 12 { frameSamples = 480 << (configuration & 1) }
        else { frameSamples = [480, 960, 1920, 2880][configuration & 3] }
        var cursor = 1
        var frameCount: Int
        var lengths = [Int]()
        func readLength() throws -> Int {
            guard cursor < bytes.count else { throw OpusConfigurationError.malformed("truncated Opus frame length") }
            let first = Int(bytes[cursor]); cursor += 1
            if first < 252 { return first }
            guard cursor < bytes.count else { throw OpusConfigurationError.malformed("truncated Opus frame length") }
            let value = first + 4 * Int(bytes[cursor]); cursor += 1
            return value
        }
        switch toc & 3 {
        case 0:
            frameCount = 1; lengths = [bytes.count - cursor]
        case 1:
            frameCount = 2
            guard (bytes.count - cursor) % 2 == 0 else { throw OpusConfigurationError.malformed("unequal CBR Opus frame lengths") }
            lengths = Array(repeating: (bytes.count - cursor) / 2, count: 2)
        case 2:
            frameCount = 2
            let first = try readLength()
            guard first <= bytes.count - cursor else { throw OpusConfigurationError.malformed("Opus frame exceeds packet") }
            lengths = [first, bytes.count - cursor - first]
        default:
            guard cursor < bytes.count else { throw OpusConfigurationError.malformed("missing Opus frame-count byte") }
            let control = bytes[cursor]; cursor += 1
            frameCount = Int(control & 63)
            guard frameCount > 0, frameSamples * frameCount <= 5760 else {
                throw OpusConfigurationError.malformed("Opus packet duration exceeds 120 ms or has no frames")
            }
            var padding = 0
            if control & 64 != 0 {
                while true {
                    guard cursor < bytes.count else { throw OpusConfigurationError.malformed("truncated Opus padding length") }
                    let byte = Int(bytes[cursor]); cursor += 1
                    padding += byte == 255 ? 254 : byte
                    guard padding <= bytes.count - cursor else { throw OpusConfigurationError.malformed("Opus padding exceeds packet") }
                    if byte != 255 { break }
                }
            }
            let end = bytes.count - padding
            if control & 128 != 0 {
                for _ in 1..<frameCount { lengths.append(try readLength()) }
                let used = lengths.reduce(0, +)
                guard cursor <= end, used <= end - cursor else { throw OpusConfigurationError.malformed("Opus VBR lengths exceed packet") }
                lengths.append(end - cursor - used)
            } else {
                guard cursor <= end, (end - cursor) % frameCount == 0 else { throw OpusConfigurationError.malformed("invalid Opus CBR packet size") }
                lengths = Array(repeating: (end - cursor) / frameCount, count: frameCount)
            }
        }
        guard frameSamples * frameCount <= 5760, lengths.allSatisfy({ (0...1275).contains($0) }) else {
            throw OpusConfigurationError.malformed("Opus frame size or packet duration exceeds codec limits")
        }
        return frameSamples * frameCount
    }

    private static func bigEndian(_ value: UInt32, _ count: Int) -> Data {
        Data((0..<count).reversed().map { UInt8((value >> ($0 * 8)) & 255) })
    }
}
