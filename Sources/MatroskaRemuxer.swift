import Foundation
import AVFoundation
import CoreMedia
import AudioToolbox

/// Original EBML/Matroska demuxing and container remuxing. H.264/HEVC and AAC are
/// passed through unchanged; playback/decoding remains Apple's system implementation.
/// References: RFC 9559 §§5,10,11; matroska.org/technical/codec_specs.html.
struct MatroskaRemuxResult: Sendable {
    let codecSummary: String
    let duration: Double
    var subtitles: [MatroskaTextTrack] = []
    var bitmapSubtitles: [PGSSubtitleTrack] = []
}

enum MatroskaRemuxError: Error, LocalizedError {
    case malformed(String), unsupported(String), writing(String)
    var errorDescription: String? {
        switch self {
        case .malformed(let s): return "Malformed Matroska file: \(s)"
        case .unsupported(let s): return "This Matroska variant is not supported: \(s)"
        case .writing(let s): return "Matroska remux failed: \(s)"
        }
    }
}

enum MatroskaRemuxer {
    /// Supports one AVC/HEVC video track and supported audio/text tracks.
    /// Other codecs are rejected, never silently discarded.
    /// Xiph, fixed and EBML lacing are supported. Unknown-sized Segment is
    /// supported; unknown-sized Clusters and codec-changing Blocks are not.
    static func remux(input: URL, output: URL) async throws -> MatroskaRemuxResult {
        let work = Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            let document = try MKVDocument(url: input)
            return try await document.write(to: output)
        }
        return try await withTaskCancellationHandler(operation: { try await work.value }, onCancel: { work.cancel() })
    }
}

private struct MKVElement {
    let id: UInt64
    let payload: Range<Int>
    let next: Int
}

private final class MKVBytes {
    let data: Data
    private var elementCount = 0
    init(_ data: Data) { self.data = data }
    func require(_ offset: Int, _ count: Int, limit: Int? = nil) throws {
        let end = limit ?? data.count
        guard offset >= 0, count >= 0, offset <= end, count <= end - offset, end <= data.count else {
            throw MatroskaRemuxError.malformed("truncated element")
        }
    }
    func vint(at offset: Int, limit: Int, id: Bool = false) throws -> (UInt64, Int, Bool) {
        try require(offset, 1, limit: limit)
        let first = data[offset]
        guard first != 0 else { throw MatroskaRemuxError.malformed("zero EBML VINT prefix") }
        var length = 1, mask: UInt8 = 0x80
        while first & mask == 0 { length += 1; mask >>= 1 }
        guard length <= (id ? 4 : 8) else { throw MatroskaRemuxError.malformed("oversized EBML VINT") }
        try require(offset, length, limit: limit)
        var value = UInt64(id ? first : first & (mask - 1))
        if length > 1 { for i in 1..<length { value = (value << 8) | UInt64(data[offset + i]) } }
        let unknown = !id && value == (UInt64(1) << (7 * length)) - 1
        return (value, length, unknown)
    }
    func element(at offset: Int, limit: Int, allowUnknownSegment: Bool = false) throws -> MKVElement {
        elementCount += 1
        if elementCount % 1024 == 0 { try Task.checkCancellation() }
        guard elementCount <= 5_000_000 else { throw MatroskaRemuxError.unsupported("element-count limit exceeded") }
        let (id, idLength, _) = try vint(at: offset, limit: limit, id: true)
        let (size, sizeLength, unknown) = try vint(at: offset + idLength, limit: limit)
        let start = offset + idLength + sizeLength
        if unknown {
            guard allowUnknownSegment && id == 0x18538067 else {
                throw MatroskaRemuxError.unsupported("unknown-sized elements other than Segment")
            }
            return MKVElement(id: id, payload: start..<limit, next: limit)
        }
        guard size <= UInt64(limit - start) else { throw MatroskaRemuxError.malformed("element exceeds its parent") }
        let end = start + Int(size)
        return MKVElement(id: id, payload: start..<end, next: end)
    }
    func children(_ range: Range<Int>, _ action: (MKVElement) throws -> Void) throws {
        var offset = range.lowerBound
        while offset < range.upperBound {
            let item = try element(at: offset, limit: range.upperBound)
            try action(item)
            offset = item.next
        }
    }
    func uint(_ range: Range<Int>) throws -> UInt64 {
        guard (1...8).contains(range.count) else { throw MatroskaRemuxError.malformed("invalid unsigned integer width") }
        return data[range].reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
    }
    func int(_ range: Range<Int>) throws -> Int {
        let value = try uint(range)
        guard value <= UInt64(Int.max) else { throw MatroskaRemuxError.malformed("integer exceeds host range") }
        return Int(value)
    }
    func signed(_ range: Range<Int>) throws -> Int64 {
        let value = try uint(range)
        let bits = range.count * 8
        if bits == 64 { return Int64(bitPattern: value) }
        return data[range.lowerBound] & 0x80 == 0 ? Int64(value) : Int64(value) - (Int64(1) << bits)
    }
    func float(_ range: Range<Int>) throws -> Double {
        let bits = try uint(range)
        let value: Double
        if range.count == 4 { value = Double(Float(bitPattern: UInt32(bits))) }
        else if range.count == 8 { value = Double(bitPattern: bits) }
        else { throw MatroskaRemuxError.malformed("invalid float width") }
        guard value.isFinite else { throw MatroskaRemuxError.malformed("non-finite float") }
        return value
    }
    func string(_ range: Range<Int>) throws -> String {
        guard range.count <= 4096, let value = String(data: data[range], encoding: .utf8) else {
            throw MatroskaRemuxError.malformed("invalid or oversized text")
        }
        return value.trimmingCharacters(in: .controlCharacters)
    }
}

