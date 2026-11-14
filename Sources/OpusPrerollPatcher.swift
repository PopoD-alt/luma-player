import Foundation

enum OpusPrerollPatcherError: Error, LocalizedError {
    case invalid(String)
    var errorDescription: String? {
        switch self { case .invalid(let detail): return "Could not preserve Opus seek preroll: \(detail)" }
    }
}

/// Adds public ISO BMFF roll sample groups to an owned, newly generated MOV.
/// Media bytes and chunk offsets remain unchanged: only a final moov is rebuilt.
/// This is deliberately not a general-purpose editor for arbitrary input movies.
/// Opus mapping: https://opus-codec.org/docs/opus_in_isobmff.html §4.3.6.
enum OpusPrerollPatcher {
    private static let maximumMoovBytes = 128 * 1024 * 1024
    private struct Box {
        let type: UInt32
        let range: Range<Int>
        let payload: Range<Int>
        let extended: Bool
    }
    private struct TopBox {
        let type: UInt32
        let offset: UInt64
        let size: UInt64
        let header: Int
    }

    /// Returns the number of Opus tracks patched. Repeated application preserves
    /// stronger existing roll distances and produces identical metadata bytes.
    static func apply(to url: URL, preRollSeconds: Double = 0.16) throws -> Int {
        guard url.isFileURL, preRollSeconds.isFinite, preRollSeconds >= 0.08 else {
            throw OpusPrerollPatcherError.invalid("a local generated MOV and at least 80 ms of preroll are required")
        }
        let handle = try FileHandle(forUpdating: url)
        defer { try? handle.close() }
        let fileSize = try handle.seekToEnd()
        var cursor: UInt64 = 0, moov: TopBox?, hasEarlierMedia = false
        while cursor < fileSize {
            try handle.seek(toOffset: cursor)
            let header = try read(handle, count: 8)
            let shortSize = try u32(header, 0), type = try u32(header, 4)
            let size: UInt64, headerBytes: Int
            if shortSize == 1 {
                size = try u64(read(handle, count: 8), 0); headerBytes = 16
            } else { size = shortSize == 0 ? fileSize - cursor : UInt64(shortSize); headerBytes = 8 }
            guard size >= UInt64(headerBytes), size <= fileSize - cursor else {
                throw OpusPrerollPatcherError.invalid("invalid top-level atom size")
            }
            if type == fourCC("moov") {
                guard moov == nil, cursor + size == fileSize, hasEarlierMedia,
                      size <= UInt64(maximumMoovBytes) else {
                    throw OpusPrerollPatcherError.invalid("requires one final moov no larger than 128 MiB, after mdat")
                }
                moov = TopBox(type: type, offset: cursor, size: size, header: headerBytes)
            } else if type == fourCC("mdat") {
                guard moov == nil else { throw OpusPrerollPatcherError.invalid("mdat follows moov") }
                hasEarlierMedia = true
            }
            cursor += size
        }
        guard let moov else { throw OpusPrerollPatcherError.invalid("missing final moov") }
        try handle.seek(toOffset: moov.offset)
        let data = try read(handle, count: Int(moov.size))
        let root = Box(type: moov.type, range: 0..<data.count, payload: moov.header..<data.count, extended: moov.header == 16)
        try validateChunkOffsets(data, container: root, before: moov.offset)
        var replacements = [Int: Data](), patched = 0
        for track in try boxes(data, within: root.payload) where track.type == fourCC("trak") {
            if let replacement = try patchTrack(data, track: track, preRollSeconds: preRollSeconds) {
                replacements[track.range.lowerBound] = replacement; patched += 1
            }
        }
        guard patched > 0 else { return 0 }
        let rebuilt = try rebuild(data, container: root, replacements: replacements)
        guard rebuilt.count <= maximumMoovBytes else { throw OpusPrerollPatcherError.invalid("updated moov exceeds 128 MiB") }
        // Complete all structure checks before the first write. The caller owns
        // failure cleanup for this newly generated output, including I/O errors.
        if rebuilt != data {
            try handle.seek(toOffset: moov.offset)
            try handle.write(contentsOf: rebuilt)
            try handle.truncate(atOffset: moov.offset + UInt64(rebuilt.count))
        }
        return patched
    }

