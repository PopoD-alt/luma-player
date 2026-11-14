import Foundation
import CoreGraphics

@main struct CodecSmoke {
    static func main() async throws {
        let root = URL(fileURLWithPath: CommandLine.arguments[1])
        let msRoot = root.appendingPathComponent("ms-adpcm", isDirectory: true)
        let msManifest = try JSONDecoder().decode(MSManifest.self, from: Data(contentsOf: msRoot.appendingPathComponent("manifest.json")))
        for vector in msManifest.vectors {
            let decoded = try OriginalWaveDecoder.decode(Data(contentsOf: msRoot.appendingPathComponent(vector.name + ".wav")))
            precondition(decoded.channels == vector.channels && decoded.sampleRate == vector.sample_rate && decoded.samples == vector.expected_samples)
        }
        let malformedMS = try Data(contentsOf: msRoot.appendingPathComponent(msManifest.vectors[0].name + ".wav"))
        var invalidMS = malformedMS
        let msBytes = MediaBytes(data: malformedMS)
        let msFormat = try msBytes.chunks(start: 12, end: msBytes.u32(4) + 8).first { $0.0 == "fmt " }!.1
        invalidMS[msFormat.lowerBound + 20] = 0; invalidMS[msFormat.lowerBound + 21] = 0
        do { _ = try OriginalWaveDecoder.decode(invalidMS); fatalError("Accepted empty MS ADPCM coefficient table") } catch { }
        print("PASS: Microsoft ADPCM WAVE parser, all exact mono/stereo goldens, fact/partial blocks and invalid coefficient table")
        func rejects(_ label: String, _ action: () throws -> Void) {
            do { try action() } catch { return }; fatalError("Accepted malformed input: \(label)")
        }
        let imaData = try Data(contentsOf: root.appendingPathComponent("Luma-IMA.wav"))
        let ima = try OriginalWaveDecoder.decode(imaData)
        precondition(ima.channels == 1 && ima.sampleRate == 8000 && ima.samples.count == 505 * 64)
        precondition(Array(ima.samples.prefix(505)) == (0...504).map(Int16.init), "IMA quantizer/predictor golden mismatch")
        let stereo = try OriginalWaveDecoder.decode(Data(contentsOf: root.appendingPathComponent("Luma-IMA-Stereo.wav")))
        precondition(stereo.channels == 2 && stereo.samples == (0...8).flatMap { [Int16($0), Int16(100 - $0)] }, "IMA stereo channel interleave mismatch")
        let roundtrip = try OriginalWaveDecoder.decode(ima.pcmWave())
        precondition(roundtrip.samples == ima.samples && roundtrip.sampleRate == ima.sampleRate)
        let alaw = try OriginalWaveDecoder.decode(Data(contentsOf: root.appendingPathComponent("Luma-ALaw.wav")))
        precondition(Array(alaw.samples.prefix(4)) == [8,-8,32256,-32256])
        let mulaw = try OriginalWaveDecoder.decode(Data(contentsOf: root.appendingPathComponent("Luma-MuLaw.wav")))
        precondition(Array(mulaw.samples.prefix(4)) == [0,0,32124,-32124])
        rejects("truncated WAVE") { _ = try OriginalWaveDecoder.decode(Data(imaData.prefix(20))) }
        var bad = imaData
        // Locate the first data block and make its quantizer index invalid.
        let bytes = MediaBytes(data: bad)
        let chunks = try bytes.chunks(start: 12, end: bytes.u32(4) + 8)
        let samples = chunks.first { $0.0 == "data" }!.1
        bad[samples.lowerBound + 2] = 89
        rejects("invalid IMA step index") { _ = try OriginalWaveDecoder.decode(bad) }
        let aviData = try Data(contentsOf: root.appendingPathComponent("Luma-RGB.avi"))
        let avi = try OriginalAVI.open(aviData)
        precondition(avi.width == 64 && avi.height == 48 && avi.frames.count == 12 && avi.duration == 6)
        let first = try avi.image(at: 0)
        let second = try avi.image(at: 0.5)
        let red = first.dataProvider!.data! as Data, green = second.dataProvider!.data! as Data
        precondition(Array(red.prefix(4)) == [255,0,0,255])
        precondition(Array(green.prefix(4)) == [0,255,0,255])
        var orientationData = aviData
        orientationData.replaceSubrange(avi.frames[0].lowerBound..<avi.frames[0].lowerBound + 3, with: [255,0,0])
        let orientation = try OriginalAVI.open(orientationData)
        let orientationPixels = try orientation.image(at: 0).dataProvider!.data! as Data
        precondition(Array(orientationPixels.prefix(4)) == [255,0,0,255])
        let bottom = (avi.height - 1) * avi.width * 4
        precondition(Array(orientationPixels[bottom..<bottom + 4]) == [0,0,255,255], "DIB bottom-up orientation mismatch")
        let rleData = try Data(contentsOf: root.appendingPathComponent("Luma-RLE8.avi"))
        let rle = try OriginalAVI.open(rleData)
        precondition(rle.compression == 1 && rle.frames.count == 12)
        let rlePixel = try rle.image(at: 0.5).dataProvider!.data! as Data
        precondition(Array(rlePixel.prefix(4)) == [0,255,0,255])
        // Independent literal-mode vector: three palette literals, odd-byte
        // padding, then a run completing each scanline.
        let literalRow: [UInt8] = [0,3,0,1,2,0,61,0,0,0]
        let literalFrame = Data(Array(repeating: literalRow, count: rle.height).flatMap { $0 } + [0,1])
        var literals = rleData
        let oldFrame = rle.frames[0]
        literals.replaceSubrange(oldFrame, with: literalFrame)
        func put32(_ offset: Int, _ value: Int) { for index in 0..<4 { literals[offset + index] = UInt8(truncatingIfNeeded: value >> (index * 8)) } }
        let rleBytes = MediaBytes(data: rleData)
        let topChunks = try rleBytes.chunks(start: 12, end: rleBytes.u32(4) + 8)
        let movi = topChunks.first { $0.0 == "LIST" && (try? rleBytes.fourCC($0.1.lowerBound)) == "movi" }!.1
        put32(oldFrame.lowerBound - 4, literalFrame.count)
        put32(movi.lowerBound - 4, movi.count + literalFrame.count - oldFrame.count)
        put32(4, literals.count - 8)
        let literalImage = try OriginalAVI.open(literals).image(at: 0)
        let literalPixels = literalImage.dataProvider!.data! as Data
        precondition(Array(literalPixels.prefix(12)) == [255,0,0,255,0,255,0,255,0,0,255,255], "RLE8 literal/padding mismatch")
        var invalidRun = rleData
        invalidRun[rle.frames[0].lowerBound] = 65
        rejects("RLE scanline overflow") { _ = try OriginalAVI.open(invalidRun).image(at: 0) }
        var invalidColor = rleData
        invalidColor[rle.frames[0].lowerBound + 1] = 3
        rejects("RLE palette overflow") { _ = try OriginalAVI.open(invalidColor).image(at: 0) }
        rejects("non-finite timestamp") { _ = try avi.image(at: .nan) }
        rejects("truncated AVI") { _ = try OriginalAVI.open(Data(aviData.dropLast(16))) }
        let video1Root = root.appendingPathComponent("msvideo1")
        let video1Manifest = try JSONDecoder().decode(Video1Manifest.self, from: Data(contentsOf: video1Root.appendingPathComponent("manifest.json")))
        for stream in video1Manifest.streams {
            let fileData = try Data(contentsOf: root.appendingPathComponent("Luma-MSVideo1-\(stream.bitDepth).avi"))
            let video = try OriginalAVI.open(fileData)
            precondition(video.width == 64 && video.height == 48 && video.duration == 6 && video.frames.count == 12)
            precondition(video.codecName.contains("Microsoft Video 1") && video.codecName.contains("\(stream.bitDepth)-bit"))
            let goldens = try stream.frames.map { try Data(contentsOf: video1Root.appendingPathComponent($0.rgba)) }
            // Start at the final delta, jump backward through learned keys, then
            // forward/backward repeatedly; seeking must reproduce exact pixels.
            for index in [11, 0, 4, 10, 3, 8, 2, 7, 1, 9, 5, 6, 6, 11, 0] {
                let actual = try video.image(at: Double(index) * 0.5).dataProvider!.data! as Data
                precondition(actual == goldens[index], "Video 1 seek/replay differs at \(stream.bitDepth)-bit frame \(index)")
            }
            try await withThrowingTaskGroup(of: Void.self) { tasks in
                for index in [11, 0, 7, 4, 10, 2] {
                    tasks.addTask {
                        let actual = try video.image(at: Double(index) * 0.5).dataProvider!.data! as Data
                        precondition(actual == goldens[index], "Concurrent Video 1 checkpoint replay differs")
                    }
                }
                try await tasks.waitForAll()
            }
            try await cancelledWorker { _ = try video.image(at: 0.5) }
            let format = try video1Format(fileData)
            for tag in ["CRAM", "cram", "MSVC", "msvc", "WHAM", "wham"] {
                var tagged = fileData
                tagged.replaceSubrange(format.lowerBound + 16..<format.lowerBound + 20, with: tag.utf8)
                let alias = try OriginalAVI.open(tagged)
                let aliasPixels = try alias.image(at: 2).dataProvider!.data! as Data
                precondition(aliasPixels == goldens[4])
            }
            var malformed = fileData
            Self.put32(&malformed, format.lowerBound + 4, 63)
            rejects("Video 1 partial edge blocks") { _ = try OriginalAVI.open(malformed) }
            malformed = fileData
            Self.put32(&malformed, format.lowerBound + 8, UInt32(bitPattern: -48))
            rejects("Video 1 top-down DIB") { _ = try OriginalAVI.open(malformed) }
            malformed = fileData
            malformed[video.frames[1].lowerBound] = 255
            malformed[video.frames[1].lowerBound + 1] = 0x87
            let overflow = try OriginalAVI.open(malformed)
            rejects("Video 1 oversized delta skip") { _ = try overflow.image(at: 0.5) }
            malformed = fileData
            malformed[video.frames[0].lowerBound] = 1
            malformed[video.frames[0].lowerBound + 1] = 0x84
            let missingKey = try OriginalAVI.open(malformed)
            rejects("Video 1 first frame depends on missing predecessor") { _ = try missingKey.image(at: 0) }
            if stream.bitDepth == 8 {
                malformed = fileData
                Self.put32(&malformed, format.lowerBound + 32, 0)
                rejects("Video 1 incomplete default 256-color palette") { _ = try OriginalAVI.open(malformed) }
                // Reclassifying an existing chunk preserves every RIFF bound;
                // dynamic palettes must fail explicitly instead of using stale RGB.
                malformed = fileData
                malformed.replaceSubrange(video.frames[1].lowerBound - 8..<video.frames[1].lowerBound - 4, with: "00pc".utf8)
                rejects("Video 1 dynamic AVI palette") { _ = try OriginalAVI.open(malformed) }
            }
        }
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".srt")
        defer { try? FileManager.default.removeItem(at: file) }
        try "1\n00:00:01,000 --> 00:00:02,000\nHello\n".write(to: file, atomically: true, encoding: .utf8)
        let cues = try SubtitleCue.read(file)
        precondition(cues.count == 1 && cues[0].start == 1 && cues[0].end == 2 && cues[0].text.hasPrefix("Hello"))
        try "1\n00:bad:01,000 --> 00:00:02,000\nHello\n".write(to: file, atomically: true, encoding: .utf8)
        rejects("malformed subtitle timestamp") { _ = try SubtitleCue.read(file) }
        // A cancelled worker must terminate at the decoder boundary, even when
        // its supplied media is valid; cancellation is not a malformed-file error.
        try await cancelledWorker { _ = try OriginalWaveDecoder.decode(imaData) }
        try await cancelledWorker { _ = try OriginalAVI.open(aviData) }
        try await cancelledWorker { _ = try rle.image(at: 0) }
        print("PASS: original IMA ADPCM/PCM roundtrip, G.711 goldens, RGB/RLE8 AVI frames/timing, malformed/truncated media, subtitles")
        print("PASS: cancelled WAVE/AVI loading and frame workers propagate CancellationError")
        print("PASS: 8/16-bit Microsoft Video 1 AVI exact key/delta frames, random/concurrent seeking, aliases, headers and palette bounds")
    }

    struct Video1Manifest: Decodable { let streams: [Video1Stream] }
    struct MSManifest: Decodable { let vectors: [MSVector] }
    struct MSVector: Decodable { let name: String; let channels: Int; let sample_rate: Int; let expected_samples: [Int16] }
    struct Video1Stream: Decodable { let bitDepth: Int; let frames: [Video1Frame] }
    struct Video1Frame: Decodable { let rgba: String }

    static func video1Format(_ data: Data) throws -> Range<Int> {
        let bytes = MediaBytes(data: data)
        let top = try bytes.chunks(start: 12, end: bytes.u32(4) + 8)
        let header = top.first { $0.0 == "LIST" && (try? bytes.fourCC($0.1.lowerBound)) == "hdrl" }!.1
        let streams = try bytes.chunks(start: header.lowerBound + 4, end: header.upperBound)
        let stream = streams.first { $0.0 == "LIST" && (try? bytes.fourCC($0.1.lowerBound)) == "strl" }!.1
        return try bytes.chunks(start: stream.lowerBound + 4, end: stream.upperBound).first { $0.0 == "strf" }!.1
    }

    static func put32(_ data: inout Data, _ position: Int, _ value: UInt32) {
        for index in 0..<4 { data[position + index] = UInt8(truncatingIfNeeded: value >> (8 * index)) }
    }

    private static func cancelledWorker(_ action: @escaping @Sendable () throws -> Void) async throws {
        let worker = Task.detached {
            withUnsafeCurrentTask { $0?.cancel() }
            try action()
        }
        do {
            try await worker.value
            throw NSError(domain: "CodecSmoke", code: 1, userInfo: [NSLocalizedDescriptionKey: "Cancelled decoder worker completed instead of cancelling"])
        } catch is CancellationError { }
    }
}
