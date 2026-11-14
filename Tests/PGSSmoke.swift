import Foundation

private enum Fixture {
    static func be(_ value: Int, _ count: Int = 2) -> Data {
        Data((0..<count).reversed().map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) })
    }
    static func segment(_ type: UInt8, _ data: Data = Data()) -> Data {
        precondition(data.count <= 65535)
        return Data([type]) + be(data.count) + data
    }
    static func sup(_ packet: Data, time: Double) -> Data {
        var result = Data(), offset = 0
        while offset < packet.count {
            let size = Int(packet[offset + 1]) * 256 + Int(packet[offset + 2])
            result += Data([0x50, 0x47]) + be(Int((time * 90_000).rounded()), 4) + be(0, 4) + packet[offset..<offset + 3 + size]
            offset += 3 + size
        }
        return result
    }
    static func ref(_ id: Int = 7, _ x: Int = 10, _ y: Int = 20, window: UInt8 = 0, forced: Bool = false, crop: [Int]? = nil) -> Data {
        var result = be(id) + Data([window, (forced ? 0x40 : 0) | (crop == nil ? 0 : 0x80)]) + be(x) + be(y)
        if let crop { precondition(crop.count == 4); for value in crop { result += be(value) } }
        return result
    }
    static func pcs(_ references: [Data], state: UInt8 = 0x80, paletteOnly: Bool = false, width: Int = 1920, height: Int = 1080) -> Data {
        segment(0x16, be(width) + be(height) + Data([0x10]) + be(0) + Data([state, paletteOnly ? 0x80 : 0, 0, UInt8(references.count)]) + references.reduce(Data(), +))
    }
    static func wds(_ windows: [(UInt8, Int, Int, Int, Int)] = [(0, 10, 20, 140, 2)]) -> Data {
        var result = Data([UInt8(windows.count)])
        for (id, x, y, width, height) in windows { result += Data([id]) + be(x) + be(y) + be(width) + be(height) }
        return segment(0x17, result)
    }
    static func palette(_ entries: Data = Data([0,16,128,128,0, 1,235,128,128,255, 2,16,128,128,128, 3,81,240,90,200]), version: UInt8 = 0) -> Data {
        segment(0x14, Data([0, version]) + entries)
    }
    static func ods(_ rle: Data, width: Int = 140, height: Int = 2, id: Int = 7, version: UInt8 = 0) -> Data {
        segment(0x15, be(id) + Data([version, 0xc0]) + be(rle.count + 4, 3) + be(width) + be(height) + rle)
    }
    // Every run branch: literals; short and extended zero/colored runs; EOL.
    static let rle = Data([1,2,2, 0,3, 0,0x84,1, 0,0x40,65, 0,0xc0,65,2, 0,0,
                           0,0xc0,140,1, 0,0])
    static let end = segment(0x80)
    static func display(_ data: Data = rle, width: Int = 140, height: Int = 2) -> Data {
        pcs([ref(forced: true)]) + wds([(0,10,20,width,height)]) + palette() + ods(data, width: width, height: height) + end
    }
    static func originalDemo() -> Data {
        // Original hand-drawn 5×7 letter shapes; no downloaded/user content.
        let letters = [
            ["10000","10000","10000","10000","10000","10000","11111"],
            ["10001","10001","10001","10001","10001","10001","01110"],
            ["10001","11011","10101","10101","10001","10001","10001"],
            ["01110","10001","10001","11111","10001","10001","10001"]]
        var rle = Data()
        for row in 0..<7 {
            var line: [UInt8] = []
            for (index, letter) in letters.enumerated() {
                if index > 0 { line += [UInt8](repeating: 0, count: 4) }
                for bit in letter[row] { line += [UInt8](repeating: bit == "1" ? 1 : 0, count: 4) }
            }
            for _ in 0..<4 {
                var start = 0
                while start < line.count {
                    var end = start + 1
                    while end < line.count, line[end] == line[start] { end += 1 }
                    let count = end - start
                    if line[start] == 0 { rle += Data([0, UInt8(count)]) }
                    else { rle += Data([0, 0x80 | UInt8(count), 1]) }
                    start = end
                }
                rle += Data([0,0])
            }
        }
        let first = pcs([ref(7, 914, 940)]) + wds([(0,914,940,92,28)]) + palette() + ods(rle,width:92,height:28) + end
        let clear = pcs([],state:0) + end
        let recolor = pcs([],state:0,paletteOnly:true) + palette(Data([1,160,176,96,255]),version:1) + end
        return sup(first,time:1) + sup(recolor,time:2.5) + sup(clear,time:4)
    }
}