private struct MKVTrack {
    var number: UInt64 = 0
    var type: UInt64 = 0
    var codec = ""
    var name = ""
    var language = "und"
    var languageIETF: String?
    var isDefault = true
    var isForced = false
    var isEnabled = true
    var privateData = Data()
    var width = 0
    var height = 0
    var sampleRate: Double = 8000
    var outputSampleRate: Double?
    var channels = 1
    var bitDepth: Int?
    var defaultDuration: Double = 0
    var timestampScale: Double = 1
    var format: CMFormatDescription?
    var aacFrames = 1024
    var audioPacketFrames = 1024
    var audioProfile = "AAC-LC"
    var audioFormat: MatroskaAudioFormat?
    var opus: OpusConfiguration?
    var codecDelayNanoseconds: UInt64?
    var seekPreRollNanoseconds: UInt64 = 0
}

private struct MKVFrame {
    let track: UInt64
    let bytes: Range<Int>
    let pts: Double
    var duration: Double
    let keyframe: Bool
    var decodeTime: Double? = nil
    var audioSampleFrames: Int? = nil
    var trimStart: Double = 0
    var trimEnd: Double = 0
}

private final class MKVDocument {
    let bytes: MKVBytes
    var tracks: [UInt64: MKVTrack] = [:]
    var trackOrder: [UInt64] = []
    var frames: [MKVFrame] = []
    var timestampScale: Double = 1_000_000
    let sourceURL: URL

    init(url: URL) throws {
        guard url.isFileURL else { throw MatroskaRemuxError.unsupported("only local files can be remuxed") }
        sourceURL = url.standardizedFileURL
        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
        guard size > 0, size <= 8_589_934_592 else { throw MatroskaRemuxError.unsupported("files must be nonempty and at most 8 GiB") }
        bytes = MKVBytes(try Data(contentsOf: url, options: .mappedIfSafe))
        try parse()
    }

    private func parse() throws {
        let header = try bytes.element(at: 0, limit: bytes.data.count)
        guard header.id == 0x1A45DFA3 else { throw MatroskaRemuxError.malformed("missing EBML header") }
        var docType: String?
        try bytes.children(header.payload) { element in
            if element.id == 0x4282 { docType = try bytes.string(element.payload) }
            if element.id == 0x4285, try bytes.uint(element.payload) > 4 { throw MatroskaRemuxError.unsupported("Matroska reader version above 4") }
            if element.id == 0x42F7, try bytes.uint(element.payload) > 1 { throw MatroskaRemuxError.unsupported("EBML reader version above 1") }
        }
        guard docType == "matroska" else { throw MatroskaRemuxError.unsupported("DocType must be matroska") }
        var cursor = header.next
        var segment: MKVElement?
        while cursor < bytes.data.count {
            let element = try bytes.element(at: cursor, limit: bytes.data.count, allowUnknownSegment: true)
            if element.id == 0x18538067 {
                guard segment == nil else { throw MatroskaRemuxError.unsupported("linked or multiple Segments") }
                segment = element
            } else if element.id != 0xEC && element.id != 0xBF {
                throw MatroskaRemuxError.malformed("unexpected top-level element")
            }
            cursor = element.next
        }
        guard let segment else { throw MatroskaRemuxError.malformed("missing Segment") }
        var clusters: [Range<Int>] = []
        try bytes.children(segment.payload) { element in
            switch element.id {
            case 0x1549A966:
                try bytes.children(element.payload) { info in
                    if info.id == 0x2AD7B1 { timestampScale = Double(try bytes.uint(info.payload)) }
                    if [0x3CB923, 0x3EB923].contains(info.id) { throw MatroskaRemuxError.unsupported("linked Segments") }
                }
            case 0x1654AE6B:
                try bytes.children(element.payload) { if $0.id == 0xAE { try parseTrack($0.payload) } }
            case 0x1F43B675: clusters.append(element.payload)
            case 0x18538067: throw MatroskaRemuxError.unsupported("nested or linked Segments")
            default: break // Metadata, seek indexes, tags and attachments carry no selected A/V samples.
            }
        }
        guard timestampScale > 0, timestampScale <= 1_000_000_000 else { throw MatroskaRemuxError.unsupported("TimestampScale outside supported range") }
        guard !tracks.isEmpty else { throw MatroskaRemuxError.malformed("no supported tracks") }
        guard tracks.values.filter({ $0.isEnabled && $0.type == 1 }).count <= 1,
              tracks.values.filter({ $0.isEnabled && $0.type == 2 }).count <= 64,
              tracks.values.filter({ $0.isEnabled && $0.type == 17 }).count <= 64 else {
            throw MatroskaRemuxError.unsupported("at most one video, 64 audio and 64 text tracks are supported; no tracks were discarded")
        }
        for number in trackOrder where tracks[number]!.isEnabled && tracks[number]!.type != 17 {
            var track = tracks[number]!
            track.format = try formatDescription(track: &track)
            tracks[number] = track
        }
        for cluster in clusters { try parseCluster(cluster) }
        guard !frames.isEmpty else { throw MatroskaRemuxError.malformed("no media frames") }
        for number in trackOrder {
            guard let opus = tracks[number]!.opus else { continue }
            var remaining = opus.codecDelaySeconds
            for index in frames.indices where frames[index].track == number {
                let amount = min(remaining, max(0, frames[index].duration - frames[index].trimEnd))
                frames[index].trimStart = amount; remaining = max(0, remaining - amount)
            }
            guard remaining < 1e-9 else { throw MatroskaRemuxError.malformed("Opus pre-skip exceeds its audio duration") }
        }
        // Matroska carries PTS and coding order, rather than explicit DTS.
        // Reordered video is supported only with a complete bounded CFR timeline.
        for number in trackOrder where tracks[number]!.isEnabled && tracks[number]!.type != 17 {
            let indices = frames.indices.filter { frames[$0].track == number }
            guard !indices.isEmpty else { throw MatroskaRemuxError.malformed("track \(number) contains no frames") }
            let track = tracks[number]!
            let presentationTimes = indices.map { frames[$0].pts }
            let reordered = zip(presentationTimes, presentationTimes.dropFirst()).contains { $1 <= $0 }
            if track.type == 1 && reordered {
                guard frames[indices[0]].keyframe else { throw MatroskaRemuxError.unsupported("reordered video must begin with a random-access frame") }
                let decodeTimes: [Double]
                do {
                    decodeTimes = try AVCDecodeTimeline.reconstruct(presentationTimes: presentationTimes,
                        frameDuration: track.defaultDuration,
                        timestampTolerance: timestampScale * track.timestampScale / 1_000_000_000 + 0.000001)
                } catch { throw MatroskaRemuxError.unsupported(error.localizedDescription) }
                for (position, index) in indices.enumerated() {
                    let precision = timestampScale * track.timestampScale / 1_000_000_000 + 0.000001
                    guard frames[index].duration <= 0 || abs(frames[index].duration - track.defaultDuration) <= precision else {
                        throw MatroskaRemuxError.unsupported("reordered video frame durations differ from DefaultDuration")
                    }
                    frames[index].duration = track.defaultDuration
                    frames[index].decodeTime = decodeTimes[position]
                }
                continue
            }
            for i in indices.indices {
                let index = indices[i]
                if i + 1 < indices.count {
                    let delta = frames[indices[i + 1]].pts - frames[index].pts
                    guard delta > 0 else { throw MatroskaRemuxError.unsupported("non-increasing audio timestamps") }
                    if frames[index].duration <= 0 { frames[index].duration = delta }
                } else if frames[index].duration <= 0 {
                    guard i > 0 else { throw MatroskaRemuxError.unsupported("single video frame without DefaultDuration or BlockDuration") }
                    frames[index].duration = frames[indices[i - 1]].duration
                }
                guard frames[index].duration > 0, frames[index].duration.isFinite else { throw MatroskaRemuxError.malformed("invalid sample duration") }
            }
        }
    }

