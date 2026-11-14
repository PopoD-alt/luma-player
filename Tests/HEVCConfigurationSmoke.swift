import Foundation
import AVFoundation
import CoreMedia
import CoreVideo

@main
struct HEVCConfigurationSmoke {
    enum Failure: Error { case check(String) }
    static func main() async throws {
        let directory = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Tests/Fixtures", isDirectory: true)
        for depth in [8,10] {
            let data = try Data(contentsOf: directory.appendingPathComponent("Luma-HEVC\(depth).hvcC"))
            let configuration = try HEVCConfiguration.parse(data)
            try check(configuration.bitDepthLuma == depth && configuration.bitDepthChroma == depth, "HEVC source depth lost")
            try check(configuration.nalUnitHeaderLength == 4 && configuration.chromaFormat == 1, "Native HEVC NAL/chroma interpretation")
            let description = try configuration.makeFormatDescription()
            let dimensions = CMVideoFormatDescriptionGetDimensions(description)
            try check(dimensions.width == 960 && dimensions.height == 540, "HEVC dimensions differ")
            let atoms = CMFormatDescriptionGetExtension(description, extensionKey: kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms) as? [String: Any]
            try check(atoms?["hvcC"] as? Data == data, "Original hvcC record was not retained")
            let asset = AVURLAsset(url: directory.appendingPathComponent("Luma-HEVC\(depth).mov"))
            let track = try await asset.loadTracks(withMediaType: .video).first!
            let reader = try AVAssetReader(asset: asset)
            let pixelFormat = depth == 10 ? kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange : kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
            let output = AVAssetReaderTrackOutput(track: track, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: pixelFormat])
            reader.add(output)
            guard reader.startReading() else { throw reader.error ?? Failure.check("Native HEVC decoder cannot start") }
            var count = 0
            while let sample = output.copyNextSampleBuffer() {
                guard let pixels = CMSampleBufferGetImageBuffer(sample) else { throw Failure.check("Native HEVC decoder produced no pixels") }
                try check(CVPixelBufferGetWidth(pixels) == 960 && CVPixelBufferGetHeight(pixels) == 540, "Decoded dimensions")
                try check(CVPixelBufferGetPixelFormatType(pixels) == pixelFormat, "Native HEVC output precision differs")
                count += 1
            }
            try check(reader.status == .completed && count == 60, "Native HEVC decode lost frames")
            print("PASS: original hvcC parser, retained \(depth)-bit config, CoreMedia dimensions, macOS decoded \(count) frames at \(depth)-bit output")

            // A valid original user-data-unregistered prefix SEI proves that
            // optional arrays/completeness are retained, not silently dropped.
            let sei = Data([0x4e, 1, 5, 20]) + Data(1...16) + Data("Luma".utf8) + Data([0x80])
            var withSEI = data
            withSEI[22] += 1
            withSEI.append(contentsOf: [0xa7, 0, 1, 0, UInt8(sei.count)])
            withSEI.append(sei)
            let extended = try HEVCConfiguration.parse(withSEI)
            try check(extended.arrays.last?.nalUnitType == 39 && extended.arrays.last?.isComplete == true && extended.arrays.last?.units == [sei], "Prefix SEI array/completeness lost")
            let extendedDescription = try extended.makeFormatDescription()
            let extendedAtoms = CMFormatDescriptionGetExtension(extendedDescription, extensionKey: kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms) as? [String: Any]
            try check(extendedAtoms?["hvcC"] as? Data == withSEI, "Optional HEVC arrays not retained in platform format")
            for headerLength in [1,2] {
                var alternate = data
                alternate[21] = (alternate[21] & 0xfc) | UInt8(headerLength - 1)
                let parsed = try HEVCConfiguration.parse(alternate)
                _ = try parsed.makeFormatDescription()
                try check(parsed.nalUnitHeaderLength == Int32(headerLength), "One/two-byte NAL-length support")
            }

            for length in 0..<data.count { try rejects(Data(data.prefix(length)), "truncation\(length)") }
            var bad = data; bad[0] = 2; try rejects(bad, "unknown record version")
            bad = data; bad[13] &= 0x0f; try rejects(bad, "reserved header bits")
            bad = data; bad[21] = (bad[21] & 0xfc) | 2; try rejects(bad, "three-byte NAL length")
            bad = data; bad[22] = 0; try rejects(bad, "missing parameter-set arrays")
            bad = data; bad[23] |= 0x40; try rejects(bad, "reserved array bit")
            bad = data; bad[24] = 0xff; bad[25] = 0xff; try rejects(bad, "oversized NAL count")
            bad = data; bad[26] = 0; bad[27] = 1; try rejects(bad, "NAL shorter than header")
            bad = data; bad[28] |= 0x80; try rejects(bad, "forbidden NAL bit")
            bad = data; bad[29] &= 0xf8; try rejects(bad, "zero temporal-id-plus-one")
            bad = data; bad[28] ^= 2; try rejects(bad, "NAL type/array mismatch")
            bad = data; bad.append(1); try rejects(bad, "unhandled trailing configuration")
            var wrapped = Data([0]); wrapped.append(data)
            try check(try HEVCConfiguration.parse(wrapped[1...]).record == data, "Data slice indexing")
        }
        try rejects(Data(repeating: 0, count: 1_048_577), "oversized hvcC")
        print("PASS: truncated, missing, oversized, malformed and unsupported configurations rejected")
    }
    static func rejects(_ data: Data, _ detail: String) throws {
        do { _ = try HEVCConfiguration.parse(data) }
        catch is HEVCConfigurationError { return }
        throw Failure.check("Invalid HEVC configuration accepted: \(detail)")
    }
    static func check(_ value: @autoclosure () throws -> Bool, _ detail: String) throws {
        if try !value() { throw Failure.check(detail) }
    }
}
