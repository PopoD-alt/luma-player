#!/usr/bin/env swift
// Generates original synthetic fixtures using only macOS system frameworks.
// Run: swift Tests/GenerateFixture.swift [output-directory]
import AVFoundation
import CoreGraphics
import CoreText
import CoreVideo
import Foundation

let width = 960
let height = 540
let fps: Int32 = 30
let duration = 12
let outputDirectory = CommandLine.arguments.dropFirst().first.map {
    URL(fileURLWithPath: $0, isDirectory: true)
} ?? URL(fileURLWithPath: #filePath).deletingLastPathComponent()
    .appendingPathComponent("Fixtures", isDirectory: true)
try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)

func fail(_ message: String) -> Never {
    fputs("Fixture generation failed: \(message)\n", stderr)
    exit(1)
}

func color(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: r, green: g, blue: b, alpha: a)
}

func text(_ text: String, at point: CGPoint, size: CGFloat, color: CGColor, in context: CGContext) {
    let font = CTFontCreateWithName("HelveticaNeue-Bold" as CFString, size, nil)
    let attributes: [NSAttributedString.Key: Any] = [
        NSAttributedString.Key(kCTFontAttributeName as String): font,
        NSAttributedString.Key(kCTForegroundColorAttributeName as String): color,
    ]
    let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attributes))
    context.textMatrix = .identity
    context.textPosition = point
    CTLineDraw(line, context)
}

let videoURL = outputDirectory.appendingPathComponent("Luma-Demo.mp4")
if FileManager.default.fileExists(atPath: videoURL.path) {
    try FileManager.default.removeItem(at: videoURL)
}
let writer = try AVAssetWriter(outputURL: videoURL, fileType: .mp4)
let settings: [String: Any] = [
    AVVideoCodecKey: AVVideoCodecType.h264,
    AVVideoWidthKey: width,
    AVVideoHeightKey: height,
    AVVideoCompressionPropertiesKey: [
        AVVideoAverageBitRateKey: 2_500_000,
        AVVideoProfileLevelKey: AVVideoProfileLevelH264MainAutoLevel,
        AVVideoMaxKeyFrameIntervalKey: fps,
        AVVideoAllowFrameReorderingKey: false,
    ],
]
let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
input.expectsMediaDataInRealTime = false
let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
    kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
    kCVPixelBufferWidthKey as String: width,
    kCVPixelBufferHeightKey as String: height,
    kCVPixelBufferCGImageCompatibilityKey as String: true,
    kCVPixelBufferCGBitmapContextCompatibilityKey as String: true,
])
guard writer.canAdd(input) else { fail("H.264 writer input is unavailable") }
writer.add(input)
guard writer.startWriting() else { fail(writer.error?.localizedDescription ?? "Cannot start video writer") }
writer.startSession(atSourceTime: .zero)
guard let pool = adaptor.pixelBufferPool else { fail("Cannot create pixel buffer pool") }
let space = CGColorSpaceCreateDeviceRGB()