    private static func patchTrack(_ data: Data, track: Box, preRollSeconds: Double) throws -> Data? {
        let mdia = try child(data, in: track, type: "mdia")
        let handler = try child(data, in: mdia, type: "hdlr")
        guard try u32(data, handler.payload.lowerBound + 8) == fourCC("soun") else { return nil }
        let minf = try child(data, in: mdia, type: "minf")
        let stbl = try child(data, in: minf, type: "stbl")
        let entries = try boxes(data, within: stbl.payload)
        let stsd = try unique(entries, type: "stsd")
        guard stsd.payload.count >= 8 else { throw OpusPrerollPatcherError.invalid("short stsd") }
        let entryCount = try u32(data, stsd.payload.lowerBound + 4)
        let descriptions = try boxes(data, within: stsd.payload.lowerBound + 8..<stsd.payload.upperBound)
        guard descriptions.count == Int(entryCount) else { throw OpusPrerollPatcherError.invalid("stsd entry count mismatch") }
        let opus = descriptions.filter { $0.type == fourCC("opus") || $0.type == fourCC("Opus") }
        guard !opus.isEmpty else { return nil }
        guard opus.count == descriptions.count else { throw OpusPrerollPatcherError.invalid("mixed Opus/non-Opus sample descriptions") }

        let mdhd = try child(data, in: mdia, type: "mdhd")
        guard mdhd.payload.count >= 4 else { throw OpusPrerollPatcherError.invalid("short mdhd") }
        let version = data[mdhd.payload.lowerBound]
        guard version <= 1 else { throw OpusPrerollPatcherError.invalid("unsupported mdhd version") }
        let rate = try u32(data, mdhd.payload.lowerBound + (version == 1 ? 20 : 12))
        guard rate > 0 else { throw OpusPrerollPatcherError.invalid("zero media timescale") }
        let stsz = try unique(entries, type: "stsz")
        guard stsz.payload.count >= 12 else { throw OpusPrerollPatcherError.invalid("short stsz") }
        let sampleCount = try u32(data, stsz.payload.lowerBound + 8)
        guard sampleCount > 0 else { throw OpusPrerollPatcherError.invalid("empty Opus track") }
        let stts = try unique(entries, type: "stts")
        guard stts.payload.count >= 8, data[stts.payload.lowerBound] == 0 else { throw OpusPrerollPatcherError.invalid("unsupported stts") }
        let timeEntryCount = Int(try u32(data, stts.payload.lowerBound + 4))
        guard timeEntryCount > 0, timeEntryCount <= (stts.payload.count - 8) / 8,
              8 + timeEntryCount * 8 == stts.payload.count else { throw OpusPrerollPatcherError.invalid("stts entry sizing") }
        var minimumDelta = UInt32.max, timedSamples: UInt64 = 0
        for index in 0..<timeEntryCount {
            let offset = stts.payload.lowerBound + 8 + index * 8
            let count = try u32(data, offset), delta = try u32(data, offset + 4)
            guard count > 0, delta > 0 else { throw OpusPrerollPatcherError.invalid("zero Opus sample count/duration") }
            timedSamples += UInt64(count); minimumDelta = min(minimumDelta, delta)
        }
        guard timedSamples == UInt64(sampleCount) else { throw OpusPrerollPatcherError.invalid("stts/stsz sample counts differ") }
        let needed = ceil(preRollSeconds * Double(rate) / Double(minimumDelta))
        guard needed >= 1, needed <= 32768 else { throw OpusPrerollPatcherError.invalid("roll distance cannot fit signed 16-bit ISO field") }
        var distance = Int(needed)
        var kept = Data()
        for entry in entries {
            let rollGroup = try [fourCC("sgpd"), fourCC("sbgp")].contains(entry.type)
                && entry.payload.count >= 8 && u32(data, entry.payload.lowerBound + 4) == fourCC("roll")
            if rollGroup {
                // Recognize our exact simple form for safe idempotent updates.
                // Unrelated grouping types are retained byte for byte below.
                if entry.type == fourCC("sgpd") {
                    guard entry.payload.count == 18, try u32(data, entry.payload.lowerBound) == 0x01000000,
                          try u32(data, entry.payload.lowerBound + 8) == 2,
                          try u32(data, entry.payload.lowerBound + 12) == 1 else {
                        throw OpusPrerollPatcherError.invalid("existing roll descriptions need a richer merge")
                    }
                    let start = entry.payload.lowerBound + 16
                    let signed = Int16(bitPattern: UInt16(data[start]) << 8 | UInt16(data[start + 1]))
                    guard signed < 0 else { throw OpusPrerollPatcherError.invalid("unexpected existing roll direction") }
                    distance = max(distance, -Int(signed))
                } else {
                    guard entry.payload.count == 20, try u32(data, entry.payload.lowerBound) == 0,
                          try u32(data, entry.payload.lowerBound + 8) == 1,
                          try u32(data, entry.payload.lowerBound + 12) == sampleCount,
                          try u32(data, entry.payload.lowerBound + 16) == 1 else {
                        throw OpusPrerollPatcherError.invalid("existing roll sample mapping needs a richer merge")
                    }
                }
            } else { kept.append(data[entry.range]) }
        }
        let signedDistance = UInt16(bitPattern: Int16(-distance))
        let sgpd = makeBox(type: fourCC("sgpd"), payload: big(0x01000000, 4) + Data("roll".utf8)
            + big(2, 4) + big(1, 4) + big(UInt64(signedDistance), 2))
        let sbgp = makeBox(type: fourCC("sbgp"), payload: big(0, 4) + Data("roll".utf8)
            + big(1, 4) + big(UInt64(sampleCount), 4) + big(1, 4))
        let updatedStbl = makeBox(type: stbl.type, payload: kept + sgpd + sbgp, extended: stbl.extended)
        let updatedMinf = try rebuild(data, container: minf, replacements: [stbl.range.lowerBound: updatedStbl])
        let updatedMdia = try rebuild(data, container: mdia, replacements: [minf.range.lowerBound: updatedMinf])
        return try rebuild(data, container: track, replacements: [mdia.range.lowerBound: updatedMdia])
    }

