#!/usr/bin/env swift
// Decode the original HE-AAC fixtures with Apple's native codec, independent of
// Luma's ASC parser/remuxer. Run: swift Tests/HEAACNativeSmoke.swift [fixture-dir]
import AudioToolbox
import Foundation

struct NativeSmokeError: Error, CustomStringConvertible {
    let description: String
}

func check(_ status: OSStatus, _ operation: String) throws {
    if status != noErr { throw NativeSmokeError(description: "\(operation): OSStatus \(status)") }
}

func decode(_ url: URL) throws {
    var opened: ExtAudioFileRef?
    try check(ExtAudioFileOpenURL(url as CFURL, &opened), "open \(url.lastPathComponent)")
    guard let file = opened else { throw NativeSmokeError(description: "No native audio reader") }
    defer { ExtAudioFileDispose(file) }
    var source = AudioStreamBasicDescription()
    var size = UInt32(MemoryLayout.size(ofValue: source))
    try check(ExtAudioFileGetProperty(file, kExtAudioFileProperty_FileDataFormat, &size, &source), "source format")
    var pcm = AudioStreamBasicDescription(mSampleRate: 48_000,
        mFormatID: kAudioFormatLinearPCM, mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
        mBytesPerPacket: 8, mFramesPerPacket: 1, mBytesPerFrame: 8,
        mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)
    try check(ExtAudioFileSetProperty(file, kExtAudioFileProperty_ClientDataFormat,
        UInt32(MemoryLayout.size(ofValue: pcm)), &pcm), "PCM output format")
    let capacity: UInt32 = 4096
    var samples = [Float](repeating: 0, count: Int(capacity) * 2)
    var totalFrames = 0
    var energy: Double = 0
    var maxAmplitude: Float = 0
    while true {
        var frameCount = capacity
        try samples.withUnsafeMutableBytes { raw in
            var list = AudioBufferList(mNumberBuffers: 1,
                mBuffers: AudioBuffer(mNumberChannels: 2, mDataByteSize: UInt32(raw.count), mData: raw.baseAddress))
            try check(ExtAudioFileRead(file, &frameCount, &list), "decode PCM")
        }
        if frameCount == 0 { break }
        totalFrames += Int(frameCount)
        for value in samples.prefix(Int(frameCount) * 2) {
            guard value.isFinite else { throw NativeSmokeError(description: "Nonfinite PCM") }
            energy += Double(value) * Double(value)
            maxAmplitude = max(maxAmplitude, abs(value))
        }
        guard totalFrames <= 150_000 else { throw NativeSmokeError(description: "Decoded duration exceeds fixture") }
    }
    guard totalFrames == 144_000 else {
        throw NativeSmokeError(description: "Expected exactly 144000 stereo PCM frames at 48kHz (3s), got \(totalFrames)")
    }
    let rms = sqrt(energy / Double(totalFrames * 2))
    guard rms > 0.01, maxAmplitude > 0.05 else { throw NativeSmokeError(description: "Tone decoded as silence") }
    print("PASS \(url.lastPathComponent): native source \(Int(source.mSampleRate))Hz/\(source.mChannelsPerFrame)ch/\(source.mFramesPerPacket)fpp -> native PCM 48000Hz/2ch, \(totalFrames)frames, 3.000s, RMS \(String(format: "%.5f", rms)), peak \(String(format: "%.5f", maxAmplitude))")
}

do {
    let directory = CommandLine.arguments.dropFirst().first.map { URL(fileURLWithPath: $0, isDirectory: true) }
        ?? URL(fileURLWithPath: #filePath).deletingLastPathComponent().appendingPathComponent("Fixtures", isDirectory: true)
    for name in ["Luma-HEAACv1.m4a", "Luma-HEAACv2.m4a"] {
        try decode(directory.appendingPathComponent(name))
    }
} catch {
    fputs("HE-AAC native smoke failed: \(error)\n", stderr)
    exit(1)
}
