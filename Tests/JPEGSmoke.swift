import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

@main struct JPEGSmoke {
    static func main() throws {
        let output = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        func rejects(_ label: String, _ action: () throws -> Void) {
            do { try action() } catch { return }; fatalError("Accepted invalid JPEG: \(label)")
        }
        func segment(_ marker: UInt8, _ payload: [UInt8]) -> [UInt8] {
            let length = payload.count + 2
            return [255,marker,UInt8(length >> 8),UInt8(length & 255)] + payload
        }
        let quantizer = segment(219, [0] + [UInt8](repeating: 1, count: 64))
        let oneCode = [UInt8](arrayLiteral: 1) + [UInt8](repeating: 0, count: 15)
        let huffman = segment(196, [0] + oneCode + [0] + [16] + oneCode + [0])
        func gray(_ width: UInt8, restart: Bool = false) -> Data {
            let frame = segment(192, [8,0,8,0,width,1,1,0x11,0])
            let scan = segment(218, [1,1,0,0,63,0])
            let entropy: [UInt8] = restart ? [0x3f,255,208,0x3f] : [0x3f]
            return Data([255,216] + quantizer + frame + huffman + (restart ? segment(221,[0,1]) : []) + scan + entropy + [255,217])
        }
        let constant = gray(8)
        let constantImage = try OriginalJPEGDecoder.decode(constant)
        let constantPixels = constantImage.dataProvider!.data! as Data
        for index in stride(from: 0, to: constantPixels.count, by: 4) { precondition(Array(constantPixels[index..<index + 4]) == [128,128,128,255]) }
        let restart = try OriginalJPEGDecoder.decode(gray(16, restart: true))
        precondition(restart.width == 16 && restart.height == 8)
        var badPadding = constant
        badPadding[badPadding.count - 3] = 0
        rejects("zero entropy padding") { _ = try OriginalJPEGDecoder.decode(badPadding) }
        var badSampling = constant
        let frameMarker = badSampling.range(of: Data([255,192]))!.lowerBound
        badSampling[frameMarker + 11] = 0x22
        rejects("unsupported grayscale sampling") { _ = try OriginalJPEGDecoder.decode(badSampling) }
        print("PASS: independent constant-gray/restart JPEG bitstreams; padding and sampling rejection")
        let width = 127, height = 95
        var pixels = Data(count: width * height * 4)
        pixels.withUnsafeMutableBytes { memory in
            let p = memory.bindMemory(to: UInt8.self)
            for y in 0..<height { for x in 0..<width {
                let i = (y * width + x) * 4
                p[i] = UInt8(x * 255 / (width - 1)); p[i + 1] = UInt8(y * 255 / (height - 1)); p[i + 2] = UInt8((x + y) * 255 / (width + height - 2)); p[i + 3] = 255
            } }
        }
        let source = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                             space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue),
                             provider: CGDataProvider(data: pixels as CFData)!, decode: nil, shouldInterpolate: true, intent: .defaultIntent)!
        let encoded = NSMutableData()
        let writer = CGImageDestinationCreateWithData(encoded, UTType.jpeg.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(writer, source, [kCGImageDestinationLossyCompressionQuality: 0.9] as CFDictionary)
        precondition(CGImageDestinationFinalize(writer))
        let data = encoded as Data
        try data.write(to: output.appendingPathComponent("Luma-Baseline.jpg"))
        let decoded = try OriginalJPEGDecoder.decode(data)
        precondition(decoded.width == width && decoded.height == height)
        let reference = CGImageSourceCreateImageAtIndex(CGImageSourceCreateWithData(data as CFData, nil)!, 0, nil)!
        let referenceBytes = NSMutableData(length: width * height * 4)!
        let context = CGContext(data: referenceBytes.mutableBytes, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
        context.draw(reference, in: CGRect(x: 0, y: 0, width: width, height: height))
        let actual = decoded.dataProvider!.data! as Data, expected = referenceBytes as Data
        var total = 0, maximum = 0, count = 0
        for index in stride(from: 0, to: actual.count, by: 4) { for channel in 0..<3 {
            let error = abs(Int(actual[index + channel]) - Int(expected[index + channel])); total += error; maximum = max(maximum, error); count += 1
        } }
        let mean = Double(total) / Double(count)
        precondition(mean < 3.0 && maximum < 16, "JPEG cross-decoder mismatch: mean \(mean), max \(maximum)")
        rejects("truncation") { _ = try OriginalJPEGDecoder.decode(Data(data.dropLast(10))) }
        var progressive = data
        if let frame = progressive.range(of: Data([255,192])) { progressive[frame.lowerBound + 1] = 194 }
        rejects("progressive coding") { _ = try OriginalJPEGDecoder.decode(progressive) }
        var malformed = data
        if let dqt = malformed.range(of: Data([255,219])) { malformed[dqt.lowerBound + 5] = 0 }
        rejects("zero quantizer") { _ = try OriginalJPEGDecoder.decode(malformed) }
        print("PASS: original baseline JPEG \(width)×\(height), system-reference mean error \(mean), max \(maximum), truncation/progressive/quantizer rejection")
    }
}
