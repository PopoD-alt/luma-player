import Foundation

/// Straight (unpremultiplied) RGBA8 rows; coordinates start at the canvas's top left.
struct PGSBitmap: Sendable, Equatable {
    let x: Int
    let y: Int
    let width: Int
    let height: Int
    let rgba: Data
    let isForced: Bool
}

/// Replaces the entire preceding PGS composition at `time`. Empty bitmaps clear it.
/// Display until the next event, or `endTime` when supplied, whichever is earlier.
struct PGSDisplayEvent: Sendable, Equatable {
    let time: Double
    let endTime: Double?
    let canvasWidth: Int
    let canvasHeight: Int
    let bitmaps: [PGSBitmap]
    var isClear: Bool { bitmaps.isEmpty }
}

enum PGSFailure: Error, LocalizedError, Equatable {
    case malformed(String), unsupported(String), capacity(String)
    var errorDescription: String? {
        switch self {
        case .malformed(let detail): return "Malformed PGS subtitles: \(detail)"
        case .unsupported(let detail): return "Unsupported PGS subtitles: \(detail)"
        case .capacity(let detail): return "PGS subtitles exceed the supported limit: \(detail)"
        }
    }
}

/// Original, bounded PGS segment/RLE implementation; no third-party decoder code.
/// Public format references (the complete licensed Blu-ray specification was not used):
/// - Matroska's S_HDMV/PGS mapping: https://www.matroska.org/technical/codec_specs.html
/// - Published PCS/PDS/WDS/ODS syntax: https://patents.google.com/patent/US20090185789A1/en
/// - Published bitmap codewords/fragmentation: https://patents.google.com/patent/US7912305B1/en
/// Scope: normal/acquisition/epoch compositions, up to two objects/windows, palette
/// changes, fragmented objects, source cropping, window clipping and clear events.
/// Not a Blu-ray demuxer, PES decoder, stereoscopic/HDR extension decoder, VobSub or
/// DVB decoder. SUP timestamps cannot wrap within one decoder stream. Seek by
/// retaining emitted events; starting at an arbitrary non-acquisition packet fails.
struct OriginalPGSDecoder: Sendable {
    static let maximumPacketBytes = 16_777_216
    static let maximumObjectPixels = 4_194_304
    static let maximumCachedPixels = 8_388_608
    static let maximumRenderedBytes = 268_435_456
    static let maximumEvents = 100_000

    private struct Color: Sendable {
        let y: UInt8, cr: UInt8, cb: UInt8, alpha: UInt8
        func rgba(hd: Bool) -> [UInt8] {
            guard alpha != 0 else { return [0, 0, 0, 0] }
            let luma = 255.0 / 219.0 * (Double(y) - 16)
            let red = Double(cr) - 128, blue = Double(cb) - 128
            func byte(_ value: Double) -> UInt8 { UInt8(min(255, max(0, value.rounded()))) }
            // Limited-range BT.709 for HD; BT.601 for SD graphics planes.
            return [byte(luma + (hd ? 1.792741071 : 1.596026786) * red),
                byte(luma - (hd ? 0.213248614 : 0.391762290) * blue - (hd ? 0.532909329 : 0.812967647) * red),
                byte(luma + (hd ? 2.112401786 : 2.017232143) * blue), alpha]
        }
    }
    private struct Rect: Sendable {
        let x: Int, y: Int, width: Int, height: Int
    }
    private struct Object: Sendable {
        let version: UInt8, width: Int, height: Int
        let indices: Data
    }
    private struct Assembly: Sendable {
        let version: UInt8, width: Int, height: Int, expected: Int
        var bytes: Data
    }
    private struct Reference: Sendable {
        let object: UInt16, window: UInt8, x: Int, y: Int
        let crop: Rect?
        let forced: Bool
    }
    private struct Composition: Sendable {
        let time: Double, endTime: Double?
        let width: Int, height: Int, palette: UInt8
        let references: [Reference]
    }
    private var palettes: [UInt8: [Color?]] = [:]
    private var objects: [UInt16: Object] = [:]
    private var assemblies: [UInt16: Assembly] = [:]
    private var windows: [UInt8: Rect] = [:]
    private var pending: Composition?
    private var previous: Composition?
    private var lastEventTime: Double?
    private var eventCount = 0
    private var renderedBytes = 0
    private var segmentCount = 0