    private func parseTrack(_ range: Range<Int>) throws {
        var track = MKVTrack()
        var displayWidth = 0, displayHeight = 0
        var unsupportedFeatures = Set<String>()
        try bytes.children(range) { element in
            switch element.id {
            case 0xD7: track.number = try bytes.uint(element.payload)
            case 0x83: track.type = try bytes.uint(element.payload)
            case 0x86: track.codec = try bytes.string(element.payload)
            case 0x536E: track.name = try bytes.string(element.payload)
            case 0x22B59C: track.language = try bytes.string(element.payload)
            case 0x22B59D: track.languageIETF = try bytes.string(element.payload)
            case 0x88: track.isDefault = try bytes.uint(element.payload) != 0
            case 0x55AA: track.isForced = try bytes.uint(element.payload) != 0
            case 0xB9: track.isEnabled = try bytes.uint(element.payload) != 0
            case 0x63A2:
                guard element.payload.count <= 1_048_576 else { throw MatroskaRemuxError.unsupported("oversized CodecPrivate") }
                track.privateData = Data(bytes.data[element.payload])
            case 0x23E383: track.defaultDuration = Double(try bytes.uint(element.payload)) / 1_000_000_000
            case 0x23314F: track.timestampScale = try bytes.float(element.payload)
            case 0x56AA:
                track.codecDelayNanoseconds = try bytes.uint(element.payload)
            case 0x56BB: track.seekPreRollNanoseconds = try bytes.uint(element.payload)
            case 0x537F:
                if try bytes.signed(element.payload) != 0 { unsupportedFeatures.insert("nonzero deprecated TrackOffset") }
            case 0x6D80, 0xE2:
                unsupportedFeatures.insert("track content encoding/encryption or TrackOperation")
            case 0xE0:
                try bytes.children(element.payload) { video in
                    switch video.id {
                    case 0xB0: track.width = try bytes.int(video.payload)
                    case 0xBA: track.height = try bytes.int(video.payload)
                    case 0x54B0: displayWidth = try bytes.int(video.payload)
                    case 0x54BA: displayHeight = try bytes.int(video.payload)
                    case 0x54B2:
                        if try bytes.uint(video.payload) != 0 { unsupportedFeatures.insert("non-pixel video DisplayUnit") }
                    case 0x54AA, 0x54BB, 0x54CC, 0x54DD, 0x53B8, 0x53C0:
                        if try bytes.uint(video.payload) != 0 { unsupportedFeatures.insert("video crop, stereo or alpha mode") }
                    case 0x7670: unsupportedFeatures.insert("video projection/rotation metadata")
                    default: break
                    }
                }
            case 0xE1:
                try bytes.children(element.payload) { audio in
                    if audio.id == 0xB5 { track.sampleRate = try bytes.float(audio.payload) }
                    if audio.id == 0x9F { track.channels = try bytes.int(audio.payload) }
                    if audio.id == 0x6264 { track.bitDepth = try bytes.int(audio.payload) }
                    if audio.id == 0x78B5 { track.outputSampleRate = try bytes.float(audio.payload) }
                }
            default: break
            }
        }
        guard track.number > 0, tracks[track.number] == nil else { throw MatroskaRemuxError.malformed("invalid or duplicate TrackNumber") }
        guard track.timestampScale > 0 else { throw MatroskaRemuxError.malformed("invalid TrackTimestampScale") }
        if track.isEnabled, let feature = unsupportedFeatures.sorted().first { throw MatroskaRemuxError.unsupported(feature) }
        guard !track.isEnabled || (track.type == 1 && ["V_MPEG4/ISO/AVC", "V_MPEGH/ISO/HEVC"].contains(track.codec))
            || (track.type == 2 && (["A_AAC", "A_OPUS"].contains(track.codec) || MatroskaAudioFormat.supportedCodecIDs.contains(track.codec)))
            || (track.type == 17 && ["S_TEXT/UTF8", "S_TEXT/ASS", "S_TEXT/SSA", "S_TEXT/WEBVTT", "S_HDMV/PGS"].contains(track.codec)) else {
            throw MatroskaRemuxError.unsupported("track \(track.number): \(track.codec), type \(track.type); this codec has no verified playback path yet (nothing was dropped)")
        }
        if track.isEnabled, track.codec != "A_OPUS", (track.codecDelayNanoseconds ?? 0) != 0 {
            throw MatroskaRemuxError.unsupported("CodecDelay trimming is currently supported for Opus only")
        }
        if track.type == 1 && track.isEnabled {
            guard (1...16384).contains(track.width), (1...16384).contains(track.height) else { throw MatroskaRemuxError.malformed("invalid video dimensions") }
            guard (displayWidth == 0 || displayWidth == track.width) && (displayHeight == 0 || displayHeight == track.height) else { throw MatroskaRemuxError.unsupported("non-square-pixel display dimensions") }
        }
        tracks[track.number] = track
        trackOrder.append(track.number)
    }

