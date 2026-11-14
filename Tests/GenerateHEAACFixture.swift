#!/usr/bin/env swift
// Original synthetic HE-AAC regression data, encoded only with Apple's afconvert.
// Run: swift Tests/GenerateHEAACFixture.swift [--inspect-only] [output-directory]
// Codec enumeration/encoding needs access to the macOS audio codec services.
// Public API definitions: AudioToolbox/AudioFile.h and CoreAudioTypes/CoreAudioBaseTypes.h.
import AudioToolbox
import Foundation

struct FixtureError: Error, CustomStringConvertible {
    let description: String
}

func checked(_ status: OSStatus, _ operation: String) throws {
    if status != noErr { throw FixtureError(description: "\(operation): OSStatus \(status)") }
}

func fourCC(_ value: UInt32) -> String {
    String(bytes: [24, 16, 8, 0].map { UInt8((value >> $0) & 255) }, encoding: .ascii) ?? "0x\(String(value, radix: 16))"
}

func describe(_ f: AudioStreamBasicDescription) -> [String: Any] {
    ["format_id": fourCC(f.mFormatID), "format_flags": f.mFormatFlags,
     "sample_rate": f.mSampleRate, "channels": f.mChannelsPerFrame,
     "frames_per_packet": f.mFramesPerPacket, "bytes_per_packet": f.mBytesPerPacket,
     "bytes_per_frame": f.mBytesPerFrame, "bits_per_channel": f.mBitsPerChannel,
     "packet_duration_seconds": f.mSampleRate > 0 ? Double(f.mFramesPerPacket) / f.mSampleRate : 0]
}

func readBytes(_ file: AudioFileID, _ property: AudioFilePropertyID) throws -> Data {
    var size: UInt32 = 0
    try checked(AudioFileGetPropertyInfo(file, property, &size, nil), "property size \(fourCC(property))")
    var bytes = Data(count: Int(size))
    try bytes.withUnsafeMutableBytes { raw in
        try checked(AudioFileGetProperty(file, property, &size, raw.baseAddress!), "property \(fourCC(property))")
    }
    return bytes.prefix(Int(size))
}

// Magic cookies for these M4A files contain an ES_Descriptor. Parse its explicit
// descriptor lengths and nesting to obtain DecoderSpecificInfo (ASC), never scan
// arbitrary payload bytes for a tag that could also occur inside audio config.
func audioSpecificConfig(_ data: Data) -> Data? {
    let bytes = Array(data)
    func descriptors(_ start: Int, _ end: Int) -> Data? {
        var cursor = start
        while cursor < end {
            let tag = bytes[cursor]
            cursor += 1
            var length = 0
            var terminated = false
            for _ in 0..<4 {
                guard cursor < end else { return nil }
                let byte = bytes[cursor]
                cursor += 1
                length = (length << 7) | Int(byte & 127)
                if byte & 128 == 0 { terminated = true; break }
            }
            guard terminated, length <= end - cursor else { return nil }
            let limit = cursor + length
            if tag == 5 { return Data(bytes[cursor..<limit]) }
            if tag == 3 {
                guard length >= 3 else { return nil }
                let flags = bytes[cursor + 2]
                var child = cursor + 3
                if flags & 128 != 0 { child += 2 }
                if flags & 64 != 0 {
                    guard child < limit else { return nil }
                    child += 1 + Int(bytes[child])
                }
                if flags & 32 != 0 { child += 2 }
                guard child <= limit else { return nil }
                if let asc = descriptors(child, limit) { return asc }
            } else if tag == 4, length >= 13,
                      let asc = descriptors(cursor + 13, limit) { return asc }
            cursor = limit
        }
        return nil
    }
    return descriptors(0, bytes.count)
}

func hex(_ data: Data) -> String { data.map { String(format: "%02x", $0) }.joined() }