    init() {}
    mutating func reset() { self = Self() }

    /// Matroska supplies the timestamp; packet bytes are type/u16 length/body,
    /// without a SUP "PG" wrapper. A packet may concatenate complete segments.
    /// Segment headers may not be split across calls; ODS fragmentation is supported.
    /// Only a PCS packet's optional BlockDuration establishes its display expiry.
    mutating func consumeMatroska(packet: Data, timestamp: Double, duration: Double? = nil) throws -> [PGSDisplayEvent] {
        try validateTime(timestamp, duration: duration)
        guard packet.count <= Self.maximumPacketBytes else { throw PGSFailure.capacity("packet exceeds 16 MiB") }
        var input = Reader(packet)
        if input.remaining >= 2, input.bytes[0] == 0x50, input.bytes[1] == 0x47 {
            throw PGSFailure.unsupported("a SUP PG wrapper was supplied as a Matroska segment; use consumeSUP")
        }
        guard input.remaining > 0 else { throw PGSFailure.malformed("empty segment packet") }
        var events: [PGSDisplayEvent] = []
        while input.remaining > 0 {
            try Task.checkCancellation()
            let type = try input.byte(), count = try input.u16()
            let body = try input.data(count)
            if let event = try consume(type: type, body: body, time: timestamp, duration: duration) { events.append(event) }
        }
        return events
    }

    /// Complete SUP wrappers only (PG/u32 PTS/u32 DTS/type/u16 length/body).
    /// Call repeatedly with complete wrappers to read a large SUP incrementally.
    mutating func consumeSUP(_ data: Data) throws -> [PGSDisplayEvent] {
        guard data.count <= Self.maximumPacketBytes else { throw PGSFailure.capacity("SUP chunk exceeds 16 MiB") }
        var input = Reader(data), events: [PGSDisplayEvent] = []
        guard input.remaining > 0 else { throw PGSFailure.malformed("empty SUP chunk") }
        while input.remaining > 0 {
            try Task.checkCancellation()
            guard try input.byte() == 0x50, try input.byte() == 0x47 else { throw PGSFailure.malformed("missing SUP PG magic") }
            let pts = try input.u32()
            _ = try input.u32() // Decode time affects buffering, not the emitted display time.
            let type = try input.byte(), count = try input.u16()
            if let event = try consume(type: type, body: input.data(count), time: Double(pts) / 90_000, duration: nil) { events.append(event) }
        }
        return events
    }

    mutating func finish() throws {
        guard pending == nil, assemblies.isEmpty else { throw PGSFailure.malformed("unfinished display set or fragmented object") }
    }

    private func validateTime(_ time: Double, duration: Double?) throws {
        guard time.isFinite, abs(time) < 1_000_000_000 else { throw PGSFailure.malformed("invalid timestamp") }
        if let duration {
            guard duration.isFinite, duration >= 0, (time + duration).isFinite else { throw PGSFailure.malformed("invalid BlockDuration") }
        }
    }