    private func parseCluster(_ range: Range<Int>) throws {
        var clusterTimestamp: UInt64?
        var blocks: [MKVElement] = []
        try bytes.children(range) { element in
            if element.id == 0xE7 { clusterTimestamp = try bytes.uint(element.payload) }
            if element.id == 0xA3 || element.id == 0xA0 { blocks.append(element) }
        }
        guard let timestamp = clusterTimestamp else { throw MatroskaRemuxError.malformed("Cluster lacks Timestamp") }
        for element in blocks {
            if element.id == 0xA3 { try parseBlock(element.payload, clusterTimestamp: timestamp, durationTicks: nil, groupKeyframe: nil) }
            else {
                var block: Range<Int>?, duration: Double?, hasReference = false, discardPadding: Int64 = 0
                try bytes.children(element.payload) { child in
                    switch child.id {
                    case 0xA1:
                        guard block == nil else { throw MatroskaRemuxError.malformed("multiple Blocks in BlockGroup") }
                        block = child.payload
                    case 0x9B: duration = Double(try bytes.uint(child.payload))
                    case 0xFB: hasReference = true
                    case 0x75A2:
                        discardPadding = try bytes.signed(child.payload)
                    case 0xA4, 0x75A1, 0xA2: throw MatroskaRemuxError.unsupported("CodecState, BlockAdditions or BlockVirtual")
                    default: break
                    }
                }
                guard let block else { throw MatroskaRemuxError.malformed("BlockGroup lacks Block") }
                let firstFrame = frames.count
                try parseBlock(block, clusterTimestamp: timestamp, durationTicks: duration, groupKeyframe: !hasReference)
                if discardPadding != 0 && firstFrame < frames.count {
                    guard firstFrame < frames.count, tracks[frames[firstFrame].track]!.opus != nil, discardPadding > 0 else {
                        throw MatroskaRemuxError.unsupported("DiscardPadding supports positive Opus tail trimming only")
                    }
                    var remaining = Double(discardPadding) / 1_000_000_000
                    for index in (firstFrame..<frames.count).reversed() {
                        let amount = min(remaining, frames[index].duration)
                        frames[index].trimEnd = amount; remaining = max(0, remaining - amount)
                    }
                    guard remaining < 1e-9 else { throw MatroskaRemuxError.malformed("Opus tail padding exceeds its Block duration") }
                }
            }
        }
    }

