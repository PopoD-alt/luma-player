import Foundation
import AudioToolbox

@main
struct AACConfigurationSmoke {
    enum Failure: Error { case check(String) }
    struct Bits {
        var values: [Int] = []
        mutating func put(_ value: Int, _ count: Int) {
            for bit in (0..<count).reversed() { values.append((value >> bit) & 1) }
        }
        func data() -> Data {
            let bits = values + [Int](repeating: 0, count: (-values.count) & 7)
            return Data(stride(from: 0, to: bits.count, by: 8).map { start in
                UInt8(bits[start..<start + 8].reduce(0) { ($0 << 1) | $1 })
            })
        }
    }

    static func main() throws {
        let real = try AACConfiguration.parse(hex("131056e59d4800"))
        try check(real.coreSampleRate == 24000 && real.outputSampleRate == 48000, "real MKV core/output rates")
        try check(real.coreFrames == 1024 && real.outputFrames == 2048 && real.channels == 2, "real MKV rendered packet geometry")
        try check(real.objectType == 5 && real.encodedObjectType == 2 && real.sbrPresent && !real.psPresent, "real MKV backward-compatible HE-AAC signaling")
        try check(real.outputFormatID == kAudioFormatMPEG4AAC_HE && real.outputFormatFlags == 0, "HE-AAC CoreAudio format/flags")
        try real.validateMatroska(sampleRate: 24000, outputSampleRate: 48000, channels: 2)
        try check(abs(Double(real.coreFrames) / Double(real.coreSampleRate) - 0.0426666666667) < 1e-10, "real block duration")
        print("PASS: actual ASC131056e59d4800 → HE-AAC24000Hz/1024 core →48000Hz/2048 stereo output")

        let lc = try AACConfiguration.parse(hex("1210"))
        try check(lc.objectType == 2 && lc.coreSampleRate == 44100 && lc.outputSampleRate == 44100 && lc.channels == 2 && lc.outputFrames == 1024, "AAC-LC known vector")
        try check(lc.outputFormatID == kAudioFormatMPEG4AAC && lc.outputFormatFlags == 2, "AAC-LC platform identifiers")
        try lc.validateMatroska(sampleRate: 44100, outputSampleRate: nil, channels: 2)
        let mono = try AACConfiguration.parse(hex("1208"))
        try check(mono.channels == 1, "AAC-LC mono")

        let explicitHE = try AACConfiguration.parse(configuration(object: 5, coreIndex: 6, channels: 2, outputIndex: 3))
        try check(explicitHE.objectType == 5 && explicitHE.encodedObjectType == 5 && explicitHE.outputFrames == 2048, "explicit HE-AACv1")
        let explicitPS = try AACConfiguration.parse(configuration(object: 29, coreIndex: 7, channels: 1, outputIndex: 4))
        try check(explicitPS.objectType == 29 && explicitPS.coreChannels == 1 && explicitPS.channels == 2 && explicitPS.outputSampleRate == 44100, "explicit HE-AACv2")
        try check(explicitPS.outputFormatID == kAudioFormatMPEG4AAC_HE_V2 && explicitPS.outputFormatFlags == 0, "HE-AACv2 platform identifiers")
        try explicitPS.validateMatroska(sampleRate: 22050, outputSampleRate: 44100, channels: 2)
        let syncPS = try AACConfiguration.parse(configuration(object: 2, coreIndex: 6, channels: 1, syncSBR: true, outputIndex: 3, syncPS: true))
        try check(syncPS.objectType == 29 && syncPS.channels == 2 && syncPS.outputFrames == 2048, "sync HE-AACv2")
        let sbrAbsent = try AACConfiguration.parse(configuration(object: 2, coreIndex: 6, channels: 2, syncSBR: false))
        try check(sbrAbsent.objectType == 2 && !sbrAbsent.sbrPresent && sbrAbsent.outputSampleRate == 24000, "explicitly absent sync SBR")
        let downsampled = try AACConfiguration.parse(configuration(object: 5, coreIndex: 6, channels: 2, outputIndex: 6))
        try check(downsampled.outputFrames == 1024 && downsampled.outputSampleRate == 24000, "downsampled SBR geometry")
        let shortFrames = try AACConfiguration.parse(configuration(object: 5, coreIndex: 6, channels: 2, frameLength: 1, outputIndex: 3))
        try check(shortFrames.coreFrames == 960 && shortFrames.outputFrames == 1920, "960/1920-frame geometry")
        let explicitRate = try AACConfiguration.parse(configuration(object: 2, coreIndex: 15, channels: 2, explicitCoreRate: 12345))
        try check(explicitRate.coreSampleRate == 12345, "24-bit explicit core sampling rate")
        let explicitOutputRate = try AACConfiguration.parse(configuration(object: 5, coreIndex: 15, channels: 1, outputIndex: 15, explicitCoreRate: 22000, explicitOutputRate: 44000))
        try check(explicitOutputRate.outputSampleRate == 44000 && explicitOutputRate.outputFrames == 2048, "explicit extension sampling rate")
        print("PASS: AAC-LC mono/stereo, explicit/sync HE-AACv1/v2, downsampled SBR,960-frame cores and explicit frequencies")

        try rejects(Data(), "empty ASC")
        try rejects(Data([0x12]), "truncated base ASC")
        try rejects(Data(repeating: 0, count: 65), "oversized ASC")
        for count in 3...6 { try rejects(Data(hex("131056e59d4800").prefix(count)), "truncated actual extension at\(count)") }
        try rejects(configuration(object: 1, coreIndex: 4, channels: 2), "unsupported AAC Main")
        try rejects(configuration(object: 2, coreIndex: 13, channels: 2), "reserved core frequency index")
        try rejects(configuration(object: 2, coreIndex: 15, channels: 2, explicitCoreRate: 0), "zero explicit core frequency")
        try rejects(configuration(object: 2, coreIndex: 4, channels: 0), "PCE channel layout")
        try rejects(configuration(object: 2, coreIndex: 4, channels: 6), "multichannel unsupported scope")
        try rejects(configuration(object: 29, coreIndex: 6, channels: 2, outputIndex: 3), "PS with stereo core")
        try rejects(configuration(object: 2, coreIndex: 6, channels: 1, syncSBR: false, syncPS: true), "PS without SBR")
        try rejects(configuration(object: 5, coreIndex: 6, channels: 2, outputIndex: 4), "noninteger SBR rate ratio")
        try rejects(configuration(object: 5, coreIndex: 6, channels: 2, outputIndex: 3, underlyingObject: 1), "HE-AAC without LC core")
        try rejects(configuration(object: 2, coreIndex: 4, channels: 2, dependsOnCore: true), "core-coder dependency")
        try rejects(configuration(object: 2, coreIndex: 4, channels: 2, extensionFlag: true), "GA extension flag")
        var trailing = hex("1210"); trailing.append(1)
        try rejects(trailing, "unknown nonzero trailing bits")
        try rejects(configuration(object: 2, coreIndex: 6, channels: 2, syncSBR: true, outputIndex: 3, extensionObject: 22), "unsupported sync extension object")
        try mismatch { try real.validateMatroska(sampleRate: 48000, outputSampleRate: 48000, channels: 2) }
        try mismatch { try real.validateMatroska(sampleRate: 24000, outputSampleRate: 24000, channels: 2) }
        try mismatch { try explicitPS.validateMatroska(sampleRate: 22050, outputSampleRate: 44100, channels: 1) }
        try mismatch { try real.validateMatroska(sampleRate: .nan, outputSampleRate: 48000, channels: 2) }
        try mismatch { try real.validateMatroska(sampleRate: 24000, outputSampleRate: .infinity, channels: 2) }
        var padded = Data([0]); padded.append(hex("131056e59d4800"))
        try check(try AACConfiguration.parse(padded[1...]).outputSampleRate == 48000, "Data slice rebasing")
        print("PASS: malformed/truncated/reserved/unknown extensions and metadata disagreements rejected; source slices supported")
    }

