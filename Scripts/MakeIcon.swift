import AppKit
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

// Original Luma artwork, drawn locally without external assets.
// Run: xcrun swift Scripts/MakeIcon.swift build
let projectRoot = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
let buildURL: URL
if let path = CommandLine.arguments.dropFirst().first {
    buildURL = URL(fileURLWithPath: path, relativeTo: URL(fileURLWithPath: FileManager.default.currentDirectoryPath)).standardizedFileURL
} else {
    buildURL = projectRoot.appendingPathComponent("build", isDirectory: true)
}
let iconsetURL = buildURL.appendingPathComponent("Luma.iconset", isDirectory: true)
try FileManager.default.createDirectory(at: iconsetURL, withIntermediateDirectories: true)

func color(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: r / 255, green: g / 255, blue: b / 255, alpha: a)
}

func trianglePath() -> CGPath {
    let vertices = [CGPoint(x: 400, y: 302), CGPoint(x: 740, y: 512), CGPoint(x: 400, y: 722)]
    let path = CGMutablePath()
    let rounding: CGFloat = 43
    for index in vertices.indices {
        let previous = vertices[(index + vertices.count - 1) % vertices.count]
        let vertex = vertices[index]
        let next = vertices[(index + 1) % vertices.count]
        let beforeLength = hypot(previous.x - vertex.x, previous.y - vertex.y)
        let afterLength = hypot(next.x - vertex.x, next.y - vertex.y)
        let before = CGPoint(x: vertex.x + (previous.x - vertex.x) * rounding / beforeLength,
                             y: vertex.y + (previous.y - vertex.y) * rounding / beforeLength)
        let after = CGPoint(x: vertex.x + (next.x - vertex.x) * rounding / afterLength,
                            y: vertex.y + (next.y - vertex.y) * rounding / afterLength)
        if index == 0 { path.move(to: before) } else { path.addLine(to: before) }
        path.addQuadCurve(to: after, control: vertex)
    }
    path.closeSubpath()
    return path
}

func writeIcon(size: Int, to url: URL) throws {
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    guard let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8,
                                  bytesPerRow: size * 4, space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
        throw NSError(domain: "LumaIcon", code: 1, userInfo: [NSLocalizedDescriptionKey: "Could not create icon bitmap."])
    }
    context.scaleBy(x: CGFloat(size) / 1024, y: CGFloat(size) / 1024)
    let face = CGPath(roundedRect: CGRect(x: 64, y: 64, width: 896, height: 896),
                      cornerWidth: 207, cornerHeight: 207, transform: nil)

    // A midnight enamel tile with a restrained lower shadow.
    context.saveGState()
    context.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: color(0, 0, 0, 0.36))
    context.addPath(face)
    context.setFillColor(color(16, 22, 32))
    context.fillPath()
    context.restoreGState()

    context.saveGState()
    context.addPath(face)
    context.clip()
    let faceGradient = CGGradient(colorsSpace: space,
                                  colors: [color(30, 39, 54), color(12, 17, 26)] as CFArray,
                                  locations: [0, 1])!
    context.drawLinearGradient(faceGradient, start: CGPoint(x: 240, y: 950),
                               end: CGPoint(x: 800, y: 70), options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    let halo = CGGradient(colorsSpace: space,
                          colors: [color(90, 170, 255, 0.13), color(90, 170, 255, 0)] as CFArray,
                          locations: [0, 1])!
    context.drawRadialGradient(halo, startCenter: CGPoint(x: 525, y: 515), startRadius: 0,
                               endCenter: CGPoint(x: 525, y: 515), endRadius: 370, options: [])
    context.restoreGState()

    context.addPath(CGPath(roundedRect: CGRect(x: 66, y: 66, width: 892, height: 892),
                           cornerWidth: 205, cornerHeight: 205, transform: nil))
    context.setStrokeColor(color(203, 218, 241, 0.13))
    context.setLineWidth(3)
    context.strokePath()

    // The luminous mark uses a rounded geometric play shape, not VLC's cone.
    let mark = trianglePath()
    context.saveGState()
    context.setShadow(offset: CGSize(width: 0, height: -13), blur: 33, color: color(40, 120, 255, 0.24))
    context.addPath(mark)
    context.setFillColor(color(120, 195, 255))
    context.fillPath()
    context.restoreGState()
    context.saveGState()
    context.addPath(mark)
    context.clip()
    let markGradient = CGGradient(colorsSpace: space,
                                  colors: [color(220, 240, 255), color(95, 175, 255), color(45, 100, 230)] as CFArray,
                                  locations: [0, 0.62, 1])!
    context.drawLinearGradient(markGradient, start: CGPoint(x: 380, y: 735),
                               end: CGPoint(x: 675, y: 275), options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    context.restoreGState()
    context.addPath(mark)
    context.setLineWidth(2)
    context.setStrokeColor(color(225, 245, 255, 0.36))
    context.strokePath()

    guard let image = context.makeImage(),
          let destination = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
        throw NSError(domain: "LumaIcon", code: 2, userInfo: [NSLocalizedDescriptionKey: "Could not create PNG output."])
    }
    CGImageDestinationAddImage(destination, image, nil)
    guard CGImageDestinationFinalize(destination) else {
        throw NSError(domain: "LumaIcon", code: 3, userInfo: [NSLocalizedDescriptionKey: "Could not finish PNG output."])
    }
}

