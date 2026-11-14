import Foundation
import CoreMedia

enum HEVCConfigurationError: Error, LocalizedError {
    case malformed(String), unsupported(String), system(String)
    var errorDescription: String? {
        switch self {
        case .malformed(let detail): return "Malformed HEVC configuration: \(detail)"
        case .unsupported(let detail): return "Unsupported HEVC configuration: \(detail)"
        case .system(let detail): return "The macOS HEVC decoder rejected this configuration: \(detail)"
        }
    }
}

struct HEVCNALArray: Sendable {
    let nalUnitType: Int
    let isComplete: Bool
    let units: [Data]
}

/// Original ISO/IEC 14496-15 HEVCDecoderConfigurationRecord parser. Compressed
/// samples and parameter-set bytes remain unchanged; macOS supplies HEVC decoding.
struct HEVCConfiguration: Sendable {
    let record: Data
    let nalUnitHeaderLength: Int32
    let profileSpace: Int
    let tierFlag: Bool
    let profileID: Int
    let compatibilityFlags: UInt32
    let constraintIndicatorFlags: UInt64
    let levelID: Int
    let minSpatialSegmentation: Int
    let parallelismType: Int
    let chromaFormat: Int
    let bitDepthLuma: Int
    let bitDepthChroma: Int
    let averageFrameRate: Int
    let constantFrameRate: Int
    let temporalLayers: Int
    let temporalIDNested: Bool
    let arrays: [HEVCNALArray]
    var parameterSets: [Data] { arrays.flatMap(\.units) }

    static func parse(_ source: Data) throws -> HEVCConfiguration {
        guard source.count >= 23 else { throw HEVCConfigurationError.malformed("truncated or missing hvcC record") }
        guard source.count <= 1_048_576 else { throw HEVCConfigurationError.unsupported("hvcC record exceeds 1 MiB") }
        let data = source.startIndex == 0 ? source : Data(source)
        guard data[0] == 1 else { throw HEVCConfigurationError.unsupported("hvcC configuration version \(data[0])") }
        guard data[13] & 0xf0 == 0xf0, data[15] & 0xfc == 0xfc,
              data[16] & 0xfc == 0xfc, data[17] & 0xf8 == 0xf8,
              data[18] & 0xf8 == 0xf8 else { throw HEVCConfigurationError.malformed("invalid hvcC reserved bits") }
        let length = Int32((data[21] & 3) + 1)
        guard [1,2,4].contains(length) else { throw HEVCConfigurationError.unsupported("three-byte HEVC NAL lengths") }
        var cursor = 23
        var arrays: [HEVCNALArray] = []
        var unitCount = 0
        func take(_ count: Int) throws -> Range<Int> {
            guard count >= 0, cursor <= data.count, count <= data.count - cursor else {
                throw HEVCConfigurationError.malformed("truncated hvcC array or NAL unit")
            }
            let range = cursor..<cursor + count
            cursor += count
            return range
        }
        func u16() throws -> Int {
            let range = try take(2)
            return Int(data[range.lowerBound]) << 8 | Int(data[range.lowerBound + 1])
        }
        for _ in 0..<Int(data[22]) {
            let descriptor = data[try take(1).lowerBound]
            guard descriptor & 0x40 == 0 else { throw HEVCConfigurationError.malformed("nonzero hvcC array reserved bit") }
            let type = Int(descriptor & 0x3f)
            let count = try u16()
            guard count <= 1024 - unitCount else { throw HEVCConfigurationError.unsupported("more than 1024 HEVC parameter-set NAL units") }
            unitCount += count
            var units: [Data] = []
            for _ in 0..<count {
                let size = try u16()
                guard size >= 2 else { throw HEVCConfigurationError.malformed("HEVC NAL unit is shorter than its header") }
                let range = try take(size)
                let first = data[range.lowerBound], second = data[range.lowerBound + 1]
                guard first & 0x80 == 0, second & 7 != 0 else { throw HEVCConfigurationError.malformed("invalid HEVC NAL header") }
                guard Int((first >> 1) & 0x3f) == type else { throw HEVCConfigurationError.malformed("HEVC NAL type differs from its hvcC array") }
                units.append(Data(data[range]))
            }
            arrays.append(HEVCNALArray(nalUnitType: type, isComplete: descriptor & 0x80 != 0, units: units))
        }
        guard cursor == data.count else { throw HEVCConfigurationError.unsupported("unrecognized trailing hvcC extension bytes") }
        for required in [32,33,34] {
            guard arrays.contains(where: { $0.nalUnitType == required && !$0.units.isEmpty }) else {
                throw HEVCConfigurationError.malformed("hvcC needs at least one VPS, SPS, and PPS; missing NAL type \(required)")
            }
        }
        var compatibility: UInt32 = 0, constraints: UInt64 = 0
        for offset in 2..<6 { compatibility = (compatibility << 8) | UInt32(data[offset]) }
        for offset in 6..<12 { constraints = (constraints << 8) | UInt64(data[offset]) }
        return HEVCConfiguration(
            record: data, nalUnitHeaderLength: length,
            profileSpace: Int(data[1] >> 6), tierFlag: data[1] & 0x20 != 0,
            profileID: Int(data[1] & 0x1f), compatibilityFlags: compatibility,
            constraintIndicatorFlags: constraints, levelID: Int(data[12]),
            minSpatialSegmentation: Int(data[13] & 15) << 8 | Int(data[14]),
            parallelismType: Int(data[15] & 3), chromaFormat: Int(data[16] & 3),
            bitDepthLuma: Int(data[17] & 7) + 8, bitDepthChroma: Int(data[18] & 7) + 8,
            averageFrameRate: Int(data[19]) << 8 | Int(data[20]),
            constantFrameRate: Int(data[21] >> 6), temporalLayers: Int((data[21] >> 3) & 7),
            temporalIDNested: data[21] & 4 != 0, arrays: arrays
        )
    }

