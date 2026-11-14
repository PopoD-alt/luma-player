import Foundation
import CoreGraphics

/// Independently written baseline sequential JPEG decoder for MJPEG frames.
/// Implements T.81 Huffman entropy decoding, dequantization and a separable IDCT.
/// Progressive, arithmetic-coded, lossless, CMYK and missing-DHT JPEG are explicit errors.
struct OriginalJPEGDecoder {
    private static let zigzag = [0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63]
    private struct Component {
        let id: Int, h: Int, v: Int, quantizer: Int
        var dcTable = 0, acTable = 0, predictor = 0
        var stride = 0, plane: [UInt8] = []
    }
    private struct Huffman {
        let entries: [Int: Int]
        init(counts: [Int], values: [UInt8]) throws {
            var entries: [Int: Int] = [:], code = 0, index = 0
            for length in 1...16 {
                // T.81 reserves the all-ones code for entropy padding.
                guard code + counts[length - 1] < 1 << length else { throw CodecFailure.malformed("oversubscribed/all-ones JPEG Huffman table") }
                for _ in 0..<counts[length - 1] { entries[(length << 16) | code] = Int(values[index]); index += 1; code += 1 }
                code <<= 1
            }
            self.entries = entries
        }
        func symbol(_ reader: inout Entropy) throws -> Int {
            var code = 0
            for length in 1...16 {
                code = (code << 1) | (try reader.bits(1))
                if let value = entries[(length << 16) | code] { return value }
            }
            throw CodecFailure.malformed("JPEG Huffman code")
        }
    }
    private struct Entropy {
        let bytes: [UInt8]
        var position: Int
        var value = 0, remaining = 0
        mutating func bits(_ count: Int) throws -> Int {
            guard (0...16).contains(count) else { throw CodecFailure.malformed("JPEG bit count") }
            var result = 0
            for _ in 0..<count {
                if remaining == 0 {
                    guard position < bytes.count else { throw CodecFailure.malformed("truncated JPEG entropy") }
                    value = Int(bytes[position]); position += 1
                    if value == 255 {
                        guard position < bytes.count, bytes[position] == 0 else { throw CodecFailure.malformed("unexpected JPEG entropy marker") }
                        position += 1
                    }
                    remaining = 8
                }
                remaining -= 1
                result = (result << 1) | ((value >> remaining) & 1)
            }
            return result
        }
        mutating func marker() throws -> Int {
            if remaining > 0 {
                let mask = (1 << remaining) - 1
                guard value & mask == mask else { throw CodecFailure.malformed("JPEG entropy padding must contain one bits") }
            }
            remaining = 0
            guard position < bytes.count, bytes[position] == 255 else { throw CodecFailure.malformed("missing JPEG marker") }
            while position < bytes.count && bytes[position] == 255 { position += 1 }
            guard position < bytes.count else { throw CodecFailure.malformed("truncated JPEG marker") }
            let marker = Int(bytes[position]); position += 1
            return marker
        }
        mutating func signed(_ count: Int) throws -> Int {
            if count == 0 { return 0 }
            let value = try bits(count)
            return value < (1 << (count - 1)) ? value - ((1 << count) - 1) : value
        }
    }
    private static let basis: [[Double]] = {
        var matrix = [[Double]](repeating: [Double](repeating: 0, count: 8), count: 8)
        for x in 0..<8 { for frequency in 0..<8 {
            let normalization: Double = frequency == 0 ? 1.0 / sqrt(2.0) : 1.0
            let angle = Double((2 * x + 1) * frequency) * Double.pi / 16.0
            matrix[x][frequency] = normalization * cos(angle) / 2.0
        } }
        return matrix
    }()