    private mutating func consume(type: UInt8, body: Data, time: Double, duration: Double?) throws -> PGSDisplayEvent? {
        segmentCount += 1
        guard segmentCount <= 1_000_000 else { throw PGSFailure.capacity("too many segments") }
        var input = Reader(body)
        if type == 0x16 { try composition(&input, time: time, duration: duration); return nil }
        guard pending != nil else { throw PGSFailure.malformed("a display set must begin with PCS") }
        switch type {
        case 0x14: try palette(&input)
        case 0x15: try object(&input)
        case 0x17: try window(&input)
        case 0x80:
            guard input.remaining == 0 else { throw PGSFailure.malformed("END must have an empty body") }
            return try display()
        default: throw PGSFailure.unsupported("segment type 0x\(String(type, radix: 16)); extensions are not silently skipped")
        }
        guard input.remaining == 0 else { throw PGSFailure.malformed("trailing segment bytes") }
        return nil
    }

    private mutating func composition(_ input: inout Reader, time: Double, duration: Double?) throws {
        guard pending == nil else { throw PGSFailure.malformed("PCS before the preceding END") }
        try validateTime(time, duration: duration)
        let width = try input.u16(), height = try input.u16()
        guard width > 0, height > 0, width <= 4096, height <= 4096, width * height <= 8_294_400 else { throw PGSFailure.capacity("canvas dimensions") }
        _ = try input.byte(); _ = try input.u16() // Frame-rate indicator and composition number.
        let state = try input.byte(), update = try input.byte(), palette = try input.byte(), count = Int(try input.byte())
        guard [0x00, 0x40, 0x80].contains(state), [0x00, 0x80].contains(update) else { throw PGSFailure.unsupported("composition state or palette-update flags") }
        guard count <= 2 else { throw PGSFailure.capacity("more than two composition objects") }
        if state == 0x80 {
            palettes.removeAll(); objects.removeAll(); assemblies.removeAll(); windows.removeAll(); previous = nil
        } else if let previous, previous.width != width || previous.height != height {
            throw PGSFailure.unsupported("canvas dimensions change without an epoch start")
        }
        var references: [Reference] = []
        for _ in 0..<count {
            let object = UInt16(try input.u16()), window = try input.byte(), flags = try input.byte()
            guard flags & 0x3f == 0 else { throw PGSFailure.unsupported("composition object extension flags") }
            let x = try input.u16(), y = try input.u16()
            var crop: Rect?
            if flags & 0x80 != 0 {
                crop = Rect(x: try input.u16(), y: try input.u16(), width: try input.u16(), height: try input.u16())
                guard crop!.width > 0, crop!.height > 0 else { throw PGSFailure.malformed("empty source crop") }
            }
            references.append(Reference(object: object, window: window, x: x, y: y, crop: crop, forced: flags & 0x40 != 0))
        }
        guard input.remaining == 0 else { throw PGSFailure.malformed("trailing PCS bytes") }
        if update == 0x80, count == 0 {
            guard let previous else { throw PGSFailure.malformed("palette-only update has no prior composition") }
            references = previous.references
        }
        pending = Composition(time: time, endTime: duration.map { time + $0 }, width: width, height: height, palette: palette, references: references)
    }

    private mutating func palette(_ input: inout Reader) throws {
        let id = try input.byte(); _ = try input.byte()
        guard input.remaining % 5 == 0 else { throw PGSFailure.malformed("palette entries are not five bytes") }
        guard palettes[id] != nil || palettes.count < 8 else { throw PGSFailure.capacity("more than eight palettes per epoch") }
        var colors = palettes[id] ?? [Color?](repeating: nil, count: 256)
        if colors[0] == nil { colors[0] = Color(y: 16, cr: 128, cb: 128, alpha: 0) }
        while input.remaining > 0 {
            let index = Int(try input.byte())
            colors[index] = Color(y: try input.byte(), cr: try input.byte(), cb: try input.byte(), alpha: try input.byte())
        }
        palettes[id] = colors
    }

