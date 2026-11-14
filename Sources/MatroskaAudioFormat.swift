import Foundation
import AudioToolbox
import CoreMedia

enum MatroskaAudioFormatError: Error, LocalizedError {
    case malformed(String), unsupported(String), metadataMismatch(String), nativeFormat(String)
    var errorDescription: String? {
        switch self {
        case .malformed(let detail): return "Malformed Matroska audio: \(detail)"
        case .unsupported(let detail): return "Unsupported Matroska audio: \(detail)"
        case .metadataMismatch(let detail): return "Matroska audio metadata disagrees with its packets: \(detail)"
        case .nativeFormat(let detail): return "The system rejected the audio format: \(detail)"
        }
    }
}

struct MatroskaAudioPacketInfo {
    /// One compressed elementary frame's extent, or the complete PCM block.
    /// A caller can split concatenated compressed frames using this byte count.
    let encodedByteCount: Int
    let sampleFrames: Int
    let duration: Double
}

/// Original container/elementary-header interpretation; compressed audio remains
/// unchanged and is decoded by macOS. Header validation does not establish that
/// an entire compressed payload is valid. The native decoder owns that check.
///
/// Framing references: ISO/IEC 11172-3 / 13818-3 MPEG audio headers; ETSI TS 102
/// 366 §§4.3–4.4 and E.1.2–E.1.3 (AC-3 / E-AC-3); Matroska codec mappings.
/// https://www.etsi.org/deliver/etsi_ts/102300_102399/102366/01.04.01_60/ts_102366v010401p.pdf
/// https://www.matroska.org/technical/codec_specs.html
struct MatroskaAudioFormat {
    static let supportedCodecIDs: Set<String> = [
        "A_MPEG/L3", "A_AC3", "A_EAC3", "A_PCM/INT/LIT", "A_PCM/FLOAT/IEEE", "A_FLAC"
    ]

    let formatDescription: CMAudioFormatDescription
    let asbd: AudioStreamBasicDescription
    let profileName: String
    let isPCM: Bool
    let fixedFramesPerPacket: Int?
    private let codecID: String
    private let flacInfo: FLACAudioInfo?

