import Foundation
import AudioToolbox

enum AACConfigurationError: Error, LocalizedError {
    case malformed(String), unsupported(String), metadataMismatch(String)
    var errorDescription: String? {
        switch self {
        case .malformed(let detail): return "Malformed AAC configuration: \(detail)"
        case .unsupported(let detail): return "Unsupported AAC configuration: \(detail)"
        case .metadataMismatch(let detail): return "AAC/Matroska metadata mismatch: \(detail)"
        }
    }
}

/// Original AudioSpecificConfig interpretation, not an AAC sample decoder.
/// Syntax references: ISO/IEC 14496-3 AudioSpecificConfig/GASpecificConfig;
/// published 2009 Amendment 1 Table 1.15 covers explicit and sync SBR/PS signaling.
/// Apple CoreAudioBaseTypes.h defines the resulting platform format identifiers.
struct AACConfiguration: Sendable {
    let coreSampleRate: Int
    let outputSampleRate: Int
    let coreChannels: Int
    let channels: Int
    let coreFrames: Int
    let outputFrames: Int
    /// Effective profile: 2=AAC-LC, 5=HE-AAC (SBR), 29=HE-AAC v2 (SBR+PS).
    let objectType: Int
    let encodedObjectType: Int
    let sbrPresent: Bool
    let psPresent: Bool
    let outputFormatID: AudioFormatID
    let outputFormatFlags: AudioFormatFlags
    var profileName: String { objectType == 29 ? "HE-AAC v2" : objectType == 5 ? "HE-AAC" : "AAC-LC" }

    static func parse(_ source: Data) throws -> AACConfiguration {
        guard source.count >= 2 else { throw AACConfigurationError.malformed("truncated AudioSpecificConfig") }
        guard source.count <= 64 else { throw AACConfigurationError.unsupported("AudioSpecificConfig exceeds the 64-byte parser limit") }
        var reader = AACConfigBits(data: source.startIndex == 0 ? source : Data(source))
        let initialObject = try reader.objectType()
        guard [2,5,29].contains(initialObject) else {
            throw AACConfigurationError.unsupported("audio object type \(initialObject); only AAC-LC and HE-AAC v1/v2 are supported")
        }
        let coreRate = try reader.frequency()
        let coreChannels = try reader.read(4)
        guard (1...2).contains(coreChannels) else {
            throw AACConfigurationError.unsupported(coreChannels == 0 ? "program-config-element channel layouts" : "channel configuration \(coreChannels); only mono/stereo are supported")
        }
        var outputRate = coreRate
        var sbr = initialObject == 5 || initialObject == 29
        var ps = initialObject == 29
        if sbr {
            outputRate = try reader.frequency()
            let underlying = try reader.objectType()
            guard underlying == 2 else { throw AACConfigurationError.unsupported("HE-AAC requires an AAC-LC core, not object type \(underlying)") }
        }

        let frameLengthFlag = try reader.read(1)
        let coreFrames = frameLengthFlag == 0 ? 1024 : 960
        let dependsOnCore = try reader.read(1)
        if dependsOnCore != 0 {
            _ = try reader.read(14)
            throw AACConfigurationError.unsupported("dependsOnCoreCoder/core-coder-delay signaling")
        }
        guard try reader.read(1) == 0 else { throw AACConfigurationError.unsupported("GASpecificConfig extensionFlag") }

        // Backward-compatible signaling appends this exact sync extension to
        // an AAC-LC config. Do not scan for patterns inside arbitrary data.
        if initialObject == 2, reader.remaining >= 11, try reader.peek(11) == 0x2b7 {
            _ = try reader.read(11)
            let extensionObject = try reader.objectType()
            guard extensionObject == 5 else {
                throw AACConfigurationError.unsupported("sync extension audio object type \(extensionObject)")
            }
            sbr = try reader.read(1) != 0
            if sbr { outputRate = try reader.frequency() }
            if reader.remaining >= 11, try reader.peek(11) == 0x548 {
                _ = try reader.read(11)
                ps = try reader.read(1) != 0
            }
        }
        // Zero fill to a byte boundary (and bounded zero padding) is benign.
        // Every nonzero unhandled bit is rejected rather than silently treating
        // an unknown codec extension as LC or SBR.
        while reader.remaining > 0 {
            guard try reader.read(1) == 0 else {
                throw AACConfigurationError.unsupported("unknown, conflicting, or truncated trailing extension bits")
            }
        }
        guard !ps || sbr else { throw AACConfigurationError.malformed("parametric stereo requires SBR") }
        guard !ps || coreChannels == 1 else { throw AACConfigurationError.unsupported("parametric stereo requires a mono AAC core") }
        guard outputRate == coreRate || (sbr && outputRate == coreRate * 2) else {
            throw AACConfigurationError.unsupported("SBR output rate must equal or double the core rate")
        }
        let factor = outputRate / coreRate
        let effectiveObject = ps ? 29 : sbr ? 5 : 2
        return AACConfiguration(
            coreSampleRate: coreRate, outputSampleRate: outputRate,
            coreChannels: coreChannels, channels: ps ? 2 : coreChannels,
            coreFrames: coreFrames, outputFrames: coreFrames * factor,
            objectType: effectiveObject, encodedObjectType: initialObject,
            sbrPresent: sbr, psPresent: ps,
            outputFormatID: ps ? kAudioFormatMPEG4AAC_HE_V2 : sbr ? kAudioFormatMPEG4AAC_HE : kAudioFormatMPEG4AAC,
            outputFormatFlags: effectiveObject == 2 ? 2 : 0
        )
    }