    private mutating func window(_ input: inout Reader) throws {
        let count = Int(try input.byte())
        guard count <= 2 else { throw PGSFailure.capacity("more than two windows") }
        guard let composition = pending else { throw PGSFailure.malformed("WDS without PCS") }
        var newWindows: [UInt8: Rect] = [:]
        for _ in 0..<count {
            let id = try input.byte()
            let rect = Rect(x: try input.u16(), y: try input.u16(), width: try input.u16(), height: try input.u16())
            guard newWindows[id] == nil, rect.width > 0, rect.height > 0,
                  rect.x + rect.width <= composition.width, rect.y + rect.height <= composition.height else { throw PGSFailure.malformed("duplicate or out-of-canvas window") }
            newWindows[id] = rect
        }
        windows = newWindows
    }

    private mutating func object(_ input: inout Reader) throws {
        let id = UInt16(try input.u16()), version = try input.byte(), flags = try input.byte()
        guard flags & 0x3f == 0 else { throw PGSFailure.unsupported("object sequence extension flags") }
        if flags & 0x80 != 0 {
            guard assemblies[id] == nil else { throw PGSFailure.malformed("new first fragment before the old object's last fragment") }
            let declared = try input.u24(), width = try input.u16(), height = try input.u16()
            guard declared >= 4 else { throw PGSFailure.malformed("object_data_length excludes dimensions") }
            guard width > 0, height > 0, width <= 4096, height <= 4096, width * height <= Self.maximumObjectPixels,
                  declared - 4 <= Self.maximumCachedPixels else { throw PGSFailure.capacity("object dimensions or encoded extent") }
            if let old = objects[id], old.width != width || old.height != height { throw PGSFailure.unsupported("object dimensions change within an epoch") }
            guard objects[id] != nil || objects.count + assemblies.count < 64 else { throw PGSFailure.capacity("more than 64 objects per epoch") }
            let queued = assemblies.values.reduce(0) { $0 + $1.expected }
            guard queued <= Self.maximumCachedPixels - (declared - 4) else { throw PGSFailure.capacity("fragment assembly budget") }
            assemblies[id] = Assembly(version: version, width: width, height: height, expected: declared - 4, bytes: Data())
        }
        guard var assembly = assemblies[id], assembly.version == version else { throw PGSFailure.malformed("continuation has no matching object/version") }
        guard input.remaining <= assembly.expected - assembly.bytes.count else { throw PGSFailure.malformed("object exceeds its declared data length") }
        assembly.bytes.append(try input.data(input.remaining))
        if flags & 0x40 != 0 {
            guard assembly.bytes.count == assembly.expected else { throw PGSFailure.malformed("last object fragment is incomplete") }
            let indices = try Self.decodeRLE(assembly.bytes, width: assembly.width, height: assembly.height)
            let cached = objects.reduce(0) { $0 + ($1.key == id ? 0 : $1.value.indices.count) }
            guard cached <= Self.maximumCachedPixels - indices.count else { throw PGSFailure.capacity("decoded object cache") }
            objects[id] = Object(version: version, width: assembly.width, height: assembly.height, indices: indices)
            assemblies.removeValue(forKey: id)
        } else {
            guard assembly.bytes.count < assembly.expected else { throw PGSFailure.malformed("complete object has no last-sequence flag") }
            assemblies[id] = assembly
        }
    }