    static func configuration(object: Int, coreIndex: Int, channels: Int, frameLength: Int = 0,
                              syncSBR: Bool? = nil, outputIndex: Int = 3, syncPS: Bool? = nil,
                              explicitCoreRate: Int = 24000, explicitOutputRate: Int = 48000,
                              underlyingObject: Int = 2, dependsOnCore: Bool = false, extensionFlag: Bool = false,
                              extensionObject: Int = 5) -> Data {
        var bits = Bits()
        bits.put(object,5); bits.put(coreIndex,4)
        if coreIndex == 15 { bits.put(explicitCoreRate,24) }
        bits.put(channels,4)
        if object == 5 || object == 29 {
            bits.put(outputIndex,4); if outputIndex == 15 { bits.put(explicitOutputRate,24) }
            bits.put(underlyingObject,5)
        }
        bits.put(frameLength,1); bits.put(dependsOnCore ? 1 : 0,1)
        if dependsOnCore { bits.put(17,14) }
        bits.put(extensionFlag ? 1 : 0,1)
        if let syncSBR {
            bits.put(0x2b7,11); bits.put(extensionObject,5); bits.put(syncSBR ? 1 : 0,1)
            if syncSBR { bits.put(outputIndex,4); if outputIndex == 15 { bits.put(explicitOutputRate,24) } }
            if let syncPS { bits.put(0x548,11); bits.put(syncPS ? 1 : 0,1) }
        }
        return bits.data()
    }
    static func hex(_ string: String) -> Data {
        Data(stride(from: 0, to: string.count, by: 2).map { offset in
            let first = string.index(string.startIndex, offsetBy: offset)
            return UInt8(string[first..<string.index(first, offsetBy: 2)], radix:16)!
        })
    }
    static func rejects(_ data: Data, _ name: String) throws {
        do { _ = try AACConfiguration.parse(data) }
        catch is AACConfigurationError { return }
        throw Failure.check("Invalid/unsupported ASC accepted: \(name)")
    }
    static func mismatch(_ operation: () throws -> Void) throws {
        do { try operation() }
        catch AACConfigurationError.metadataMismatch { return }
        throw Failure.check("Invalid Matroska metadata accepted")
    }
    static func check(_ condition: @autoclosure () throws -> Bool, _ name: String) throws {
        if try !condition() { throw Failure.check(name) }
    }
}