for pointSize in [16, 32, 128, 256, 512] {
    try writeIcon(size: pointSize, to: iconsetURL.appendingPathComponent("icon_\(pointSize)x\(pointSize).png"))
    try writeIcon(size: pointSize * 2, to: iconsetURL.appendingPathComponent("icon_\(pointSize)x\(pointSize)@2x.png"))
}
let icnsURL = buildURL.appendingPathComponent("Luma.icns")
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["--convert", "icns", "--output", icnsURL.path, iconsetURL.path]
try iconutil.run()
iconutil.waitUntilExit()
if iconutil.terminationStatus != 0 {
    // Some constrained macOS hosts reject iconutil's image-loader operation.
    // ICNS also supports native PNG elements; retain every generated resolution.
    func bigEndian(_ value: UInt32) -> Data {
        var value = value.bigEndian
        return withUnsafeBytes(of: &value) { Data($0) }
    }
    let elements = [
        ("icp4", "icon_16x16.png"), ("ic11", "icon_16x16@2x.png"),
        ("icp5", "icon_32x32.png"), ("ic12", "icon_32x32@2x.png"),
        ("ic07", "icon_128x128.png"), ("ic13", "icon_128x128@2x.png"),
        ("ic08", "icon_256x256.png"), ("ic14", "icon_256x256@2x.png"),
        ("ic09", "icon_512x512.png"), ("ic10", "icon_512x512@2x.png")
    ]
    var body = Data()
    for (type, filename) in elements {
        let image = try Data(contentsOf: iconsetURL.appendingPathComponent(filename))
        body.append(Data(type.utf8))
        body.append(bigEndian(UInt32(image.count + 8)))
        body.append(image)
    }
    var container = Data("icns".utf8)
    container.append(bigEndian(UInt32(body.count + 8)))
    container.append(body)
    try container.write(to: icnsURL, options: .atomic)
    print("Used PNG-backed ICNS fallback.")
}
guard let source = CGImageSourceCreateWithURL(icnsURL as CFURL, nil), CGImageSourceGetCount(source) >= 1,
      let _ = CGImageSourceCreateImageAtIndex(source, CGImageSourceGetCount(source) - 1, nil) else {
    throw NSError(domain: "LumaIcon", code: 4, userInfo: [NSLocalizedDescriptionKey: "The generated ICNS could not be decoded."])
}
print("Created \(icnsURL.path)")