    static func make(codecID: String, codecPrivate: Data = Data(), sampleRate: Double,
                     channels: Int, bitDepth: Int? = nil) throws -> MatroskaAudioFormat {
        guard supportedCodecIDs.contains(codecID) else {
            throw MatroskaAudioFormatError.unsupported(codecID)
        }
        guard sampleRate.isFinite, sampleRate.rounded() == sampleRate,
              (1...768_000).contains(sampleRate), (1...32).contains(channels) else {
            throw MatroskaAudioFormatError.malformed("invalid sample rate or channel count")
        }
        guard codecID == "A_FLAC" || codecPrivate.isEmpty else {
            throw MatroskaAudioFormatError.unsupported("unexpected CodecPrivate for \(codecID)")
        }
        var description = AudioStreamBasicDescription(mSampleRate: sampleRate,
            mFormatID: 0, mFormatFlags: 0, mBytesPerPacket: 0, mFramesPerPacket: 0,
            mBytesPerFrame: 0, mChannelsPerFrame: UInt32(channels), mBitsPerChannel: 0, mReserved: 0)
        let name: String
        let pcm: Bool
        let frames: Int?
        var cookie = Data()
        var flacInfo: FLACAudioInfo?
        switch codecID {
        case "A_MPEG/L3":
            guard channels <= 2,
                  [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000].contains(Int(sampleRate)) else {
                throw MatroskaAudioFormatError.unsupported("MP3 channel count or sample rate")
            }
            description.mFormatID = kAudioFormatMPEGLayer3
            frames = sampleRate >= 32_000 ? 1152 : 576
            description.mFramesPerPacket = UInt32(frames!)
            name = "MP3"; pcm = false
        case "A_AC3", "A_EAC3":
            // Independent AC-3/E-AC-3 frames carry at most 5.1 channels. E-AC-3
            // dependent substreams require grouping/channel-map handling and are
            // rejected by packetInfo rather than dropped from an access unit.
            guard channels <= 6 else {
                throw MatroskaAudioFormatError.unsupported("Dolby dependent-substream/channel-map handling is required for this channel count")
            }
            description.mFormatID = codecID == "A_AC3" ? kAudioFormatAC3 : kAudioFormatEnhancedAC3
            frames = codecID == "A_AC3" ? 1536 : nil
            description.mFramesPerPacket = UInt32(frames ?? 0)
            name = codecID == "A_AC3" ? "AC-3" : "E-AC-3"; pcm = false
        case "A_PCM/INT/LIT", "A_PCM/FLOAT/IEEE":
            guard let depth = bitDepth else {
                throw MatroskaAudioFormatError.malformed("PCM BitDepth is required")
            }
            let float = codecID == "A_PCM/FLOAT/IEEE"
            guard (float ? [32, 64] : [8, 16, 24, 32]).contains(depth) else {
                throw MatroskaAudioFormatError.unsupported("\(float ? "floating" : "integer") PCM depth \(depth)")
            }
            description.mFormatID = kAudioFormatLinearPCM
            // Matroska's 8-bit integer PCM is unsigned. Wider integer samples
            // are signed; float and integer data are little-endian/interleaved.
            let representation: AudioFormatFlags = float ? kAudioFormatFlagIsFloat : depth == 8 ? 0 : kAudioFormatFlagIsSignedInteger
            description.mFormatFlags = kAudioFormatFlagIsPacked | representation
            description.mFramesPerPacket = 1
            description.mBytesPerFrame = UInt32(channels * (depth / 8))
            description.mBytesPerPacket = description.mBytesPerFrame
            description.mBitsPerChannel = UInt32(depth)
            frames = nil; pcm = true
            name = float ? "Float\(depth)LE" : "PCM\(depth)LE"
        case "A_FLAC":
            let info = try FLACAudioInfo(codecPrivate)
            guard info.sampleRate == Int(sampleRate), info.channels == channels,
                  bitDepth == nil || bitDepth == info.depth else {
                throw MatroskaAudioFormatError.metadataMismatch("FLAC STREAMINFO rate, channels or bit depth")
            }
            description.mFormatID = kAudioFormatFLAC
            description.mBitsPerChannel = UInt32(info.depth)
            // Even fixed-block FLAC can have a shorter final frame. Each packet
            // carries its actual frame count in the packet description.
            description.mFramesPerPacket = 0
            // Apple's native FLAC magic cookie is the complete public dfLa atom:
            // size/type, version/flags, then a last STREAMINFO metadata block.
            cookie = Data([0,0,0,50,0x64,0x66,0x4c,0x61,0,0,0,0,0x80,0,0,34]) + info.streamInfo
            flacInfo = info
            frames = nil; pcm = false; name = "FLAC"
        default:
            throw MatroskaAudioFormatError.unsupported(codecID)
        }
        var native: CMAudioFormatDescription?
        let status = cookie.withUnsafeBytes { raw in
            CMAudioFormatDescriptionCreate(allocator: nil, asbd: &description,
                layoutSize: 0, layout: nil, magicCookieSize: cookie.count, magicCookie: raw.baseAddress,
                extensions: nil, formatDescriptionOut: &native)
        }
        guard status == noErr, let native else {
            throw MatroskaAudioFormatError.nativeFormat("\(name) (\(status))")
        }
        return MatroskaAudioFormat(formatDescription: native, asbd: description,
            profileName: name, isPCM: pcm, fixedFramesPerPacket: frames, codecID: codecID, flacInfo: flacInfo)
    }