    func makeFormatDescription() throws -> CMVideoFormatDescription {
        let supportedTypes: Set<Int> = [32,33,34,39,40]
        guard arrays.allSatisfy({ supportedTypes.contains($0.nalUnitType) }) else {
            throw HEVCConfigurationError.unsupported("hvcC contains NAL types other than VPS, SPS, PPS, prefix SEI, or suffix SEI")
        }
        let sets = parameterSets.map { $0 as NSData }
        let pointers = sets.map { $0.bytes.assumingMemoryBound(to: UInt8.self) }
        let sizes = sets.map(\.length)
        var description: CMFormatDescription?
        let extensions = [kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms as String: ["hvcC": record]] as CFDictionary
        let status = pointers.withUnsafeBufferPointer { pointerBuffer in
            sizes.withUnsafeBufferPointer { sizeBuffer in
                CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                    allocator: nil, parameterSetCount: sets.count,
                    parameterSetPointers: pointerBuffer.baseAddress!, parameterSetSizes: sizeBuffer.baseAddress!,
                    nalUnitHeaderLength: nalUnitHeaderLength, extensions: extensions,
                    formatDescriptionOut: &description
                )
            }
        }
        guard status == noErr, let description else { throw HEVCConfigurationError.system("parameter-set status \(status)") }
        // CoreMedia may synthesize an hvcC record from SPS/PPS. Retain the source
        // record explicitly, including profile/bit-depth fields, SEI arrays and
        // completeness bits, while preserving every extension CoreMedia derived.
        if let atoms = CMFormatDescriptionGetExtension(description, extensionKey: kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms) as? [String: Any],
           let stored = atoms["hvcC"] as? Data, stored == record { return description }
        let dimensions = CMVideoFormatDescriptionGetDimensions(description)
        var retainedExtensions = CMFormatDescriptionGetExtensions(description) as? [String: Any] ?? [:]
        var atoms = retainedExtensions[kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms as String] as? [String: Any] ?? [:]
        atoms["hvcC"] = record
        retainedExtensions[kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms as String] = atoms
        var retained: CMFormatDescription?
        let retainStatus = CMVideoFormatDescriptionCreate(allocator: nil, codecType: kCMVideoCodecType_HEVC,
            width: dimensions.width, height: dimensions.height, extensions: retainedExtensions as CFDictionary,
            formatDescriptionOut: &retained)
        guard retainStatus == noErr, let retained else { throw HEVCConfigurationError.system("format-extension status \(retainStatus)") }
        return retained
    }
}
