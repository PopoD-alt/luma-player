import Foundation
import CoreGraphics

/// A decoded Microsoft Video 1 frame. Pixels are retained in the codec's native
/// palette-index/RGB555 representation so delta frames remain independent of
/// later palette changes. `rgba` is top-down, tightly packed, straight RGBA8.
struct MSVideo1Frame {
    let width: Int
    let height: Int
    let bitDepth: Int
    let rgba: Data
    let isKeyframe: Bool
    fileprivate let pixels: [UInt16]

    func image() throws -> CGImage {
        try Task.checkCancellation()
        guard let provider = CGDataProvider(data: rgba as CFData),
              let image = CGImage(width: width, height: height, bitsPerComponent: 8,
                                  bitsPerPixel: 32, bytesPerRow: width * 4,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue),
                                  provider: provider, decode: nil, shouldInterpolate: true,
                                  intent: .defaultIntent) else {
            throw MSVideo1Error.malformed("Could not create the decoded frame image.")
        }
        return image
    }
}

enum MSVideo1Error: Error, LocalizedError {
    case malformed(String)
    case unsupported(String)
    case capacity

    var errorDescription: String? {
        switch self {
        case .malformed(let reason): return "Invalid Microsoft Video 1 frame: \(reason)"
        case .unsupported(let reason): return "Microsoft Video 1: \(reason)"
        case .capacity: return "The Microsoft Video 1 frame exceeds the decoder's size limit."
        }
    }
}