    func packetInfo(_ data: Data) throws -> MatroskaAudioPacketInfo {
        guard !data.isEmpty else { throw MatroskaAudioFormatError.malformed("empty audio packet") }
        if isPCM {
            let bytesPerFrame = Int(asbd.mBytesPerFrame)
            guard bytesPerFrame > 0, data.count % bytesPerFrame == 0 else {
                throw MatroskaAudioFormatError.malformed("PCM block does not contain complete interleaved sample frames")
            }
            let frames = data.count / bytesPerFrame
            return MatroskaAudioPacketInfo(encodedByteCount: data.count, sampleFrames: frames,
                duration: Double(frames) / asbd.mSampleRate)
        }
        let header: ElementaryAudioHeader
        switch codecID {
        case "A_MPEG/L3": header = try Self.mp3Header(data)
        case "A_AC3": header = try Self.dolbyHeader(data, enhanced: false)
        case "A_EAC3": header = try Self.dolbyHeader(data, enhanced: true)
        case "A_FLAC":
            guard let flacInfo else { throw MatroskaAudioFormatError.malformed("missing FLAC STREAMINFO") }
            header = try Self.flacHeader(data, info: flacInfo)
        default: throw MatroskaAudioFormatError.unsupported(codecID)
        }
        guard header.sampleRate == Int(asbd.mSampleRate), header.channels == Int(asbd.mChannelsPerFrame) else {
            throw MatroskaAudioFormatError.metadataMismatch("\(profileName) packet is \(header.sampleRate) Hz / \(header.channels) channels; track declares \(Int(asbd.mSampleRate)) Hz / \(asbd.mChannelsPerFrame) channels")
        }
        guard header.byteCount <= data.count else {
            throw MatroskaAudioFormatError.malformed("truncated \(profileName) frame (needs \(header.byteCount) bytes, has \(data.count))")
        }
        return MatroskaAudioPacketInfo(encodedByteCount: header.byteCount, sampleFrames: header.frames,
            duration: Double(header.frames) / Double(header.sampleRate))
    }

    private struct ElementaryAudioHeader {
        let byteCount: Int, sampleRate: Int, channels: Int, frames: Int
    }

    private struct FLACAudioInfo {
        let sampleRate: Int, channels: Int, depth: Int, minBlock: Int, maxBlock: Int
        let streamInfo: Data
        init(_ data: Data) throws {
            guard data.count >= 42, data.prefix(4) == Data("fLaC".utf8) else {
                throw MatroskaAudioFormatError.malformed("FLAC CodecPrivate must contain fLaC metadata")
            }
            let b = Array(data)
            guard b[4] & 0x7f == 0, b[5] == 0, b[6] == 0, b[7] == 34 else {
                throw MatroskaAudioFormatError.malformed("FLAC STREAMINFO must be first and exactly 34 bytes")
            }
            streamInfo = Data(b[8..<42])
            minBlock = Int(b[8]) << 8 | Int(b[9])
            maxBlock = Int(b[10]) << 8 | Int(b[11])
            var packed: UInt64 = 0
            for byte in b[18..<26] { packed = packed << 8 | UInt64(byte) }
            sampleRate = Int(packed >> 44)
            channels = Int((packed >> 41) & 7) + 1
            depth = Int((packed >> 36) & 31) + 1
            guard sampleRate > 0, minBlock >= 16, minBlock <= maxBlock,
                  (4...32).contains(depth) else {
                throw MatroskaAudioFormatError.malformed("invalid FLAC STREAMINFO")
            }
            var cursor = 4
            while true {
                guard cursor + 4 <= b.count else { throw MatroskaAudioFormatError.malformed("truncated FLAC metadata") }
                let type = b[cursor] & 0x7f
                let last = b[cursor] & 0x80 != 0
                let length = Int(b[cursor + 1]) << 16 | Int(b[cursor + 2]) << 8 | Int(b[cursor + 3])
                guard type != 127, (cursor == 4 || type != 0), length <= b.count - cursor - 4 else {
                    throw MatroskaAudioFormatError.malformed("invalid FLAC metadata block")
                }
                cursor += 4 + length
                if last {
                    guard cursor == b.count else { throw MatroskaAudioFormatError.malformed("FLAC CodecPrivate contains trailing audio/data") }
                    break
                }
            }
        }
    }