for frame in 0..<(duration * Int(fps)) {
    while !input.isReadyForMoreMediaData {
        if writer.status == .failed { fail(writer.error?.localizedDescription ?? "Video writer failed") }
        Thread.sleep(forTimeInterval: 0.002)
    }
    autoreleasepool {
        var pixelBuffer: CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pixelBuffer) == kCVReturnSuccess,
              let buffer = pixelBuffer else { fail("Cannot allocate video frame") }
        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }
        guard let context = CGContext(
            data: CVPixelBufferGetBaseAddress(buffer), width: width, height: height,
            bitsPerComponent: 8, bytesPerRow: CVPixelBufferGetBytesPerRow(buffer),
            space: space,
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else { fail("Cannot create drawing context") }
        let t = Double(frame) / Double(fps)
        let phase = t / Double(duration) * 2 * Double.pi
        let gradientColors = [color(0.035, 0.055, 0.11), color(0.16, 0.09, 0.29), color(0.045, 0.21, 0.25)]
        let gradient = CGGradient(colorsSpace: space, colors: gradientColors as CFArray, locations: [0, 0.52, 1])!
        context.drawLinearGradient(gradient, start: CGPoint(x: 0, y: 0), end: CGPoint(x: width, y: height), options: [])

        // A moving luminous orb and orbit lines make pause/seek changes obvious.
        let orb = CGPoint(x: 690 + 95 * cos(phase), y: 275 + 105 * sin(phase))
        context.setStrokeColor(color(0.6, 0.78, 1, 0.12))
        context.setLineWidth(1)
        for radius in stride(from: 120, through: 190, by: 35) {
            let r = CGFloat(radius)
            context.strokeEllipse(in: CGRect(x: 690 - r, y: 275 - r, width: r * 2, height: r * 2))
        }
        for radius in stride(from: 105, through: 55, by: -5) {
            let r = CGFloat(radius)
            context.setFillColor(color(0.32, 0.65, 1, 0.025))
            context.fillEllipse(in: CGRect(x: orb.x - r, y: orb.y - r, width: r * 2, height: r * 2))
        }
        let orbGradient = CGGradient(colorsSpace: space,
            colors: [color(0.5, 0.85, 1), color(0.31, 0.4, 0.95), color(0.5, 0.22, 0.72)] as CFArray,
            locations: [0, 0.65, 1])!
        context.saveGState()
        context.addEllipse(in: CGRect(x: orb.x - 56, y: orb.y - 56, width: 112, height: 112))
        context.clip()
        context.drawLinearGradient(orbGradient, start: CGPoint(x: orb.x - 50, y: orb.y + 55), end: CGPoint(x: orb.x + 60, y: orb.y - 55), options: [])
        context.restoreGState()

        context.setFillColor(color(0.8, 0.96, 0.84, 0.8))
        context.fill(CGRect(x: 75 + 18 * sin(phase * 2), y: 348, width: 34, height: 5))
        text("LUMA PLAYER", at: CGPoint(x: 72, y: 287), size: 44, color: color(0.96, 0.97, 1), in: context)
        text("Playback check", at: CGPoint(x: 75, y: 246), size: 22, color: color(0.67, 0.73, 0.84), in: context)
        text("ORIGINAL SYNTHETIC MEDIA", at: CGPoint(x: 75, y: 204), size: 11, color: color(0.49, 0.57, 0.7), in: context)
        text(String(format: "%02d:%02d  /  00:12", Int(t) / 60, Int(t) % 60), at: CGPoint(x: 75, y: 106), size: 16, color: color(0.85, 0.89, 0.97), in: context)
        text(String(format: "FRAME %03d  ·  960 × 540  ·  H.264 / 30 FPS", frame), at: CGPoint(x: 75, y: 74), size: 11, color: color(0.49, 0.57, 0.7), in: context)
        context.setFillColor(color(1, 1, 1, 0.13))
        context.fill(CGRect(x: 75, y: 49, width: 810, height: 3))
        context.setFillColor(color(0.61, 0.72, 1))
        context.fill(CGRect(x: 75, y: 49, width: 810 * t / Double(duration), height: 3))

        guard adaptor.append(buffer, withPresentationTime: CMTime(value: Int64(frame), timescale: fps)) else {
            fail(writer.error?.localizedDescription ?? "Cannot append video frame")
        }
    }
}
input.markAsFinished()
let completed = DispatchSemaphore(value: 0)
writer.finishWriting { completed.signal() }
completed.wait()
guard writer.status == .completed else { fail(writer.error?.localizedDescription ?? "Video finalization failed") }

// Separate audio fixture: three seconds of quiet 440 Hz PCM with fade in/out.
func appendLE<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
    var littleEndian = value.littleEndian
    withUnsafeBytes(of: &littleEndian) { data.append(contentsOf: $0) }
}
let sampleRate: UInt32 = 48_000
let sampleCount = Int(sampleRate) * 3
let audioBytes = UInt32(sampleCount * 2)
var wav = Data("RIFF".utf8)
appendLE(audioBytes + 36, to: &wav)
wav.append(Data("WAVEfmt ".utf8))
appendLE(UInt32(16), to: &wav)
appendLE(UInt16(1), to: &wav)
appendLE(UInt16(1), to: &wav)
appendLE(sampleRate, to: &wav)
appendLE(sampleRate * 2, to: &wav)
appendLE(UInt16(2), to: &wav)
appendLE(UInt16(16), to: &wav)
wav.append(Data("data".utf8))
appendLE(audioBytes, to: &wav)
for sample in 0..<sampleCount {
    let t = Double(sample) / Double(sampleRate)
    let fade = min(1, min(t / 0.08, (3 - t) / 0.08))
    let value = Int16(sin(t * 2 * Double.pi * 440) * 32767 * 0.12 * fade)
    appendLE(value, to: &wav)
}
let audioURL = outputDirectory.appendingPathComponent("Luma-Tone.wav")
try wav.write(to: audioURL)
print("Generated \(videoURL.path) (12s, 960×540, H.264, 30fps)")
print("Generated \(audioURL.path) (3s, mono 48kHz PCM, quiet 440Hz tone)")
