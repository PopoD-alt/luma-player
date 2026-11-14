import Foundation
import CoreGraphics

@main struct MSVideo1Smoke {
    static func main() async throws {
        let directory = URL(fileURLWithPath: CommandLine.arguments[1])
        let manifest = try JSONDecoder().decode(Manifest.self, from: Data(contentsOf: directory.appendingPathComponent("manifest.json")))
        let palette = manifest.palette.map { (UInt8($0[0]), UInt8($0[1]), UInt8($0[2])) }
        var cases = 0
        for stream in manifest.streams {
            var previous: MSVideo1Frame?
            for entry in stream.frames {
                let packet = try Data(contentsOf: directory.appendingPathComponent(entry.packet))
                let expected = try Data(contentsOf: directory.appendingPathComponent(entry.rgba))
                let frame = try OriginalMSVideo1Decoder.decode(width: manifest.width, height: manifest.height,
                                                              bitDepth: stream.bitDepth, data: packet,
                                                              palette: palette, previousFrame: previous)
                precondition(frame.rgba == expected, "Exact RGBA golden differs: \(entry.packet)")
                precondition(frame.isKeyframe == entry.keyframe)
                let image = try frame.image()
                precondition(image.width == manifest.width && image.height == manifest.height)
                precondition((image.dataProvider!.data! as Data) == expected)
                if !entry.keyframe {
                    rejects("delta without predecessor") {
                        _ = try OriginalMSVideo1Decoder.decode(width: manifest.width, height: manifest.height,
                                                               bitDepth: stream.bitDepth, data: packet, palette: palette)
                    }
                } else {
                    let independent = try OriginalMSVideo1Decoder.decode(width: manifest.width, height: manifest.height,
                                                                         bitDepth: stream.bitDepth, data: packet, palette: palette)
                    precondition(independent.rgba == expected)
                }
                previous = frame
                cases += 1
            }
        }

        // Literal vectors have hand-written pixel oracles, independent of the
        // fixture generator: low flag bit is the bottom-left pixel, set -> A.
        let red: [UInt8] = [255, 0, 0, 255], blue: [UInt8] = [0, 0, 255, 255]
        let two = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16,
                                                     data: Data([1, 0, 0, 0x7c, 0x1f, 0]))
        precondition(two.rgba == Data(Array(repeating: blue, count: 12).flatMap { $0 } + red + Array(repeating: blue, count: 3).flatMap { $0 }))
        let solid = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16, data: Data([0, 0xfc, 0, 0]))
        precondition(solid.rgba == Data(Array(repeating: red, count: 16).flatMap { $0 }))
        let repeated = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16,
                                                          data: Data([1, 0x84]), previousFrame: two)
        precondition(repeated.rgba == two.rgba && !repeated.isKeyframe && two.rgba != solid.rgba)
        // A copied delta must not mutate a retained seek checkpoint.
        let painted = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16,
                                                         data: Data([0, 0xfc]), previousFrame: two)
        precondition(painted.rgba == solid.rgba && two.rgba == repeated.rgba && painted.isKeyframe)
        let indexed = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 8,
                                                         data: Data([0, 0x80]), palette: [(1, 2, 3)])
        let recolored = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 8,
                                                          data: Data([1, 0x84]), palette: [(4, 5, 6)], previousFrame: indexed)
        precondition(Array(recolored.rgba.prefix(4)) == [4, 5, 6, 255], "Delta indices must use the current palette")

        // Multi-byte skip lengths, including a skip crossing a block-row boundary.
        let many = try OriginalMSVideo1Decoder.decode(width: 128, height: 128, bitDepth: 16,
                                                      data: Data(Array(repeating: [UInt8(0), 0xfc], count: 1024).flatMap { $0 }))
        let skip1023 = try OriginalMSVideo1Decoder.decode(width: 128, height: 128, bitDepth: 16,
                                                         data: Data([255, 0x87, 0x1f, 0x80]), previousFrame: many)
        precondition(Array(skip1023.rgba.prefix(124 * 4)) == Array(many.rgba.prefix(124 * 4)))
        precondition(Array(skip1023.rgba[124 * 4..<128 * 4]) == Array(repeating: blue, count: 4).flatMap { $0 })
        let noOp = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16, data: Data([0, 0x84, 0, 0xfc]))
        precondition(noOp.rgba == solid.rgba && noOp.isKeyframe)

        for packet: [UInt8] in [[], [0], [1, 0], [1, 0, 0, 0], [0, 0, 0, 0x80], [0, 0xfc, 1], [0, 0xfc, 0, 1], [0, 0xfc, 0, 0, 0, 0]] {
            rejects("truncated block or invalid trailer \(packet)") { _ = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16, data: Data(packet)) }
        }
        rejects("skip beyond grid") { _ = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16, data: Data([2, 0x84]), previousFrame: two) }
        rejects("wrong previous dimensions") { _ = try OriginalMSVideo1Decoder.decode(width: 8, height: 4, bitDepth: 16, data: Data([2, 0x84]), previousFrame: two) }
        rejects("wrong previous depth") { _ = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 8, data: Data([1, 0x84]), palette: palette, previousFrame: two) }
        rejects("unsupported depth") { _ = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 24, data: Data()) }
        rejects("partial edge blocks") { _ = try OriginalMSVideo1Decoder.decode(width: 5, height: 4, bitDepth: 16, data: Data()) }
        rejects("empty palette") { _ = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 8, data: Data([0, 0x80])) }
        rejects("palette overflow") { _ = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 8, data: Data([1, 0x80]), palette: [(0, 0, 0)]) }
        rejects("dimension capacity") { _ = try OriginalMSVideo1Decoder.decode(width: Int.max, height: Int.max, bitDepth: 16, data: Data()) }
        rejects("negative dimensions") { _ = try OriginalMSVideo1Decoder.decode(width: -4, height: 4, bitDepth: 16, data: Data()) }
        rejects("pixel capacity") { _ = try OriginalMSVideo1Decoder.decode(width: 8192, height: 8192, bitDepth: 16, data: Data()) }

        // Every strict prefix of one complete eight-color packet must fail.
        let eight = Data([0x55, 0x55, 0, 0xfc, 0x1f, 0, 0xe0, 3, 0, 0, 0xff, 0x7f, 0, 0, 0x1f, 0x7c, 0xe0, 0x7f])
        _ = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16, data: eight)
        for end in 0..<eight.count { rejects("eight-color prefix \(end)") { _ = try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16, data: Data(eight.prefix(end))) } }
        let cancelled = Task.detached {
            withUnsafeCurrentTask { $0?.cancel() }
            return try OriginalMSVideo1Decoder.decode(width: 4, height: 4, bitDepth: 16, data: Data([0, 0xfc]))
        }
        do { _ = try await cancelled.value; fatalError("Cancelled decode succeeded") } catch is CancellationError {}
        print("PASS: \(cases) exact 8/16-bit Video 1 goldens, solid/two/eight-color modes, row orientation, delta/seek checkpoints, palette changes, malformed bounds, cancellation")
    }

    static func rejects(_ label: String, _ action: () throws -> Void) {
        do { try action() } catch { return }
        fatalError("Accepted invalid input: \(label)")
    }
    struct Manifest: Decodable { let width: Int; let height: Int; let palette: [[Int]]; let streams: [Stream] }
    struct Stream: Decodable { let bitDepth: Int; let frames: [Frame] }
    struct Frame: Decodable { let packet: String; let rgba: String; let keyframe: Bool }
}