    private static func flacHeader(_ data: Data, info: FLACAudioInfo) throws -> ElementaryAudioHeader {
        var bits = AudioHeaderBits(data)
        guard try bits.read(15) == 0x7ffc else { throw MatroskaAudioFormatError.malformed("FLAC frame sync/reserved bit") }
        let variable = try bits.read(1) != 0
        let blockCode = try bits.read(4), rateCode = try bits.read(4)
        let channelCode = try bits.read(4), depthCode = try bits.read(3)
        guard try bits.read(1) == 0, blockCode != 0, rateCode != 15,
              channelCode <= 10, depthCode != 3 else {
            throw MatroskaAudioFormatError.malformed("reserved FLAC frame parameters")
        }
        // Skip the explicit UTF-8-coded frame/sample number, validating its
        // shortest representation and the format's 31/36-bit coordinate limit.
        let first = try bits.read(8)
        if first >= 128 {
            var count = 0
            while count < 8, first & (0x80 >> count) != 0 { count += 1 }
            guard (2...(variable ? 7 : 6)).contains(count) else { throw MatroskaAudioFormatError.malformed("invalid FLAC frame/sample number") }
            var number = UInt64(first & ((1 << (7 - count)) - 1))
            for _ in 1..<count {
                let next = try bits.read(8)
                guard next & 0xc0 == 0x80 else { throw MatroskaAudioFormatError.malformed("invalid FLAC coded-number continuation") }
                number = number << 6 | UInt64(next & 63)
            }
            let minimum: UInt64 = count == 2 ? 128 : UInt64(1) << (5 * count - 4)
            guard number >= minimum, number < UInt64(1) << (variable ? 36 : 31) else {
                throw MatroskaAudioFormatError.malformed("overlong/out-of-range FLAC frame/sample number")
            }
        }
        let block: Int
        switch blockCode {
        case 1: block = 192
        case 2...5: block = 576 << (blockCode - 2)
        case 6: block = try bits.read(8) + 1
        case 7: block = try bits.read(16) + 1
        default: block = 256 << (blockCode - 8)
        }
        let rate: Int
        switch rateCode {
        case 0: rate = info.sampleRate
        case 1...11: rate = [0,88200,176400,192000,8000,16000,22050,24000,32000,44100,48000,96000][rateCode]
        case 12: rate = try bits.read(8) * 1000
        case 13: rate = try bits.read(16)
        default: rate = try bits.read(16) * 10
        }
        let depth = depthCode == 0 ? info.depth : [0,8,12,0,16,20,24,32][depthCode]
        let channels = channelCode <= 7 ? channelCode + 1 : 2
        guard block <= info.maxBlock, rate == info.sampleRate, depth == info.depth, channels == info.channels else {
            throw MatroskaAudioFormatError.metadataMismatch("FLAC frame differs from STREAMINFO")
        }
        let headerBytes = bits.bytePosition
        let expectedCRC8 = try bits.read(8)
        guard crc(data.prefix(headerBytes), width: 8, polynomial: 0x07) == expectedCRC8 else {
            throw MatroskaAudioFormatError.malformed("FLAC frame header CRC8 mismatch")
        }
        guard data.count >= headerBytes + 3 else { throw MatroskaAudioFormatError.malformed("truncated FLAC frame") }
        let b = Array(data.suffix(2))
        guard crc(data.dropLast(2), width: 16, polynomial: 0x8005) == Int(b[0]) << 8 | Int(b[1]) else {
            throw MatroskaAudioFormatError.malformed("FLAC frame CRC16 mismatch")
        }
        return ElementaryAudioHeader(byteCount: data.count, sampleRate: rate, channels: channels, frames: block)
    }

    private static func crc(_ bytes: Data, width: Int, polynomial: Int) -> Int {
        var value = 0
        let mask = (1 << width) - 1
        for byte in bytes {
            value ^= Int(byte) << (width - 8)
            for _ in 0..<8 { value = (value << 1 ^ (value & (1 << (width - 1)) == 0 ? 0 : polynomial)) & mask }
        }
        return value
    }