    private static func validateChunkOffsets(_ data: Data, container: Box, before moovOffset: UInt64) throws {
        for box in try boxes(data, within: container.payload) {
            if [fourCC("trak"), fourCC("mdia"), fourCC("minf"), fourCC("stbl")].contains(box.type) {
                try validateChunkOffsets(data, container: box, before: moovOffset)
            } else if box.type == fourCC("stco") || box.type == fourCC("co64") {
                let width = box.type == fourCC("stco") ? 4 : 8
                guard box.payload.count >= 8 else { throw OpusPrerollPatcherError.invalid("short chunk-offset table") }
                let count = Int(try u32(data, box.payload.lowerBound + 4))
                guard count <= (box.payload.count - 8) / width, 8 + count * width == box.payload.count else {
                    throw OpusPrerollPatcherError.invalid("chunk-offset table sizing")
                }
                for index in 0..<count {
                    let offset = box.payload.lowerBound + 8 + index * width
                    let value = width == 4 ? UInt64(try u32(data, offset)) : try u64(data, offset)
                    guard value < moovOffset else { throw OpusPrerollPatcherError.invalid("a chunk does not precede final moov") }
                }
            }
        }
    }

    private static func child(_ data: Data, in container: Box, type: String) throws -> Box {
        try unique(boxes(data, within: container.payload), type: type)
    }
    private static func unique(_ boxes: [Box], type: String) throws -> Box {
        let matching = boxes.filter { $0.type == fourCC(type) }
        guard matching.count == 1 else { throw OpusPrerollPatcherError.invalid("requires exactly one \(type)") }
        return matching[0]
    }
    private static func rebuild(_ data: Data, container: Box, replacements: [Int: Data]) throws -> Data {
        var payload = Data()
        for box in try boxes(data, within: container.payload) { payload.append(replacements[box.range.lowerBound] ?? Data(data[box.range])) }
        return makeBox(type: container.type, payload: payload, extended: container.extended)
    }
    private static func boxes(_ data: Data, within range: Range<Int>) throws -> [Box] {
        var cursor = range.lowerBound, result = [Box]()
        while cursor < range.upperBound {
            guard range.upperBound - cursor >= 8 else { throw OpusPrerollPatcherError.invalid("truncated atom header") }
            let shortSize = try u32(data, cursor), type = try u32(data, cursor + 4)
            let header = shortSize == 1 ? 16 : 8
            guard range.upperBound - cursor >= header else { throw OpusPrerollPatcherError.invalid("truncated extended atom header") }
            let size = shortSize == 1 ? try u64(data, cursor + 8) : shortSize == 0 ? UInt64(range.upperBound - cursor) : UInt64(shortSize)
            guard size >= UInt64(header), size <= UInt64(range.upperBound - cursor) else { throw OpusPrerollPatcherError.invalid("atom exceeds its parent") }
            let end = cursor + Int(size)
            result.append(Box(type: type, range: cursor..<end, payload: cursor + header..<end, extended: header == 16))
            cursor = end
        }
        return result
    }
    private static func read(_ file: FileHandle, count: Int) throws -> Data {
        let result = try file.read(upToCount: count) ?? Data()
        guard result.count == count else { throw OpusPrerollPatcherError.invalid("truncated generated MOV") }
        return result
    }
    private static func u32(_ data: Data, _ offset: Int) throws -> UInt32 {
        guard offset >= 0, data.count >= 4, offset <= data.count - 4 else { throw OpusPrerollPatcherError.invalid("truncated integer") }
        return data.withUnsafeBytes { UInt32(bigEndian: $0.loadUnaligned(fromByteOffset: offset, as: UInt32.self)) }
    }
    private static func u64(_ data: Data, _ offset: Int) throws -> UInt64 {
        guard offset >= 0, data.count >= 8, offset <= data.count - 8 else { throw OpusPrerollPatcherError.invalid("truncated integer") }
        return data.withUnsafeBytes { UInt64(bigEndian: $0.loadUnaligned(fromByteOffset: offset, as: UInt64.self)) }
    }
    private static func fourCC(_ value: String) -> UInt32 { value.utf8.reduce(0) { $0 << 8 | UInt32($1) } }
    private static func big(_ value: UInt64, _ count: Int) -> Data { Data((0..<count).reversed().map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) }) }
    private static func makeBox(type: UInt32, payload: Data, extended: Bool = false) -> Data {
        if extended { return big(1, 4) + big(UInt64(type), 4) + big(UInt64(payload.count + 16), 8) + payload }
        return big(UInt64(payload.count + 8), 4) + big(UInt64(type), 4) + payload
    }
}