    func validateMatroska(sampleRate: Double, outputSampleRate declaredOutput: Double?, channels declaredChannels: Int) throws {
        guard sampleRate.isFinite, sampleRate > 0, abs(sampleRate - Double(coreSampleRate)) < 0.5 else {
            throw AACConfigurationError.metadataMismatch("SamplingFrequency must match the \(coreSampleRate) Hz AAC core")
        }
        if let declaredOutput {
            guard declaredOutput.isFinite, declaredOutput > 0, abs(declaredOutput - Double(outputSampleRate)) < 0.5 else {
                throw AACConfigurationError.metadataMismatch("OutputSamplingFrequency must match the \(outputSampleRate) Hz rendered audio")
            }
        }
        // If OutputSamplingFrequency is omitted, the unambiguous ASC SBR
        // declaration still establishes the actual rendered frequency.
        guard declaredChannels == channels else {
            throw AACConfigurationError.metadataMismatch("Channels must match the \(channels)-channel rendered audio, not the \(coreChannels)-channel core")
        }
    }
}

private struct AACConfigBits {
    let data: Data
    var position = 0
    var remaining: Int { data.count * 8 - position }
    mutating func read(_ count: Int) throws -> Int {
        guard count >= 0, count <= 24, count <= remaining else { throw AACConfigurationError.malformed("truncated AudioSpecificConfig bit field") }
        var value = 0
        for _ in 0..<count {
            value = (value << 1) | Int((data[position / 8] >> (7 - position % 8)) & 1)
            position += 1
        }
        return value
    }
    func peek(_ count: Int) throws -> Int {
        var copy = self
        return try copy.read(count)
    }
    mutating func objectType() throws -> Int {
        let initial = try read(5)
        return initial == 31 ? 32 + (try read(6)) : initial
    }
    mutating func frequency() throws -> Int {
        let index = try read(4)
        let values = [96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350]
        let rate: Int
        if index == 15 { rate = try read(24) }
        else {
            guard values.indices.contains(index) else { throw AACConfigurationError.malformed("reserved sampling-frequency index") }
            rate = values[index]
        }
        guard (7350...96000).contains(rate) else {
            throw AACConfigurationError.unsupported("sampling frequency \(rate) Hz is outside 7350–96000 Hz")
        }
        return rate
    }
}