    private static func mp3Header(_ data: Data) throws -> ElementaryAudioHeader {
        guard data.count >= 4 else { throw MatroskaAudioFormatError.malformed("truncated MP3 header") }
        let b = Array(data.prefix(4))
        guard b[0] == 0xff, b[1] & 0xe0 == 0xe0 else {
            throw MatroskaAudioFormatError.malformed("missing MPEG audio sync")
        }
        let version = Int((b[1] >> 3) & 3)
        let layer = Int((b[1] >> 1) & 3)
        let bitrateIndex = Int(b[2] >> 4)
        let rateIndex = Int((b[2] >> 2) & 3)
        guard version != 1, layer == 1, rateIndex != 3, bitrateIndex != 15 else {
            throw MatroskaAudioFormatError.malformed("reserved MPEG version, non-Layer-III data, bitrate or sample rate")
        }
        guard bitrateIndex != 0 else {
            throw MatroskaAudioFormatError.unsupported("free-format MP3 requires frame-size discovery")
        }
        guard b[3] & 3 != 2 else { throw MatroskaAudioFormatError.malformed("reserved MP3 emphasis") }
        let mpeg1 = version == 3
        let rates = [44100, 48000, 32000]
        let sampleRate = rates[rateIndex] / (mpeg1 ? 1 : version == 2 ? 2 : 4)
        let bitrates = mpeg1
            ? [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
            : [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]
        let frameBytes = (mpeg1 ? 144_000 : 72_000) * bitrates[bitrateIndex] / sampleRate + Int((b[2] >> 1) & 1)
        return ElementaryAudioHeader(byteCount: frameBytes, sampleRate: sampleRate,
            channels: b[3] >> 6 == 3 ? 1 : 2, frames: mpeg1 ? 1152 : 576)
    }

    private static func dolbyHeader(_ data: Data, enhanced: Bool) throws -> ElementaryAudioHeader {
        var bits = AudioHeaderBits(data)
        guard try bits.read(16) == 0x0b77 else {
            throw MatroskaAudioFormatError.malformed("missing Dolby audio sync")
        }
        let channelCounts = [2, 1, 2, 3, 3, 4, 4, 5]
        if enhanced {
            let streamType = try bits.read(2)
            let substream = try bits.read(3)
            let frameBytes = (try bits.read(11) + 1) * 2
            guard streamType != 3 else { throw MatroskaAudioFormatError.malformed("reserved E-AC-3 stream type") }
            guard streamType != 1, substream == 0 else {
                throw MatroskaAudioFormatError.unsupported("E-AC-3 dependent or multiple substreams require access-unit grouping")
            }
            let rateCode = try bits.read(2)
            let sampleRate: Int, blocks: Int
            if rateCode == 3 {
                let halfRate = try bits.read(2)
                guard halfRate < 3 else { throw MatroskaAudioFormatError.malformed("reserved E-AC-3 sample rate") }
                sampleRate = [24000, 22050, 16000][halfRate]; blocks = 6
            } else {
                sampleRate = [48000, 44100, 32000][rateCode]
                blocks = [1, 2, 3, 6][try bits.read(2)]
            }
            let mode = try bits.read(3)
            let lfe = try bits.read(1)
            let bsid = try bits.read(5)
            guard (11...16).contains(bsid), frameBytes >= 7 else {
                throw MatroskaAudioFormatError.malformed("invalid E-AC-3 bitstream ID or frame size")
            }
            return ElementaryAudioHeader(byteCount: frameBytes, sampleRate: sampleRate,
                channels: channelCounts[mode] + lfe, frames: blocks * 256)
        }
        _ = try bits.read(16) // CRC1: payload integrity is checked by the decoder.
        let rateCode = try bits.read(2)
        let sizeCode = try bits.read(6)
        let bsid = try bits.read(5)
        _ = try bits.read(3) // Bitstream mode does not change packet duration.
        let mode = try bits.read(3)
        if mode & 1 != 0 && mode != 1 { _ = try bits.read(2) }
        if mode & 4 != 0 { _ = try bits.read(2) }
        if mode == 2 { _ = try bits.read(2) }
        let lfe = try bits.read(1)
        guard rateCode < 3, sizeCode < 38, bsid <= 10 else {
            throw MatroskaAudioFormatError.malformed("invalid AC-3 rate, size or bitstream ID")
        }
        let bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640]
        let bitrate = bitrates[sizeCode / 2]
        let frameBytes: Int
        switch rateCode {
        case 0: frameBytes = bitrate * 4
        case 1: frameBytes = (320 * bitrate / 147 + (sizeCode & 1)) * 2
        default: frameBytes = bitrate * 6
        }
        let sampleRate = [48000, 44100, 32000][rateCode] >> max(0, bsid - 8)
        return ElementaryAudioHeader(byteCount: frameBytes, sampleRate: sampleRate,
            channels: channelCounts[mode] + lfe, frames: 1536)
    }
}

private struct AudioHeaderBits {
    private let bytes: [UInt8]
    private var position = 0
    init(_ data: Data) { bytes = Array(data.prefix(16)) }
    var bytePosition: Int { position / 8 }
    mutating func read(_ count: Int) throws -> Int {
        guard count >= 0, count <= bytes.count * 8 - position else {
            throw MatroskaAudioFormatError.malformed("truncated elementary audio header")
        }
        var value = 0
        for _ in 0..<count {
            value = value << 1 | Int((bytes[position / 8] >> (7 - position % 8)) & 1)
            position += 1
        }
        return value
    }
}