    private func parseBlock(_ range: Range<Int>, clusterTimestamp: UInt64, durationTicks: Double?, groupKeyframe: Bool?) throws {
        guard range.count <= 67_108_864 else { throw MatroskaRemuxError.unsupported("Block larger than 64 MiB") }
        let (number, length, unknown) = try bytes.vint(at: range.lowerBound, limit: range.upperBound)
        guard !unknown, let track = tracks[number] else { throw MatroskaRemuxError.malformed("Block references an unknown track") }
        guard track.isEnabled else { return }
        var cursor = range.lowerBound + length
        try bytes.require(cursor, 3, limit: range.upperBound)
        let relative = Int16(bitPattern: UInt16(bytes.data[cursor]) << 8 | UInt16(bytes.data[cursor + 1]))
        let flags = bytes.data[cursor + 2]
        cursor += 3
        if flags & 0x08 != 0 { throw MatroskaRemuxError.unsupported("invisible frames") }
        let lacing = (flags >> 1) & 3
        var sizes: [Int] = []
        if lacing == 0 { sizes = [range.upperBound - cursor] }
        else {
            try bytes.require(cursor, 1, limit: range.upperBound)
            let count = Int(bytes.data[cursor]) + 1
            cursor += 1
            guard count >= 2 else { throw MatroskaRemuxError.malformed("lacing requires multiple frames") }
            switch lacing {
            case 1:
                for _ in 0..<(count - 1) {
                    var size = 0, value: UInt8 = 255
                    while value == 255 {
                        try bytes.require(cursor, 1, limit: range.upperBound)
                        value = bytes.data[cursor]; cursor += 1; size += Int(value)
                    }
                    sizes.append(size)
                }
            case 2:
                guard (range.upperBound - cursor) % count == 0 else { throw MatroskaRemuxError.malformed("fixed lace size mismatch") }
                sizes = [Int](repeating: (range.upperBound - cursor) / count, count: count)
            case 3:
                let (first, width, unknown) = try bytes.vint(at: cursor, limit: range.upperBound)
                guard !unknown, first <= UInt64(range.count) else { throw MatroskaRemuxError.malformed("invalid first EBML lace size") }
                cursor += width; sizes.append(Int(first))
                if count > 2 {
                    for _ in 1..<(count - 1) {
                        let (encoded, width, unknown) = try bytes.vint(at: cursor, limit: range.upperBound)
                        guard !unknown else { throw MatroskaRemuxError.malformed("invalid EBML lace delta") }
                        cursor += width
                        let bias = (Int64(1) << (7 * width - 1)) - 1
                        let next = Int64(sizes.last!) + Int64(encoded) - bias
                        guard next > 0, next <= Int64(range.count) else { throw MatroskaRemuxError.malformed("invalid EBML lace size") }
                        sizes.append(Int(next))
                    }
                }
            default: break
            }
            if lacing != 2 {
                let used = sizes.reduce(0, +)
                guard used < range.upperBound - cursor else { throw MatroskaRemuxError.malformed("lace payload exceeds Block") }
                sizes.append(range.upperBound - cursor - used)
            }
        }
        guard sizes.allSatisfy({ $0 > 0 }) else { throw MatroskaRemuxError.malformed("empty laced frame") }
        let duration: Double
        if let durationTicks { duration = durationTicks * track.timestampScale * timestampScale / 1_000_000_000 / Double(sizes.count) }
        else if track.opus != nil { duration = 0 }
        else if let audio = track.audioFormat { duration = audio.fixedFramesPerPacket.map { Double($0) / track.sampleRate } ?? 0 }
        else if track.type == 2 { duration = Double(track.aacFrames) / track.sampleRate }
        else { duration = track.defaultDuration }
        if track.codec == "A_AAC", abs(duration - Double(track.aacFrames) / track.sampleRate) > timestampScale * track.timestampScale / 1_000_000_000 + 0.000001 {
            throw MatroskaRemuxError.unsupported("AAC BlockDuration does not match its decoded frame count")
        }
        guard sizes.count == 1 || duration > 0 || track.audioFormat != nil || track.opus != nil else { throw MatroskaRemuxError.unsupported("laced video without known frame duration") }
        let basePTS = (Double(clusterTimestamp) + Double(relative) * track.timestampScale) * timestampScale / 1_000_000_000
        guard basePTS.isFinite, abs(basePTS) < Double(Int64.max) / 1_000_000_000 - 1 else { throw MatroskaRemuxError.malformed("timestamp overflow") }
        var audioTime = basePTS
        for (index, size) in sizes.enumerated() {
            try bytes.require(cursor, size, limit: range.upperBound)
            if let opus = track.opus {
                let sampleFrames = try opus.packetSampleFrames(Data(bytes.data[cursor..<cursor + size]))
                let packetDuration = Double(sampleFrames) / 48_000
                frames.append(MKVFrame(track: number, bytes: cursor..<cursor + size,
                    pts: audioTime - opus.codecDelaySeconds, duration: packetDuration, keyframe: true,
                    audioSampleFrames: sampleFrames))
                cursor += size; audioTime += packetDuration
                continue
            }
            if let audio = track.audioFormat {
                let end = cursor + size
                while cursor < end {
                    let info = try audio.packetInfo(Data(bytes.data[cursor..<end]))
                    guard info.encodedByteCount > 0, info.encodedByteCount <= end - cursor else { throw MatroskaRemuxError.malformed("invalid elementary audio frame extent") }
                    frames.append(MKVFrame(track: number, bytes: cursor..<cursor + info.encodedByteCount,
                        pts: audioTime, duration: info.duration, keyframe: true, audioSampleFrames: info.sampleFrames))
                    cursor += info.encodedByteCount; audioTime += info.duration
                    guard frames.count <= 2_000_000 else { throw MatroskaRemuxError.unsupported("sample-count limit exceeded") }
                }
                continue
            }
            frames.append(MKVFrame(track: number, bytes: cursor..<cursor + size, pts: basePTS + Double(index) * duration,
                                   duration: duration, keyframe: groupKeyframe ?? (flags & 0x80 != 0)))
            cursor += size
        }
        guard frames.count <= 2_000_000 else { throw MatroskaRemuxError.unsupported("sample-count limit exceeded") }
    }