/// Original Video 1 block parser; no external decoder or compressed-codec API.
/// Syntax was implemented from the original public format investigation:
/// https://multimedia.cx/video1.txt (Mike Melanson, 2001–2003).
/// Windows RGB555 component masks follow Microsoft's pixel-format definition:
/// https://learn.microsoft.com/windows/win32/directshow/working-with-16-bit-rgb
/// The old investigation's written R/B bit labels disagree with Windows RGB555;
/// this decoder uses red in bits 10–14 and blue in bits 0–4.
enum OriginalMSVideo1Decoder {
    static func decode(width: Int, height: Int, bitDepth: Int, data: Data,
                       palette: [(UInt8, UInt8, UInt8)] = [],
                       previousFrame: MSVideo1Frame? = nil) throws -> MSVideo1Frame {
        try Task.checkCancellation()
        guard width > 0, height > 0 else { throw MSVideo1Error.malformed("Nonpositive dimensions.") }
        guard width <= 8192, height <= 8192, width * height <= 16_777_216,
              data.count <= 33_554_432 else { throw MSVideo1Error.capacity }
        guard width % 4 == 0, height % 4 == 0 else {
            throw MSVideo1Error.unsupported("Dimensions must be multiples of four.")
        }
        guard bitDepth == 8 || bitDepth == 16 else {
            throw MSVideo1Error.unsupported("Only 8-bit indexed and 16-bit RGB555 frames are implemented.")
        }
        if bitDepth == 8 {
            guard !palette.isEmpty, palette.count <= 256 else {
                throw MSVideo1Error.malformed("An indexed frame requires a palette of 1–256 RGB entries.")
            }
        }
        if let previousFrame {
            guard previousFrame.width == width, previousFrame.height == height,
                  previousFrame.bitDepth == bitDepth,
                  previousFrame.pixels.count == width * height else {
                throw MSVideo1Error.malformed("The previous frame has a different format.")
            }
        }

        // Copying the native plane preserves value semantics when callers keep
        // checkpoints. An unchanged delta can share its predecessor's storage.
        var pixels = previousFrame?.pixels ?? [UInt16](repeating: 0, count: width * height)
        let bytes = [UInt8](data)
        var cursor = 0, block = 0, operations = 0, dependsOnPrevious = false
        let blocksWide = width / 4, blockCount = blocksWide * (height / 4)
        func readByte() throws -> UInt8 {
            guard cursor < bytes.count else { throw MSVideo1Error.malformed("Truncated block data.") }
            defer { cursor += 1 }
            return bytes[cursor]
        }
        func readColor() throws -> UInt16 {
            let low = try readByte()
            if bitDepth == 8 { return UInt16(low) }
            return UInt16(low) | (UInt16(try readByte()) << 8)
        }
        func paint(_ colors: [UInt16], flags: UInt16?, quadrants: Bool) {
            let blockX = (block % blocksWide) * 4, blockY = (block / blocksWide) * 4
            for row in 0..<4 {
                let destination = (height - 1 - blockY - row) * width + blockX
                for column in 0..<4 {
                    let pair = quadrants ? ((row / 2) * 2 + column / 2) * 2 : 0
                    let choice = flags.map { ($0 & (UInt16(1) << (row * 4 + column))) != 0 ? 0 : 1 } ?? 0
                    pixels[destination + column] = colors[pair + choice] & 0x7fff
                }
            }
        }

        while block < blockCount {
            if operations & 255 == 0 { try Task.checkCancellation() }
            operations += 1
            let low = try readByte(), high = try readByte()
            if high >= 0x84 && high <= 0x87 {
                let count = (Int(high) - 0x84) * 256 + Int(low)
                guard count <= blockCount - block else { throw MSVideo1Error.malformed("A skip exceeds the frame's block grid.") }
                if count > 0 {
                    guard previousFrame != nil else {
                        throw MSVideo1Error.unsupported("A delta frame requires its decoded predecessor.")
                    }
                    dependsOnPrevious = true
                    block += count
                }
                // A zero-length skip is a documented no-op. It still consumes
                // input, and both input length and cancellation checks are bounded.
                continue
            }
            if high < 0x80 {
                let first = try readColor(), second = try readColor()
                var colors = [first, second]
                let quadrants = bitDepth == 16 && first & 0x8000 != 0
                if quadrants { for _ in 0..<6 { colors.append(try readColor()) } }
                paint(colors, flags: UInt16(low) | (UInt16(high) << 8), quadrants: quadrants)
            } else if bitDepth == 8 && high >= 0x90 {
                var colors: [UInt16] = []
                for _ in 0..<8 { colors.append(try readColor()) }
                paint(colors, flags: UInt16(low) | (UInt16(high) << 8), quadrants: true)
            } else {
                let color = bitDepth == 8 ? UInt16(low) : UInt16(low) | (UInt16(high) << 8)
                paint([color], flags: nil, quadrants: false)
            }
            block += 1
        }
        // AVI chunk padding is outside the packet. An optional end-of-frame word
        // is allowed only after the entire grid; arbitrary trailers are rejected.
        guard cursor == bytes.count || (bytes.count - cursor == 2 && bytes[cursor] == 0 && bytes[cursor + 1] == 0) else {
            throw MSVideo1Error.malformed("Unexpected data after the final block.")
        }
        try Task.checkCancellation()
        var rgba = Data(count: width * height * 4)
        try rgba.withUnsafeMutableBytes { storage in
            let output = storage.bindMemory(to: UInt8.self)
            for index in pixels.indices {
                if index & 0xffff == 0 { try Task.checkCancellation() }
                let color = pixels[index], offset = index * 4
                if bitDepth == 8 {
                    guard Int(color) < palette.count else { throw MSVideo1Error.malformed("Palette index is outside the supplied palette.") }
                    let entry = palette[Int(color)]
                    output[offset] = entry.0; output[offset + 1] = entry.1; output[offset + 2] = entry.2
                } else {
                    func expand(_ value: UInt16) -> UInt8 { UInt8((value << 3) | (value >> 2)) }
                    output[offset] = expand((color >> 10) & 31)
                    output[offset + 1] = expand((color >> 5) & 31)
                    output[offset + 2] = expand(color & 31)
                }
                output[offset + 3] = 255
            }
        }
        try Task.checkCancellation()
        return MSVideo1Frame(width: width, height: height, bitDepth: bitDepth,
                             rgba: rgba, isKeyframe: !dependsOnPrevious, pixels: pixels)
    }
}
