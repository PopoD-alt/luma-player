import AppKit
import AVFoundation
import Foundation

private func pgsFail(_ message: String) -> NSError {
    NSError(domain: "PGSPlaybackSmoke", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
}

/// Original bounded EBML fixture editor. Copies every existing TrackEntry and
/// Cluster byte-for-byte, adds track 4, then appends a timestamp-zero PGS Cluster.
/// The source is our generated, index-free Luma-AVC-AAC.mkv, never user media.
private enum PGSFixtureMux {
    private struct Element {
        let id: UInt64
        let start: Int
        let body: Range<Int>
        let end: Int
    }
    private static func integer(_ value: UInt64) -> Data {
        var bytes = Data(), started = false
        for shift in stride(from: 56, through: 0, by: -8) {
            let byte = UInt8(truncatingIfNeeded: value >> shift)
            if byte != 0 || started || shift == 0 { bytes.append(byte); started = true }
        }
        return bytes
    }
    private static func size(_ value: Int) -> Data {
        for width in 1...8 where UInt64(value) < (UInt64(1) << (width * 7)) - 1 {
            let encoded = UInt64(value) | (UInt64(1) << (width * 7))
            return Data((0..<width).reversed().map { UInt8(truncatingIfNeeded: encoded >> ($0 * 8)) })
        }
        preconditionFailure("Generated fixture exceeds EBML bounds")
    }
    private static func element(_ id: UInt64, _ body: Data) -> Data { integer(id) + size(body.count) + body }
    private static func number(_ id: UInt64, _ value: UInt64) -> Data { element(id,integer(value)) }
    private static func text(_ id: UInt64, _ value: String) -> Data { element(id,Data(value.utf8)) }
    private static func vint(_ bytes: [UInt8], at offset: Int, limit: Int, identifier: Bool) throws -> (UInt64,Int,Bool) {
        guard offset >= 0, offset < limit, limit <= bytes.count, bytes[offset] != 0 else { throw pgsFail("Invalid generated EBML VINT") }
        var width = 1, marker: UInt8 = 0x80
        while bytes[offset] & marker == 0 { width += 1; marker >>= 1 }
        guard width <= (identifier ? 4 : 8), width <= limit - offset else { throw pgsFail("Truncated generated EBML VINT") }
        var value = UInt64(identifier ? bytes[offset] : bytes[offset] & (marker - 1))
        for index in 1..<width { value = value * 256 + UInt64(bytes[offset + index]) }
        return (value,width,!identifier && value == (UInt64(1) << (7 * width)) - 1)
    }
    private static func elements(_ bytes: [UInt8], in range: Range<Int>) throws -> [Element] {
        var result: [Element] = [], offset = range.lowerBound
        while offset < range.upperBound {
            let (id,idWidth,_) = try vint(bytes,at:offset,limit:range.upperBound,identifier:true)
            let (length,sizeWidth,unknown) = try vint(bytes,at:offset + idWidth,limit:range.upperBound,identifier:false)
            let start = offset + idWidth + sizeWidth
            guard !unknown || id == 0x18538067 else { throw pgsFail("Fixture editor supports unknown size only for Segment") }
            guard unknown || length <= UInt64(range.upperBound - start) else { throw pgsFail("Generated EBML extent exceeds parent") }
            let end = unknown ? range.upperBound : start + Int(length)
            result.append(Element(id:id,start:offset,body:start..<end,end:end)); offset = end
        }
        return result
    }
    static func make(base: Data, sup: Data) throws -> Data {
        let bytes = Array(base), top = try elements(bytes,in:0..<base.count)
        guard let header = top.first, header.id == 0x1a45dfa3,
              let segment = top.first(where: { $0.id == 0x18538067 }) else { throw pgsFail("Original Matroska fixture structure changed") }
        let children = try elements(bytes,in:segment.body)
        guard children.filter({ $0.id == 0x1654ae6b }).count == 1,
              !children.contains(where: { [0x114d9b74,0x1c53bb6b].contains($0.id) }) else { throw pgsFail("Fixture must have one Tracks element and no position-dependent indexes") }
        let subtitle = element(0xae,number(0xd7,4) + number(0x73c5,4) + number(0x83,17)
            + number(0xb9,1) + number(0x88,0) + number(0x55aa,0)
            + text(0x86,"S_HDMV/PGS") + text(0x536e,"Original LUMA PGS")
            + text(0x22b59c,"eng") + text(0x22b59d,"en"))
        var payload = Data(), scale: UInt64 = 1_000_000
        for child in children {
            if child.id == 0x1549a966 {
                for info in try elements(bytes,in:child.body) where info.id == 0x2ad7b1 {
                    guard (1...8).contains(info.body.count) else { throw pgsFail("Fixture TimestampScale is invalid") }
                    scale = bytes[info.body].reduce(UInt64(0)) { $0 * 256 + UInt64($1) }
                }
            }
            if child.id == 0x1654ae6b {
                for track in try elements(bytes,in:child.body) where track.id == 0xae {
                    for field in try elements(bytes,in:track.body) where field.id == 0xd7 {
                        let number = bytes[field.body].reduce(UInt64(0)) { $0 * 256 + UInt64($1) }
                        guard number != 4 else { throw pgsFail("Original fixture already contains track 4") }
                    }
                }
                payload += element(child.id,Data(bytes[child.body]) + subtitle)
            } else { payload += Data(bytes[child.start..<child.end]) }
        }
        guard scale > 0 else { throw pgsFail("Fixture TimestampScale must be positive") }
        let graphics = Array(sup)
        var offset = 0, blocks = Data(), count = 0
        while offset < graphics.count {
            guard graphics.count - offset >= 13, graphics[offset] == 0x50, graphics[offset + 1] == 0x47 else { throw pgsFail("Original PGS fixture has a truncated SUP header") }
            let pts = graphics[offset + 2..<offset + 6].reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
            let length = Int(graphics[offset + 11]) * 256 + Int(graphics[offset + 12])
            guard length <= graphics.count - offset - 13 else { throw pgsFail("Original PGS fixture has a truncated segment") }
            // Existing media starts video at .25s; remux normalizes that to zero.
            let ticks = ((Double(pts) / 90_000 + 0.25) * 1_000_000_000 / Double(scale)).rounded()
            guard ticks >= Double(Int16.min), ticks <= Double(Int16.max) else { throw pgsFail("Original PGS fixture exceeds this single-cluster timestamp range") }
            let relative = UInt16(bitPattern:Int16(ticks))
            let rawSegment = Data(graphics[offset + 10..<offset + 13 + length])
            let block = Data([0x84,UInt8(relative >> 8),UInt8(relative & 255),0]) + rawSegment
            blocks += element(0xa3,block)
            count += 1; offset += 13 + length
        }
        guard count > 0 else { throw pgsFail("Original PGS fixture is empty") }
        payload += element(0x1f43b675,number(0xe7,0) + blocks)
        return Data(bytes[header.start..<header.end]) + integer(0x18538067)
            + Data([0x01,0xff,0xff,0xff,0xff,0xff,0xff,0xff]) + payload
    }
}

@main struct PGSPlaybackSmoke {
    @MainActor static func main() {
        setbuf(stdout,nil)
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        Task { @MainActor in
            do { try await run(); exit(0) }
            catch { print("FAIL: \(error)"); exit(1) }
        }
        app.run()
    }
    @MainActor private static func run() async throws {
        let root = URL(fileURLWithPath:CommandLine.arguments[1],isDirectory:true)
        let generated = root.appendingPathComponent("Luma-PGS.mkv")
        let sup = try Data(contentsOf:root.appendingPathComponent("Luma-PGS.sup"))
        var reference = OriginalPGSDecoder()
        let events = try reference.consumeSUP(sup)
        try reference.finish()
        guard events.count == 3, events[0].bitmaps.count == 1, events[1].bitmaps.count == 1, events[2].isClear else { throw pgsFail("Original PGS fixture manifest changed") }
        let muxed = try PGSFixtureMux.make(base:Data(contentsOf:root.appendingPathComponent("Luma-AVC-AAC.mkv")),sup:sup)
        try muxed.write(to:generated,options:.atomic)

        let window = NSWindow(contentRect:NSRect(x:100,y:100,width:640,height:360),styleMask:[.titled,.resizable],backing:.buffered,defer:false)
        window.title = "Luma PGS integration smoke"
        window.isReleasedWhenClosed = false
        let host = NSView(frame:NSRect(x:0,y:0,width:640,height:360))
        window.contentView = host; window.orderFront(nil)
        let engine = PlaybackEngine()
        engine.setVolume(0); engine.attachVideoView(host)
        defer { engine.shutdown(); window.close() }
        guard let output = host.subviews.first as? PlaybackCanvas else { throw pgsFail("No production PlaybackCanvas") }
        engine.load(generated)
        try await wait(engine) { engine.isPlaying && engine.duration > 0 && output.movieLayer.isReadyForDisplay && !output.movieLayer.videoRect.isEmpty }
        engine.pause()
        guard let track = engine.subtitleTracks.first(where: { $0.id >= 3_000_000 }),
              track.name.contains("Original LUMA PGS"), engine.selectedSubtitleTrack == -1,
              output.bitmapLayer.sublayers?.isEmpty != false else { throw pgsFail("Nondefault PGS track menu/initial Off state is incorrect") }
        guard let asset = engine.player.currentItem?.asset,
              try await asset.loadTracks(withMediaType:.video).count == 1,
              try await asset.loadTracks(withMediaType:.audio).count == 1 else { throw pgsFail("PGS injection changed the original media tracks") }
        engine.setSubtitleTrack(track.id)
        try await seek(engine,to:1.5)
        try await wait(engine) { imageBytes(output) == events[0].bitmaps[0].rgba }
        try placement(output,bitmap:events[0].bitmaps[0],canvas:events[0])
        guard output.caption.stringValue.isEmpty, engine.subtitleRenderingNote?.contains("PGS") == true else { throw pgsFail("PGS selection did not choose bitmap rendering") }
        print("PASS: production Matroska PGS menu/Off/default metadata, native A/V retained, paused seek displays one exact RGBA CGImage at the top-left canvas placement")

        let white = imageBytes(output)
        try await seek(engine,to:3)
        try await wait(engine) { imageBytes(output) == events[1].bitmaps[0].rgba }
        guard imageBytes(output) != white else { throw pgsFail("Palette change did not reach the production CGImage") }
        try await seek(engine,to:4.5)
        try await wait(engine) { output.bitmapLayer.sublayers?.isEmpty != false }
        try await seek(engine,to:1.5)
        try await wait(engine) { imageBytes(output) == events[0].bitmaps[0].rgba }
        engine.setSubtitleTrack(-1)
        guard output.bitmapLayer.sublayers?.isEmpty != false else { throw pgsFail("Off left bitmap layers visible") }
        engine.setSubtitleTrack(track.id)
        try await wait(engine) { imageBytes(output) == events[0].bitmaps[0].rgba }
        print("PASS: palette update replaces the bitmap, clear event removes it, backward seek restores it, Off clears it and selecting the track restores the paused composition")

        engine.setSubtitleDelay(1)
        try await wait(engine) { output.bitmapLayer.sublayers?.isEmpty != false }
        try await seek(engine,to:2.5)
        try await wait(engine) { imageBytes(output) == events[0].bitmaps[0].rgba }
        engine.setSubtitleDelay(-1)
        try await wait(engine) { imageBytes(output) == events[1].bitmaps[0].rgba }
        engine.setSubtitleDelay(0)
        try await seek(engine,to:3)
        try await wait(engine) { imageBytes(output) == events[1].bitmaps[0].rgba }
        let oldFrame = output.bitmapLayer.sublayers!.first!.frame
        window.setContentSize(NSSize(width:900,height:650))
        try await wait(engine) { output.bounds.width == 900 && output.bounds.height == 650
            && output.movieLayer.frame == output.bounds && output.movieLayer.videoRect.width > 899
            && output.movieLayer.videoRect.height < output.bounds.height - 20 }
        try await wait(engine) { placementMatches(output,bitmap:events[1].bitmaps[0],canvas:events[1]) }
        guard output.bitmapLayer.sublayers!.first!.frame != oldFrame,
              imageBytes(output) == events[1].bitmaps[0].rgba else {
            throw pgsFail("Resize result: oldFrame=\(oldFrame), newFrame=\(output.bitmapLayer.sublayers!.first!.frame), canvas=\(output.bounds), video=\(output.movieLayer.videoRect), pixelsMatch=\(imageBytes(output) == events[1].bitmaps[0].rgba)")
        }
        try placement(output,bitmap:events[1].bitmaps[0],canvas:events[1])
        engine.unload()
        guard engine.subtitleTracks.isEmpty, output.bitmapLayer.sublayers?.isEmpty != false else { throw pgsFail("Unload left PGS menus or bitmap layers behind") }
        print("PASS: positive/negative subtitle delay while paused, aspect-fit letterbox resize tracks AVPlayerLayer.videoRect, unload removes bitmap state")
        print("Saved original PGS Matroska fixture: \(generated.path)")
    }
    @MainActor private static func imageBytes(_ output: PlaybackCanvas) -> Data? {
        guard let layers = output.bitmapLayer.sublayers, layers.count == 1, let contents = layers[0].contents else { return nil }
        let image = contents as! CGImage
        guard image.width == 92, image.height == 28, image.alphaInfo == .last,
              let bytes = image.dataProvider?.data else { return nil }
        return bytes as Data
    }
    @MainActor private static func expectedFrame(_ output: PlaybackCanvas,bitmap:PGSBitmap,canvas:PGSDisplayEvent) -> CGRect {
        let video = output.movieLayer.videoRect
        let sx = video.width / CGFloat(canvas.canvasWidth), sy = video.height / CGFloat(canvas.canvasHeight)
        return CGRect(x:video.minX + CGFloat(bitmap.x) * sx,y:video.minY + CGFloat(canvas.canvasHeight - bitmap.y - bitmap.height) * sy,
            width:CGFloat(bitmap.width) * sx,height:CGFloat(bitmap.height) * sy)
    }
    @MainActor private static func placementMatches(_ output: PlaybackCanvas,bitmap:PGSBitmap,canvas:PGSDisplayEvent) -> Bool {
        guard let layers = output.bitmapLayer.sublayers, layers.count == 1, !output.movieLayer.videoRect.isEmpty else { return false }
        let actual = layers[0].frame, expected = expectedFrame(output,bitmap:bitmap,canvas:canvas)
        return abs(actual.minX - expected.minX) < 0.25 && abs(actual.minY - expected.minY) < 0.25
            && abs(actual.width - expected.width) < 0.25 && abs(actual.height - expected.height) < 0.25
    }
    @MainActor private static func placement(_ output: PlaybackCanvas,bitmap:PGSBitmap,canvas:PGSDisplayEvent) throws {
        guard placementMatches(output,bitmap:bitmap,canvas:canvas) else { throw pgsFail("Bitmap placement does not follow the native video's aspect-fit rectangle") }
    }
    @MainActor private static func seek(_ engine:PlaybackEngine,to time:Double) async throws {
        engine.pause(); engine.seek(to:time)
        try await wait(engine) { engine.state == "paused" && abs(engine.player.currentTime().seconds - time) < 0.02 }
        // Let the production 40ms subtitle poll observe the completed native seek.
        try await Task.sleep(for:.milliseconds(100))
    }
    @MainActor private static func wait(_ engine:PlaybackEngine,_ condition:() -> Bool) async throws {
        for _ in 0..<500 {
            if let error = engine.errorMessage { throw pgsFail(error) }
            if condition() { return }
            try await Task.sleep(for:.milliseconds(40))
        }
        throw pgsFail("Timed out waiting for the production native/PGS rendering state")
    }
}