    private mutating func display() throws -> PGSDisplayEvent {
        guard let composition = pending, assemblies.isEmpty else { throw PGSFailure.malformed("END before all object fragments are complete") }
        if let lastEventTime, composition.time < lastEventTime - 0.000001 { throw PGSFailure.unsupported("decreasing composition timestamps or wrapped SUP PTS") }
        guard eventCount < Self.maximumEvents else { throw PGSFailure.capacity("too many display events") }
        var bitmaps: [PGSBitmap] = []
        if !composition.references.isEmpty {
            guard let colors = palettes[composition.palette] else { throw PGSFailure.malformed("composition references a missing palette") }
            let rgba = colors.map { $0?.rgba(hd: composition.height > 576) }
            for reference in composition.references {
                guard let object = objects[reference.object], let window = windows[reference.window] else { throw PGSFailure.malformed("composition references a missing object or window") }
                let source = reference.crop ?? Rect(x: 0, y: 0, width: object.width, height: object.height)
                guard source.x + source.width <= object.width, source.y + source.height <= object.height else { throw PGSFailure.malformed("source crop exceeds object") }
                let x = max(reference.x, window.x), y = max(reference.y, window.y)
                let right = min(reference.x + source.width, window.x + window.width, composition.width)
                let bottom = min(reference.y + source.height, window.y + window.height, composition.height)
                guard right > x, bottom > y else { continue }
                let width = right - x, height = bottom - y, size = width * height * 4
                guard size <= Self.maximumRenderedBytes - renderedBytes else { throw PGSFailure.capacity("256 MiB rendered-event budget") }
                var pixels = [UInt8](repeating: 0, count: size)
                for row in 0..<height {
                    if row % 32 == 0 { try Task.checkCancellation() }
                    let start = (source.y + y - reference.y + row) * object.width + source.x + x - reference.x
                    for column in 0..<width {
                        guard let color = rgba[Int(object.indices[start + column])] else { throw PGSFailure.malformed("bitmap uses an undefined palette entry") }
                        let target = (row * width + column) * 4
                        pixels[target] = color[0]; pixels[target + 1] = color[1]; pixels[target + 2] = color[2]; pixels[target + 3] = color[3]
                    }
                }
                renderedBytes += size
                bitmaps.append(PGSBitmap(x: x, y: y, width: width, height: height, rgba: Data(pixels), isForced: reference.forced))
            }
        }
        let event = PGSDisplayEvent(time: composition.time, endTime: composition.endTime,
            canvasWidth: composition.width, canvasHeight: composition.height, bitmaps: bitmaps)
        previous = composition; pending = nil; lastEventTime = composition.time; eventCount += 1
        return event
    }

    private static func decodeRLE(_ data: Data, width: Int, height: Int) throws -> Data {
        var input = Reader(data), pixels = [UInt8](repeating: 0, count: width * height), x = 0, y = 0, commands = 0
        while input.remaining > 0 {
            if commands % 1024 == 0 { try Task.checkCancellation() }
            commands += 1
            guard y < height else { throw PGSFailure.malformed("RLE continues beyond the object height") }
            let first = try input.byte()
            var count = 1, color = first
            if first == 0 {
                let control = try input.byte()
                if control == 0 { x = 0; y += 1; continue }
                count = Int(control & 0x3f)
                if control & 0x40 != 0 { count = count * 256 + Int(try input.byte()) }
                color = control & 0x80 != 0 ? try input.byte() : 0
                guard count > 0 else { throw PGSFailure.malformed("zero-length RLE run") }
            }
            guard count <= width - x else { throw PGSFailure.malformed("RLE run crosses a scanline") }
            let offset = y * width + x
            for index in offset..<offset + count { pixels[index] = color }
            x += count
        }
        guard y == height, x == 0 else { throw PGSFailure.malformed("RLE lacks the final end-of-line marker") }
        return Data(pixels)
    }

    private struct Reader {
        let bytes: [UInt8]
        var position = 0
        var remaining: Int { bytes.count - position }
        init(_ data: Data) { bytes = Array(data) }
        mutating func byte() throws -> UInt8 {
            guard remaining >= 1 else { throw PGSFailure.malformed("truncated segment") }
            defer { position += 1 }; return bytes[position]
        }
        mutating func u16() throws -> Int { Int(try byte()) * 256 + Int(try byte()) }
        mutating func u24() throws -> Int { try u16() * 256 + Int(byte()) }
        mutating func u32() throws -> UInt32 { UInt32(try u16()) << 16 | UInt32(try u16()) }
        mutating func data(_ count: Int) throws -> Data {
            guard count >= 0, count <= remaining else { throw PGSFailure.malformed("segment length exceeds packet") }
            defer { position += count }; return Data(bytes[position..<position + count])
        }
    }
}