private func require(_ condition: @autoclosure () -> Bool, _ message: String) throws {
    if !condition() { throw NSError(domain: "PGSSmoke", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
}
private func rejects(_ message: String, _ body: () throws -> Void) throws {
    do { try body() } catch is PGSFailure { return }
    throw NSError(domain: "PGSSmoke", code: 1, userInfo: [NSLocalizedDescriptionKey: "Expected PGS rejection: " + message])
}
private func pixel(_ bitmap: PGSBitmap, _ x: Int, _ y: Int = 0) -> Data {
    let start = (y * bitmap.width + x) * 4
    return Data(bitmap.rgba[start..<start + 4])
}

@main struct PGSSmoke {
    static func main() async throws {
        var decoder = OriginalPGSDecoder()
        let first = try decoder.consumeMatroska(packet: Fixture.display(), timestamp: 1, duration: 3)
        try require(first.count == 1 && first[0].time == 1 && first[0].endTime == 4, "PCS timing/BlockDuration lost")
        let image = first[0].bitmaps[0]
        try require(image.x == 10 && image.y == 20 && image.width == 140 && image.height == 2 && image.isForced, "Placement/dimensions/forced flag lost")
        try require(image.rgba.count == 140 * 2 * 4, "Wrong RGBA extent")
        try require(pixel(image,0) == Data([255,255,255,255]), "White RGBA changed")
        try require(pixel(image,1) == Data([0,0,0,128]) && pixel(image,2) == Data([0,0,0,128]), "Literal indexed black/alpha changed")
        try require(pixel(image,3) == Data([0,0,0,0]) && pixel(image,5) == Data([0,0,0,0]), "Short zero run changed")
        try require(pixel(image,6) == Data([255,255,255,255]) && pixel(image,9) == Data([255,255,255,255]), "Short color run changed")
        try require(pixel(image,10) == Data([0,0,0,0]) && pixel(image,74) == Data([0,0,0,0]), "Extended zero run changed")
        try require(pixel(image,75) == Data([0,0,0,128]) && pixel(image,139) == Data([0,0,0,128]), "Extended color run changed")
        try require(pixel(image,139,1) == Data([255,255,255,255]), "Second scanline did not decode")
        try decoder.finish()
        print("PASS: original RLE all codeword branches, RGBA/alpha, placement, forced flag, PCS time and explicit BlockDuration")

        let update = Fixture.pcs([],state:0,paletteOnly:true) + Fixture.palette(Data([1,235,128,128,64]),version:1) + Fixture.end
        let faded = try decoder.consumeMatroska(packet: update, timestamp: 2)
        try require(pixel(faded[0].bitmaps[0],0) == Data([255,255,255,64]), "Palette-only update did not recolor cached object")
        try require(pixel(faded[0].bitmaps[0],1) == Data([0,0,0,128]), "Partial palette update lost old entries")
        let crop = Fixture.pcs([Fixture.ref(crop:[1,0,3,1])],state:0) + Fixture.end
        let cropped = try decoder.consumeMatroska(packet: crop,timestamp:2.5)[0].bitmaps[0]
        try require(cropped.x == 10 && cropped.width == 3 && cropped.height == 1 && pixel(cropped,0) == Data([0,0,0,128]) && pixel(cropped,2) == Data([0,0,0,0]), "Source cropping changed placement or pixels")
        let move = Fixture.pcs([Fixture.ref(7,12,20)],state:0x40) + Fixture.end
        let clipped = try decoder.consumeMatroska(packet:move,timestamp:3)[0].bitmaps[0]
        try require(clipped.x == 12 && clipped.width == 138, "Window clipping or acquisition cache reuse failed")
        let clear = try decoder.consumeMatroska(packet:Fixture.pcs([],state:0) + Fixture.end,timestamp:4)
        try require(clear.count == 1 && clear[0].isClear && clear[0].canvasWidth == 1920, "Clear composition not emitted")
        try decoder.finish()
        print("PASS: partial palette updates/fades, cache reuse, source crop, window clipping, acquisition and clear events")

        var fragmented = OriginalPGSDecoder()
        let split = 8
        let head = Fixture.segment(0x15,Fixture.be(7) + Data([0,0x80]) + Fixture.be(Fixture.rle.count + 4,3) + Fixture.be(140) + Fixture.be(2) + Fixture.rle.prefix(split))
        let tail = Fixture.segment(0x15,Fixture.be(7) + Data([0,0x40]) + Fixture.rle.dropFirst(split))
        let incomplete = try fragmented.consumeMatroska(packet:Fixture.pcs([Fixture.ref(forced:true)]) + Fixture.wds() + Fixture.palette() + head,timestamp:1,duration:3)
        try require(incomplete.isEmpty,"Fragmented object was emitted before END")
        let completed = try fragmented.consumeMatroska(packet:tail + Fixture.end,timestamp:1.1)
        try require(completed == first,"ODS fragmentation or END timestamp changed the display")
        try fragmented.finish()
        var sup = OriginalPGSDecoder()
        let wrapped = try sup.consumeSUP(Fixture.sup(Fixture.display(),time:1))
        try require(wrapped.count == 1 && wrapped[0].bitmaps == first[0].bitmaps && wrapped[0].time == 1 && wrapped[0].endTime == nil,"SUP wrapper decode differs from Matroska")
        try sup.finish()
        print("PASS: ODS fragments across packets preserve PCS time, and independent SUP PG timestamps match Matroska pixels")

        var two = OriginalPGSDecoder()
        let twoPacket = Fixture.pcs([Fixture.ref(),Fixture.ref(8,100,200,window:1)])
            + Fixture.wds([(0,10,20,140,2),(1,100,200,2,1)]) + Fixture.palette()
            + Fixture.ods(Fixture.rle) + Fixture.ods(Data([2,1,0,0]),width:2,height:1,id:8) + Fixture.end
        let twoEvent = try two.consumeMatroska(packet:twoPacket,timestamp:1)[0]
        try require(twoEvent.bitmaps.count == 2 && twoEvent.bitmaps[1].x == 100 && twoEvent.bitmaps[1].y == 200 && pixel(twoEvent.bitmaps[1],1) == Data([255,255,255,255]),"Two objects/windows not preserved")
        for (height, expected) in [(1080,Data([255,24,0,200])), (480,Data([254,0,0,200]))] {
            var color = OriginalPGSDecoder()
            let packet = Fixture.pcs([Fixture.ref()],height:height) + Fixture.wds([(0,10,20,1,1)]) + Fixture.palette() + Fixture.ods(Data([3,0,0]),width:1,height:1) + Fixture.end
            let bitmap = try color.consumeMatroska(packet:packet,timestamp:1)[0].bitmaps[0]
            try require(pixel(bitmap,0) == expected,"HD/SD limited-range color conversion changed")
        }
        print("PASS: two independent objects/windows and BT.709/BT.601 straight RGBA conversion")

        try rejects("SUP passed as Matroska") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Fixture.sup(Fixture.display(),time:1),timestamp:1) }
        try rejects("truncated wrapper") { var d = OriginalPGSDecoder(); _ = try d.consumeSUP(Data([0x50,0x47,0])) }
        try rejects("truncated segment") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Data([0x16,0,255,0]),timestamp:1) }
        try rejects("unfinished display set") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Fixture.pcs([]),timestamp:1); try d.finish() }
        try rejects("missing object or palette") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Fixture.pcs([Fixture.ref()]) + Fixture.end,timestamp:1) }
        try rejects("RLE overflow") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Fixture.display(Data([0,0xc0,141,1,0,0])),timestamp:1) }
        try rejects("RLE missing EOL") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Fixture.display(Data([1]),width:1,height:1),timestamp:1) }
        try rejects("undefined palette entry") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Fixture.display(Data([9,0,0]),width:1,height:1),timestamp:1) }
        try rejects("fragment without matching first") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Fixture.pcs([Fixture.ref()]) + tail,timestamp:1) }
        try rejects("oversized canvas") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Fixture.pcs([],width:65535,height:65535),timestamp:1) }
        try rejects("unknown segment extension") { var d = OriginalPGSDecoder(); _ = try d.consumeMatroska(packet:Fixture.pcs([]) + Fixture.segment(0x81),timestamp:1) }
        try rejects("epoch reset removes old graphics") { var d = decoder; _ = try d.consumeMatroska(packet:Fixture.pcs([Fixture.ref()]) + Fixture.end,timestamp:5) }
        try rejects("decreasing/wrapped timestamp") { var d = decoder; _ = try d.consumeMatroska(packet:Fixture.pcs([],state:0) + Fixture.end,timestamp:1) }
        decoder.reset()
        try decoder.finish()
        print("PASS: malformed lengths, wrappers, unfinished sets/fragments, missing palettes/objects, RLE bounds, epoch reset, unsupported extensions and timestamp order are explicit errors")

        let demo = Fixture.originalDemo()
        var demoDecoder = OriginalPGSDecoder()
        let demoEvents = try demoDecoder.consumeSUP(demo)
        try demoDecoder.finish()
        try require(demoEvents.count == 3 && demoEvents[0].bitmaps[0].width == 92 && demoEvents[0].bitmaps[0].height == 28 && demoEvents[1].time == 2.5 && demoEvents[2].isClear,"Original retained PGS fixture failed")
        if CommandLine.arguments.count > 1 {
            let url = URL(fileURLWithPath:CommandLine.arguments[1])
            try demo.write(to:url)
            let manifest:[String:Any] = ["origin":"Original generated LUMA 5x7 bitmap letterforms, scaled 4x; no downloaded or user content", "codec":"HDMV PGS", "canvasWidth":1920, "canvasHeight":1080, "bitmapWidth":92, "bitmapHeight":28, "x":914, "y":940, "displayTimes":[1,2.5], "clearTime":4, "bytes":demo.count, "expectedEvents":3]
            try JSONSerialization.data(withJSONObject:manifest,options:[.prettyPrinted,.sortedKeys]).write(to:url.deletingPathExtension().appendingPathExtension("json"))
            print("Saved original PGS fixture: \(url.path)")
        }
        print("PASS: original LUMA bitmap SUP generator, timed palette change and clear")
    }
}