    private func formatDescription(track: inout MKVTrack) throws -> CMFormatDescription {
        if track.codec == "A_OPUS" {
            let opus = try OpusConfiguration.parse(track.privateData)
            try opus.validateMatroska(sampleRate: track.sampleRate, channels: track.channels,
                codecDelayNanoseconds: track.codecDelayNanoseconds)
            guard track.seekPreRollNanoseconds <= OpusConfiguration.recommendedSeekPreRollNanoseconds else { throw MatroskaRemuxError.unsupported("Opus seek preroll exceeds the native route's 80 ms support") }
            track.opus = opus; track.audioProfile = "Opus"
            return try opus.makeFormatDescription()
        }
        if track.type == 2 && track.codec != "A_AAC" {
            let audio = try MatroskaAudioFormat.make(codecID: track.codec, codecPrivate: track.privateData,
                sampleRate: track.sampleRate, channels: track.channels, bitDepth: track.bitDepth)
            track.audioFormat = audio; track.audioProfile = audio.profileName
            return audio.formatDescription
        }
        if track.codec == "V_MPEGH/ISO/HEVC" {
            do {
                let config = try HEVCConfiguration.parse(track.privateData)
                let format = try config.makeFormatDescription()
                let dimensions = CMVideoFormatDescriptionGetDimensions(format)
                guard dimensions.width == track.width, dimensions.height == track.height else { throw MatroskaRemuxError.unsupported("HEVC parameter-set dimensions differ from Matroska video dimensions") }
                return format
            } catch let error as HEVCConfigurationError {
                switch error {
                case .malformed(let detail): throw MatroskaRemuxError.malformed(detail)
                case .unsupported(let detail), .system(let detail): throw MatroskaRemuxError.unsupported(detail)
                }
            }
        }
        if track.type == 1 {
            let config = track.privateData
            guard config.count >= 7, config[0] == 1 else { throw MatroskaRemuxError.malformed("missing AVCDecoderConfigurationRecord") }
            let headerLength = Int32((config[4] & 3) + 1)
            guard [1, 2, 4].contains(headerLength) else { throw MatroskaRemuxError.unsupported("three-byte AVC NAL length") }
            var cursor = 6
            var sets: [NSData] = []
            func readSets(_ count: Int) throws {
                for _ in 0..<count {
                    guard cursor + 2 <= config.count else { throw MatroskaRemuxError.malformed("truncated AVC parameter-set length") }
                    let count = Int(config[cursor]) << 8 | Int(config[cursor + 1]); cursor += 2
                    guard count > 0, count <= config.count - cursor else { throw MatroskaRemuxError.malformed("truncated AVC parameter set") }
                    sets.append(Data(config[cursor..<cursor + count]) as NSData); cursor += count
                }
            }
            try readSets(Int(config[5] & 31))
            guard cursor < config.count else { throw MatroskaRemuxError.malformed("missing AVC PPS count") }
            let ppsCount = Int(config[cursor]); cursor += 1
            try readSets(ppsCount)
            guard sets.count >= 2 else { throw MatroskaRemuxError.malformed("AVC needs SPS and PPS") }
            let pointers = sets.map { $0.bytes.assumingMemoryBound(to: UInt8.self) }
            let sizes = sets.map(\.length)
            var description: CMFormatDescription?
            let status = pointers.withUnsafeBufferPointer { pointerBuffer in
                sizes.withUnsafeBufferPointer { sizeBuffer in
                    CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: nil, parameterSetCount: sets.count,
                        parameterSetPointers: pointerBuffer.baseAddress!, parameterSetSizes: sizeBuffer.baseAddress!,
                        nalUnitHeaderLength: headerLength, formatDescriptionOut: &description)
                }
            }
            guard status == noErr, let description else { throw MatroskaRemuxError.unsupported("system H.264 format rejected AVC configuration (\(status))") }
            let dimensions = CMVideoFormatDescriptionGetDimensions(description)
            guard dimensions.width == track.width, dimensions.height == track.height else { throw MatroskaRemuxError.unsupported("AVC parameter-set dimensions differ from Matroska PixelWidth/PixelHeight") }
            return description
        }
        let asc = track.privateData
        guard asc.count >= 2 else { throw MatroskaRemuxError.malformed("missing AAC AudioSpecificConfig") }
        let configuration: AACConfiguration
        do {
            configuration = try AACConfiguration.parse(asc)
            try configuration.validateMatroska(sampleRate: track.sampleRate,
                outputSampleRate: track.outputSampleRate, channels: track.channels)
        } catch let error as AACConfigurationError {
            switch error {
            case .unsupported(let detail): throw MatroskaRemuxError.unsupported(detail)
            case .malformed(let detail), .metadataMismatch(let detail): throw MatroskaRemuxError.malformed(detail)
            }
        }
        guard configuration.coreFrames == 1024 else { throw MatroskaRemuxError.unsupported("960-frame AAC is not supported yet") }
        track.aacFrames = configuration.coreFrames
        track.audioPacketFrames = configuration.outputFrames
        track.audioProfile = configuration.profileName
        var asbd = AudioStreamBasicDescription(mSampleRate: Double(configuration.outputSampleRate),
            mFormatID: configuration.outputFormatID, mFormatFlags: configuration.outputFormatFlags,
            mBytesPerPacket: 0, mFramesPerPacket: UInt32(configuration.outputFrames),
            mBytesPerFrame: 0, mChannelsPerFrame: UInt32(configuration.channels), mBitsPerChannel: 0, mReserved: 0)
        // Apple's AAC magic cookie is an MPEG-4 Elementary Stream Descriptor,
        // whereas Matroska CodecPrivate contains only AudioSpecificConfig.
        // Build the small public ISO descriptor structure around the unchanged ASC.
        func descriptor(_ tag: UInt8, _ payload: Data) -> Data {
            var value = payload.count
            var length = [UInt8(value & 0x7f)]
            value >>= 7
            while value > 0 { length.insert(UInt8(value & 0x7f) | 0x80, at: 0); value >>= 7 }
            return Data([tag]) + Data(length) + payload
        }
        let decoderConfig = Data([0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) + descriptor(0x05, asc)
        let cookieData = descriptor(0x03, Data([0, 0, 0]) + descriptor(0x04, decoderConfig) + descriptor(0x06, Data([2])))
        var description: CMAudioFormatDescription?
        let status = cookieData.withUnsafeBytes { cookie in
            CMAudioFormatDescriptionCreate(allocator: nil, asbd: &asbd, layoutSize: 0, layout: nil,
                magicCookieSize: cookieData.count, magicCookie: cookie.baseAddress, extensions: nil, formatDescriptionOut: &description)
        }
        guard status == noErr, let description else { throw MatroskaRemuxError.unsupported("system AAC format rejected configuration (\(status))") }
        return description
    }