    static func decode(_ data: Data) throws -> CGImage {
        guard data.count <= 67_108_864 else { throw CodecFailure.capacity }
        let bytes = Array(data)
        guard bytes.count >= 4, bytes[0] == 255, bytes[1] == 216 else { throw CodecFailure.malformed("JPEG SOI") }
        var position = 2, width = 0, height = 0, restartInterval = 0
        var components: [Component] = [], quantizers: [Int: [Int]] = [:], tables: [Int: Huffman] = [:]
        var adobeTransform: Int?
        func integer(_ offset: Int) throws -> Int {
            guard offset >= 0, offset + 1 < bytes.count else { throw CodecFailure.malformed("JPEG integer") }
            return Int(bytes[offset]) << 8 | Int(bytes[offset + 1])
        }
        while position < bytes.count {
            guard bytes[position] == 255 else { throw CodecFailure.malformed("JPEG segment marker") }
            while position < bytes.count && bytes[position] == 255 { position += 1 }
            guard position < bytes.count else { throw CodecFailure.malformed("JPEG marker") }
            let marker = Int(bytes[position]); position += 1
            guard marker != 0xd9, marker != 0xd8, !(0xd0...0xd7).contains(marker) else { throw CodecFailure.malformed("JPEG marker before scan") }
            let length = try integer(position)
            guard length >= 2, length <= bytes.count - position else { throw CodecFailure.malformed("JPEG segment length") }
            let start = position + 2, end = position + length
            position = end
            switch marker {
            case 0xdb:
                var cursor = start
                while cursor < end {
                    let descriptor = Int(bytes[cursor]); cursor += 1
                    guard descriptor >> 4 == 0, descriptor & 15 <= 3, end - cursor >= 64 else { throw CodecFailure.unsupported("JPEG requires baseline 8-bit quantization tables.") }
                    var table = [Int](repeating: 0, count: 64)
                    for index in 0..<64 {
                        let value = Int(bytes[cursor + index])
                        guard value > 0 else { throw CodecFailure.malformed("zero JPEG quantizer") }
                        table[zigzag[index]] = value
                    }
                    quantizers[descriptor & 15] = table; cursor += 64
                }
            case 0xc4:
                var cursor = start
                while cursor < end {
                    guard end - cursor >= 17 else { throw CodecFailure.malformed("JPEG Huffman header") }
                    let descriptor = Int(bytes[cursor]); cursor += 1
                    guard descriptor >> 4 <= 1, descriptor & 15 <= 3 else { throw CodecFailure.malformed("JPEG Huffman class") }
                    let counts = bytes[cursor..<cursor + 16].map(Int.init); cursor += 16
                    let total = counts.reduce(0, +)
                    guard total > 0, total <= 256, total <= end - cursor else { throw CodecFailure.malformed("JPEG Huffman symbols") }
                    tables[descriptor] = try Huffman(counts: counts, values: Array(bytes[cursor..<cursor + total])); cursor += total
                }
            case 0xc0:
                guard components.isEmpty, end - start >= 6, bytes[start] == 8 else { throw CodecFailure.unsupported("Only 8-bit baseline sequential JPEG is implemented.") }
                height = try integer(start + 1); width = try integer(start + 3)
                let count = Int(bytes[start + 5])
                guard [1,3].contains(count), end - start == 6 + count * 3,
                      width > 0, height > 0, width <= 8192, height <= 8192, width * height <= 16_777_216 else { throw CodecFailure.unsupported("JPEG dimensions or color layout are outside this decoder’s supported subset.") }
                for index in 0..<count {
                    let offset = start + 6 + index * 3
                    let id = Int(bytes[offset]), sampling = Int(bytes[offset + 1]), quantizer = Int(bytes[offset + 2])
                    let h = sampling >> 4, v = sampling & 15
                    guard (1...4).contains(h), (1...4).contains(v), quantizer <= 3,
                          !components.contains(where: { $0.id == id }) else { throw CodecFailure.malformed("JPEG component") }
                    components.append(Component(id: id, h: h, v: v, quantizer: quantizer))
                }
                guard count != 1 || (components[0].h == 1 && components[0].v == 1) else { throw CodecFailure.unsupported("Grayscale JPEG currently requires 1×1 sampling.") }
            case 0xdd:
                guard end - start == 2 else { throw CodecFailure.malformed("JPEG restart interval") }
                restartInterval = try integer(start)
            case 0xee:
                if end - start >= 12 && String(bytes: bytes[start..<start + 5], encoding: .ascii) == "Adobe" { adobeTransform = Int(bytes[start + 11]) }
            case 0xda:
                guard !components.isEmpty, end > start else { throw CodecFailure.malformed("JPEG scan before frame") }
                let count = Int(bytes[start])
                guard count == components.count, end - start == 1 + 2 * count + 3,
                      bytes[end - 3] == 0, bytes[end - 2] == 63, bytes[end - 1] == 0 else { throw CodecFailure.unsupported("Non-interleaved/progressive JPEG scans are not implemented.") }
                var scanOrder: [Int] = []
                for index in 0..<count {
                    let id = Int(bytes[start + 1 + index * 2]), selectors = Int(bytes[start + 2 + index * 2])
                    guard let component = components.firstIndex(where: { $0.id == id }), !scanOrder.contains(component),
                          selectors >> 4 <= 3, selectors & 15 <= 3 else { throw CodecFailure.malformed("JPEG scan component") }
                    components[component].dcTable = selectors >> 4; components[component].acTable = selectors & 15
                    scanOrder.append(component)
                }
                let maxH = components.map(\.h).max()!, maxV = components.map(\.v).max()!
                guard components.reduce(0, { $0 + $1.h * $1.v }) <= 10,
                      components.allSatisfy({ maxH % $0.h == 0 && maxV % $0.v == 0 }) else { throw CodecFailure.unsupported("Unsupported JPEG sampling factors.") }
                let columns = (width + maxH * 8 - 1) / (maxH * 8), rows = (height + maxV * 8 - 1) / (maxV * 8)
                for index in components.indices {
                    let stride = columns * components[index].h * 8
                    let planeHeight = rows * components[index].v * 8
                    components[index].stride = stride
                    components[index].plane = [UInt8](repeating: 0, count: stride * planeHeight)
                }
                var reader = Entropy(bytes: bytes, position: end)
                var restartNumber = 0
                for mcu in 0..<columns * rows {
                    if mcu & 127 == 0 { try Task.checkCancellation() }
                    if restartInterval > 0 && mcu > 0 && mcu % restartInterval == 0 {
                        guard try reader.marker() == 0xd0 + restartNumber else { throw CodecFailure.malformed("JPEG restart sequence") }
                        restartNumber = (restartNumber + 1) & 7
                        for index in components.indices { components[index].predictor = 0 }
                    }
                    for index in scanOrder {
                        guard let quantizer = quantizers[components[index].quantizer],
                              let dc = tables[components[index].dcTable], let ac = tables[16 | components[index].acTable] else { throw CodecFailure.unsupported("This JPEG is missing explicit quantization/Huffman tables.") }
                        for by in 0..<components[index].v { for bx in 0..<components[index].h {
                            var coefficients = [Int](repeating: 0, count: 64)
                            let category = try dc.symbol(&reader)
                            guard category <= 11 else { throw CodecFailure.malformed("JPEG DC magnitude") }
                            components[index].predictor += try reader.signed(category)
                            guard abs(components[index].predictor) <= 32767 else { throw CodecFailure.malformed("JPEG DC predictor") }
                            coefficients[0] = components[index].predictor * quantizer[0]
                            var cursor = 1
                            while cursor < 64 {
                                let symbol = try ac.symbol(&reader), run = symbol >> 4, magnitude = symbol & 15
                                if symbol == 0 { break }
                                if symbol == 0xf0 { cursor += 16; guard cursor <= 64 else { throw CodecFailure.malformed("JPEG zero run") }; continue }
                                guard magnitude > 0, magnitude <= 10 else { throw CodecFailure.malformed("JPEG AC magnitude") }
                                cursor += run
                                guard cursor < 64 else { throw CodecFailure.malformed("JPEG AC run") }
                                let natural = zigzag[cursor]
                                coefficients[natural] = try reader.signed(magnitude) * quantizer[natural]
                                cursor += 1
                            }
                            let pixels = idct(coefficients)
                            let x = ((mcu % columns) * components[index].h + bx) * 8
                            let y = ((mcu / columns) * components[index].v + by) * 8
                            let stride = components[index].stride
                            for dy in 0..<8 { for dx in 0..<8 { components[index].plane[(y + dy) * stride + x + dx] = pixels[dy * 8 + dx] } }
                        } }
                    }
                }
                guard try reader.marker() == 0xd9 else { throw CodecFailure.unsupported("Multiple JPEG scans/fields are not implemented.") }
                guard adobeTransform == nil || adobeTransform == 0 || adobeTransform == 1 else { throw CodecFailure.unsupported("JPEG color transform is not implemented.") }
                let rgb = adobeTransform == 0 || components.map(\.id) == [82,71,66]
                var output = Data(count: width * height * 4)
                try output.withUnsafeMutableBytes { storage in
                    let pixels = storage.bindMemory(to: UInt8.self)
                    for y in 0..<height {
                        try Task.checkCancellation()
                        for x in 0..<width {
                        func sample(_ index: Int) -> Double {
                            let component = components[index]
                            return Double(component.plane[(y * component.v / maxV) * component.stride + x * component.h / maxH])
                        }
                        let first = sample(0), index = (y * width + x) * 4
                        let r: Double, g: Double, b: Double
                        if components.count == 1 { r = first; g = first; b = first }
                        else if rgb { r = first; g = sample(1); b = sample(2) }
                        else {
                            let cb = sample(1) - 128, cr = sample(2) - 128
                            r = first + 1.402 * cr; g = first - 0.344136 * cb - 0.714136 * cr; b = first + 1.772 * cb
                        }
                        pixels[index] = UInt8(clamping: Int(r.rounded())); pixels[index + 1] = UInt8(clamping: Int(g.rounded())); pixels[index + 2] = UInt8(clamping: Int(b.rounded())); pixels[index + 3] = 255
                    } }
                }
                guard let provider = CGDataProvider(data: output as CFData),
                      let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                                          space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue), provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent) else { throw CodecFailure.malformed("JPEG image output") }
                return image
            case 0xc1...0xcf where marker != 0xc4 && marker != 0xc8 && marker != 0xcc:
                throw CodecFailure.unsupported("This JPEG coding mode is not baseline sequential Huffman coding.")
            case 0xe0...0xef, 0xfe: break
            default: throw CodecFailure.unsupported("JPEG marker \(marker) is not implemented.")
            }
        }
        throw CodecFailure.malformed("JPEG scan missing")
    }

    private static func idct(_ coefficients: [Int]) -> [UInt8] {
        if coefficients.dropFirst().allSatisfy({ $0 == 0 }) { return [UInt8](repeating: UInt8(clamping: Int((Double(coefficients[0]) / 8 + 128).rounded())), count: 64) }
        var intermediate = [Double](repeating: 0, count: 64)
        for y in 0..<8 { for u in 0..<8 {
            var sum = 0.0
            for v in 0..<8 { sum += basis[y][v] * Double(coefficients[v * 8 + u]) }
            intermediate[y * 8 + u] = sum
        } }
        var result = [UInt8](repeating: 0, count: 64)
        for y in 0..<8 { for x in 0..<8 {
            var sum = 128.0
            for u in 0..<8 { sum += intermediate[y * 8 + u] * basis[x][u] }
            result[y * 8 + x] = UInt8(clamping: Int(sum.rounded()))
        } }
        return result
    }
}