func inspect(_ url: URL) throws -> [String: Any] {
    var opened: AudioFileID?
    try checked(AudioFileOpenURL(url as CFURL, .readPermission, 0, &opened), "open \(url.lastPathComponent)")
    guard let file = opened else { throw FixtureError(description: "AudioFileOpenURL returned no file") }
    defer { AudioFileClose(file) }
    var format = AudioStreamBasicDescription()
    var size = UInt32(MemoryLayout.size(ofValue: format))
    try checked(AudioFileGetProperty(file, kAudioFilePropertyDataFormat, &size, &format), "data format")
    let listBytes = try readBytes(file, kAudioFilePropertyFormatList)
    var formats: [[String: Any]] = []
    let itemStride = MemoryLayout<AudioFormatListItem>.stride
    for offset in stride(from: 0, to: listBytes.count, by: itemStride) {
        guard offset + itemStride <= listBytes.count else { break }
        let item = listBytes.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: offset, as: AudioFormatListItem.self) }
        var row = describe(item.mASBD)
        row["channel_layout_tag"] = item.mChannelLayoutTag
        formats.append(row)
    }
    let cookie = try readBytes(file, kAudioFilePropertyMagicCookieData)
    var packetTable = AudioFilePacketTableInfo()
    size = UInt32(MemoryLayout.size(ofValue: packetTable))
    try checked(AudioFileGetProperty(file, kAudioFilePropertyPacketTableInfo, &size, &packetTable), "packet table")
    var count: UInt64 = 0
    size = UInt32(MemoryLayout.size(ofValue: count))
    try checked(AudioFileGetProperty(file, kAudioFilePropertyAudioDataPacketCount, &size, &count), "packet count")
    var upper: UInt32 = 0
    size = UInt32(MemoryLayout.size(ofValue: upper))
    try checked(AudioFileGetProperty(file, kAudioFilePropertyPacketSizeUpperBound, &size, &upper), "packet upper bound")
    var descriptions = [AudioStreamPacketDescription](repeating: AudioStreamPacketDescription(), count: 4)
    var packetBytes = Data(count: Int(upper) * 4)
    var readCount: UInt32 = 4
    var readBytes = UInt32(packetBytes.count)
    try packetBytes.withUnsafeMutableBytes { raw in
        try checked(AudioFileReadPacketData(file, false, &readBytes, &descriptions, 0, &readCount, raw.baseAddress!), "read first packets")
    }
    let packets = descriptions.prefix(Int(readCount)).map {
        ["offset": $0.mStartOffset, "byte_size": Int64($0.mDataByteSize), "variable_frames": Int64($0.mVariableFramesInPacket)]
    }
    return ["file": url.lastPathComponent,
            "bytes": try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0,
            "default_asbd": describe(format), "format_list": formats,
            "magic_cookie_hex": hex(cookie), "audio_specific_config_hex": audioSpecificConfig(cookie).map(hex) ?? "UNRESOLVED",
            "packet_count": count, "first_packets": packets,
            "packet_table": ["valid_frames": packetTable.mNumberValidFrames,
                             "priming_frames": Int64(packetTable.mPrimingFrames),
                             "remainder_frames": Int64(packetTable.mRemainderFrames)],
            "notes": "Default ASBD and packet-table frames describe the AAC core. HE format-list entries describe expanded output. A zero variable_frames field uses each selected ASBD's fixed frames_per_packet."]
}

func appendLE<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
    var littleEndian = value.littleEndian
    withUnsafeBytes(of: &littleEndian) { data.append(contentsOf: $0) }
}

func syntheticTone(_ url: URL) throws {
    let rate: UInt32 = 48_000
    let samples = Int(rate) * 3
    let byteCount = UInt32(samples * 2)
    var wav = Data("RIFF".utf8)
    appendLE(byteCount + 36, to: &wav)
    wav.append(Data("WAVEfmt ".utf8))
    appendLE(UInt32(16), to: &wav)
    appendLE(UInt16(1), to: &wav)
    appendLE(UInt16(1), to: &wav)
    appendLE(rate, to: &wav)
    appendLE(rate * 2, to: &wav)
    appendLE(UInt16(2), to: &wav)
    appendLE(UInt16(16), to: &wav)
    wav.append(Data("data".utf8))
    appendLE(byteCount, to: &wav)
    for sample in 0..<samples {
        let t = Double(sample) / Double(rate)
        let fade = min(1, min(t / 0.08, (3 - t) / 0.08))
        appendLE(Int16(sin(t * 2 * .pi * 440) * 32767 * 0.12 * fade), to: &wav)
    }
    try wav.write(to: url)
}

let inspectOnly = CommandLine.arguments.contains("--inspect-only")
let directoryArgument = CommandLine.arguments.dropFirst().first { !$0.hasPrefix("--") }
let output = directoryArgument.map { URL(fileURLWithPath: $0, isDirectory: true) }
    ?? URL(fileURLWithPath: #filePath).deletingLastPathComponent().appendingPathComponent("Fixtures", isDirectory: true)
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
let configurations = [("Luma-HEAACv1.m4a", "aach", "48000"), ("Luma-HEAACv2.m4a", "aacp", "32000")]
var commands: [[String]] = []
if !inspectOnly {
    let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("Luma-HEAAC-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: temporary) }
    let source = temporary.appendingPathComponent("original-tone.wav")
    try syntheticTone(source)
    for (name, codec, bitrate) in configurations {
        let destination = output.appendingPathComponent(name)
        if FileManager.default.fileExists(atPath: destination.path) { try FileManager.default.removeItem(at: destination) }
        let args = ["-f", "m4af", "-d", "\(codec)@48000", "-c", "2", "-b", bitrate, source.path, destination.path]
        commands.append(["/usr/bin/afconvert"] + args)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/afconvert")
        process.arguments = args
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw FixtureError(description: "Native HE-AAC encoder failed (\(process.terminationStatus)). Check access to macOS codec services.") }
    }
}
let metadata: [String: Any] = [
    "provenance": "Original generated 3s/48kHz/16-bit mono 440Hz tone, faded, upmixed by Apple's afconvert to HE-AAC stereo. No user media, VLC, FFmpeg, or downloaded assets.",
    "generator": "Tests/GenerateHEAACFixture.swift", "commands": commands,
    "encoder_configurations": configurations.map { ["file": $0.0, "format": $0.1, "requested_bitrate": $0.2, "requested_sample_rate": "48000", "requested_channels": "2"] },
    "fixtures": try configurations.map { try inspect(output.appendingPathComponent($0.0)) },
    "metadata_authority": "AudioFileGetProperty(DataFormat, FormatList, MagicCookieData, PacketTableInfo), AudioFileReadPacketData",
    "limitations": "These native fixtures use backward-compatible AAC-LC ASC plus SBR/PS sync extensions. They do not exercise explicit AOT5/AOT29 configurations, 960-frame AAC, or arbitrary MPEG-4 profiles."
]
let json = try JSONSerialization.data(withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys])
try json.write(to: output.appendingPathComponent("Luma-HEAAC-Metadata.json"))
print(String(data: json, encoding: .utf8)!)