    func write(to output: URL) async throws -> MatroskaRemuxResult {
        try Task.checkCancellation()
        guard output.isFileURL, output.standardizedFileURL != sourceURL else { throw MatroskaRemuxError.writing("output must be a different local file") }
        guard !FileManager.default.fileExists(atPath: output.path) else { throw MatroskaRemuxError.writing("output already exists; it was left unchanged") }
        guard ["mov", "mp4"].contains(output.pathExtension.lowercased()) else { throw MatroskaRemuxError.writing("output extension must be .mov or .mp4") }
        let writer = try AVAssetWriter(outputURL: output, fileType: output.pathExtension.lowercased() == "mov" ? .mov : .mp4)
        writer.shouldOptimizeForNetworkUse = false
        // Align movie edits to every audio sample grid, including precise trims.
        let audioRates = tracks.values.filter { $0.isEnabled && $0.type == 2 }.compactMap { track in
            track.format.flatMap { CMAudioFormatDescriptionGetRichestDecodableFormat($0)?.pointee.mASBD.mSampleRate
                ?? CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mSampleRate }
        }
        writer.movieTimeScale = try MediaClock.movieTimeScale(audioSampleRates: audioRates)
        var completed = false
        defer {
            if !completed { writer.cancelWriting(); try? FileManager.default.removeItem(at: output) }
        }
        var inputs: [UInt64: AVAssetWriterInput] = [:]
        let mediaOrder = trackOrder.filter { tracks[$0]!.isEnabled && tracks[$0]!.type != 17 }
        guard !mediaOrder.isEmpty else { throw MatroskaRemuxError.unsupported("the file contains no playable audio or video") }
        for number in mediaOrder {
            let track = tracks[number]!
            let input = AVAssetWriterInput(mediaType: track.type == 1 ? .video : .audio, outputSettings: nil, sourceFormatHint: track.format)
            input.expectsMediaDataInRealTime = false
            if track.type == 1 { input.mediaTimeScale = 1_000_000_000 }
            input.languageCode = MediaLanguage.iso639(track.language)
            input.extendedLanguageTag = MediaLanguage.bcp47(track.languageIETF)
            if !track.name.isEmpty {
                let title = AVMutableMetadataItem()
                title.identifier = .commonIdentifierTitle; title.value = track.name as NSString
                input.metadata = [title]
            }
            guard writer.canAdd(input) else { throw MatroskaRemuxError.writing("system writer rejected track \(number)") }
            writer.add(input); inputs[number] = input
        }
        let audioNumbers = mediaOrder.filter { tracks[$0]!.type == 2 }
        if audioNumbers.count > 1 {
            let defaultNumber = audioNumbers.first { tracks[$0]!.isEnabled && tracks[$0]!.isDefault }
                ?? audioNumbers.first { tracks[$0]!.isEnabled }
            let group = AVAssetWriterInputGroup(inputs: audioNumbers.map { inputs[$0]! }, defaultInput: defaultNumber.flatMap { inputs[$0] })
            guard writer.canAdd(group) else { throw MatroskaRemuxError.writing("could not preserve selectable alternate audio tracks") }
            writer.add(group)
        }
        guard writer.startWriting() else { throw MatroskaRemuxError.writing(writer.error?.localizedDescription ?? "could not start writer") }
        writer.startSession(atSourceTime: .zero)
        let mediaFrames = frames.filter { tracks[$0.track]!.type != 17 }
        let effectiveTimes = mediaFrames.filter { $0.duration > $0.trimStart + $0.trimEnd + 1e-9 }.map { $0.pts + $0.trimStart }
        let shift = -(effectiveTimes.min() ?? 0) // Normalize audible/visible time, preserving every A/V offset.
        // Each input receives samples independently. A writer may back-pressure
        // either track until the other advances, so one global append loop can deadlock.
        let grouped = Dictionary(grouping: mediaFrames, by: \.track)
        try await withThrowingTaskGroup(of: Void.self) { group in
            for number in mediaOrder {
                let input = inputs[number]!
                let trackFrames = grouped[number]!
                group.addTask {
                    for frame in trackFrames {
                        try Task.checkCancellation()
                        let deadline = Date().addingTimeInterval(15)
                        while !input.isReadyForMoreMediaData {
                            try Task.checkCancellation()
                            guard writer.status == .writing, Date() < deadline else {
                                throw MatroskaRemuxError.writing(writer.error?.localizedDescription ?? "writer stopped accepting track \(frame.track) samples at \(frame.pts)s (status \(writer.status.rawValue))")
                            }
                            try await Task.sleep(nanoseconds: 2_000_000)
                        }
                        let sample = try self.sampleBuffer(frame, shift: shift)
                        guard input.append(sample) else { throw MatroskaRemuxError.writing(writer.error?.localizedDescription ?? "sample append failed") }
                    }
                    input.markAsFinished()
                }
            }
            try await group.waitForAll()
        }
        await withTaskCancellationHandler(operation: { await writer.finishWriting() }, onCancel: { writer.cancelWriting() })
        try Task.checkCancellation()
        guard writer.status == .completed else { throw MatroskaRemuxError.writing(writer.error?.localizedDescription ?? "writer did not finish") }
        let opusCount = mediaOrder.filter { tracks[$0]!.opus != nil }.count
        if opusCount > 0 {
            let patched = try OpusPrerollPatcher.apply(to: output)
            guard patched == opusCount else { throw MatroskaRemuxError.writing("could not preserve every Opus track's seek preroll") }
        }
        let outputAsset = AVURLAsset(url: output)
        let outputTracks = try await outputAsset.load(.tracks)
        let expectedVideo = tracks.values.filter { $0.isEnabled && $0.type == 1 }.count
        let expectedAudio = tracks.values.filter { $0.isEnabled && $0.type == 2 }.count
        guard outputTracks.filter({ $0.mediaType == .video }).count == expectedVideo,
              outputTracks.filter({ $0.mediaType == .audio }).count == expectedAudio else {
            throw MatroskaRemuxError.writing("system writer did not preserve every supported track")
        }
        let duration = try await outputAsset.load(.duration).seconds
        guard duration.isFinite, duration > 0 else { throw MatroskaRemuxError.writing("output has no valid duration") }
        try Task.checkCancellation()
        var subtitles: [MatroskaTextTrack] = []
        var bitmapSubtitles: [PGSSubtitleTrack] = []
        var bitmapBytes = 0
        for number in trackOrder where tracks[number]!.isEnabled && tracks[number]!.type == 17 && tracks[number]!.codec == "S_HDMV/PGS" {
            let track = tracks[number]!
            guard track.privateData.isEmpty else { throw MatroskaRemuxError.unsupported("PGS CodecPrivate extensions are not implemented") }
            var decoder = OriginalPGSDecoder(), events: [PGSDisplayEvent] = []
            for frame in frames where frame.track == number {
                let decoded = try decoder.consumeMatroska(packet: Data(bytes.data[frame.bytes]), timestamp: frame.pts + shift,
                    duration: frame.duration > 0 ? frame.duration : nil)
                for event in decoded { for bitmap in event.bitmaps { bitmapBytes += bitmap.rgba.count } }
                guard bitmapBytes <= OriginalPGSDecoder.maximumRenderedBytes else { throw MatroskaRemuxError.unsupported("combined bitmap subtitle memory exceeds 256 MiB") }
                events += decoded
            }
            try decoder.finish()
            let language = track.languageIETF ?? track.language
            let languageName = Locale.current.localizedString(forLanguageCode: language) ?? language
            bitmapSubtitles.append(PGSSubtitleTrack(id: Int32(3_000_000 + bitmapSubtitles.count),
                name: "\(languageName) · \(track.name.isEmpty ? "PGS subtitles" : track.name)", language: language,
                events: events, isDefault: track.isDefault, isForced: track.isForced))
        }
        for number in trackOrder where tracks[number]!.isEnabled && tracks[number]!.type == 17 && tracks[number]!.codec != "S_HDMV/PGS" {
            let track = tracks[number]!
            var cues: [MatroskaTextCue] = []
            for frame in frames where frame.track == number {
                try Task.checkCancellation()
                let payload = Data(bytes.data[frame.bytes])
                if let cue = try MatroskaTextDecoder.decode(codecID: track.codec, packet: payload,
                    start: frame.pts + shift, duration: frame.duration) { cues.append(cue) }
                guard cues.count <= MatroskaTextDecoder.maximumCuesPerTrack else { throw MatroskaRemuxError.unsupported("too many subtitle cues") }
            }
            let language = track.languageIETF ?? track.language
            let languageName = Locale.current.localizedString(forLanguageCode: language) ?? language
            let title = track.name.isEmpty ? "Subtitles" : track.name
            subtitles.append(MatroskaTextTrack(id: Int32(2_000_000 + subtitles.count),
                name: "\(languageName) · \(title)", language: language,
                cues: cues.sorted { $0.start < $1.start }, isDefault: track.isDefault && track.isEnabled,
                isForced: track.isForced && track.isEnabled,
                renderingNote: ["S_TEXT/ASS", "S_TEXT/SSA"].contains(track.codec) ? "ASS/SSA text rendering; advanced styling and positioned signs are not yet supported." : nil))
        }
        let codecs = mediaOrder.map { tracks[$0]!.type == 1 ? (tracks[$0]!.codec == "V_MPEGH/ISO/HEVC" ? "HEVC" : "H.264/AVC") : tracks[$0]!.audioProfile }
        let summary = codecs.reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }.joined(separator: " + ")
        try Task.checkCancellation()
        completed = true
        return MatroskaRemuxResult(codecSummary: "Luma Matroska demux/remux · \(summary) · system decoding", duration: duration, subtitles: subtitles, bitmapSubtitles: bitmapSubtitles)
    }

    private func sampleBuffer(_ frame: MKVFrame, shift: Double) throws -> CMSampleBuffer {
        var block: CMBlockBuffer?
        var status = CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: frame.bytes.count,
            blockAllocator: nil, customBlockSource: nil, offsetToData: 0, dataLength: frame.bytes.count, flags: 0, blockBufferOut: &block)
        guard status == noErr, let block else { throw MatroskaRemuxError.writing("could not allocate compressed sample (\(status))") }
        status = bytes.data.withUnsafeBytes { source in
            CMBlockBufferReplaceDataBytes(with: source.baseAddress!.advanced(by: frame.bytes.lowerBound), blockBuffer: block,
                                          offsetIntoDestination: 0, dataLength: frame.bytes.count)
        }
        guard status == noErr else { throw MatroskaRemuxError.writing("could not copy compressed sample (\(status))") }
        let pts = CMTime(seconds: frame.pts + shift, preferredTimescale: 1_000_000_000)
        var timing = CMSampleTimingInfo(duration: CMTime(seconds: frame.duration, preferredTimescale: 1_000_000_000),
                                       presentationTimeStamp: pts,
                                       decodeTimeStamp: CMTime(seconds: (frame.decodeTime ?? frame.pts) + shift, preferredTimescale: 1_000_000_000))
        var size = frame.bytes.count
        var sample: CMSampleBuffer?
        if let audio = tracks[frame.track]!.audioFormat, audio.isPCM {
            let count = frame.audioSampleFrames!
            var sampleSize = Int(audio.asbd.mBytesPerFrame)
            var pcmTiming = CMSampleTimingInfo(duration: CMTime(seconds: 1 / audio.asbd.mSampleRate, preferredTimescale: 1_000_000_000), presentationTimeStamp: pts, decodeTimeStamp: pts)
            status = CMSampleBufferCreateReady(allocator: nil, dataBuffer: block, formatDescription: audio.formatDescription,
                sampleCount: count, sampleTimingEntryCount: 1, sampleTimingArray: &pcmTiming,
                sampleSizeEntryCount: 1, sampleSizeArray: &sampleSize, sampleBufferOut: &sample)
        } else if tracks[frame.track]!.type == 2 {
            var packet = AudioStreamPacketDescription(mStartOffset: 0, mVariableFramesInPacket: UInt32(frame.audioSampleFrames ?? tracks[frame.track]!.audioPacketFrames), mDataByteSize: UInt32(size))
            status = CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator: nil, dataBuffer: block,
                formatDescription: tracks[frame.track]!.format!, sampleCount: 1, presentationTimeStamp: pts,
                packetDescriptions: &packet, sampleBufferOut: &sample)
        } else {
            status = CMSampleBufferCreateReady(allocator: nil, dataBuffer: block, formatDescription: tracks[frame.track]!.format,
                sampleCount: 1, sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                sampleSizeEntryCount: 1, sampleSizeArray: &size, sampleBufferOut: &sample)
        }
        guard status == noErr, let sample else { throw MatroskaRemuxError.writing("could not create sample (\(status))") }
        if frame.trimStart > 0 {
            CMSetAttachment(sample, key: kCMSampleBufferAttachmentKey_TrimDurationAtStart,
                value: CMTimeCopyAsDictionary(CMTime(seconds: frame.trimStart, preferredTimescale: 48_000), allocator: nil)!, attachmentMode: kCMAttachmentMode_ShouldPropagate)
        }
        if frame.trimEnd > 0 {
            CMSetAttachment(sample, key: kCMSampleBufferAttachmentKey_TrimDurationAtEnd,
                value: CMTimeCopyAsDictionary(CMTime(seconds: frame.trimEnd, preferredTimescale: 48_000), allocator: nil)!, attachmentMode: kCMAttachmentMode_ShouldPropagate)
        }
        if tracks[frame.track]!.type == 1, let array = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true) {
            let dictionary = unsafeBitCast(CFArrayGetValueAtIndex(array, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dictionary, Unmanaged.passUnretained(kCMSampleAttachmentKey_NotSync).toOpaque(),
                                 Unmanaged.passUnretained(frame.keyframe ? kCFBooleanFalse : kCFBooleanTrue).toOpaque())
        }
        return sample
    }
}
