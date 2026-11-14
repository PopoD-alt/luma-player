import Foundation

@main struct MatroskaTextSmoke {
    static func main() throws {
        func cue(_ codec: String, _ value: String, start: Double = 2.5, duration: Double = 3) throws -> MatroskaTextCue? {
            try MatroskaTextDecoder.decode(codecID: codec, packet: Data(value.utf8), start: start, duration: duration)
        }
        func rejects(_ label: String, _ action: () throws -> Void) {
            do { try action() } catch is MatroskaTextError { return } catch { fatalError("Unexpected \(label): \(error)") }
            fatalError("Accepted invalid subtitle: \(label)")
        }
        func check(_ condition: @autoclosure () throws -> Bool) throws { let passed = try condition(); precondition(passed) }
        let utf8 = try cue("S_TEXT/UTF8", "<i>Hello</i> &amp; goodbye<br>Next &#x1F642;")!
        precondition(utf8.start == 2.5 && utf8.end == 5.5 && utf8.text == "Hello & goodbye\nNext 🙂")
        let ass = try cue("S_TEXT/ASS", #"7,0,Default,Speaker,0,0,0,,{\an8\bord2}Hello, world\NNext\nline\htext \{literal\}"#)!
        precondition(ass.text == "Hello, world\nNext\nline\u{a0}text {literal}")
        let ssa = try cue("S_TEXT/SSA", "1,,Default,,0,0,0,,SSA text")!
        precondition(ssa.text == "SSA text")
        let drawings = try cue("S_TEXT/ASS", #"8,0,Default,,0,0,0,,Before {\p1}m 0 0 l 100 100{\p0}After {\pos(20,30)}still text"#)!
        precondition(drawings.text == "Before After still text")
        try check(try cue("S_TEXT/ASS", #"9,0,Default,,0,0,0,,{\p1}m 0 0 l 100 100"#) == nil)
        let vtt = try cue("S_TEXT/WEBVTT", "<v Person><c.color>Visible</c></v> <00:00:01.000>text")!
        precondition(vtt.text == "Visible text")
        try check(try cue("S_TEXT/UTF8", "Text", start: -1, duration: 2)?.start == -1)
        try check(try cue("S_TEXT/UTF8", "Text", duration: 0) == nil)
        try check(try cue("S_TEXT/UTF8", "\u{feff}1 < 2 &unknown; &lt;b&gt;")?.text == "1 < 2 &unknown; <b>")
        rejects("bad UTF-8") { _ = try MatroskaTextDecoder.decode(codecID: "S_TEXT/UTF8", packet: Data([255]), start: 0, duration: 1) }
        rejects("oversized packet") { _ = try MatroskaTextDecoder.decode(codecID: "S_TEXT/UTF8", packet: Data(repeating: 65, count: MatroskaTextDecoder.maximumPacketBytes + 1), start: 0, duration: 1) }
        rejects("ASS fields") { _ = try cue("S_TEXT/ASS", "missing fields") }
        rejects("ASS ReadOrder") { _ = try cue("S_TEXT/ASS", "bad,0,Default,,0,0,0,,Text") }
        rejects("unterminated overrides") { _ = try cue("S_TEXT/ASS", "1,0,Default,,0,0,0,,{bad") }
        rejects("unknown bitmap codec") { _ = try cue("S_HDMV/PGS", "data") }
        rejects("legacy WebM layout") { _ = try cue("D_WEBVTT/SUBTITLES", "data") }
        rejects("negative duration") { _ = try cue("S_TEXT/UTF8", "Text", duration: -1) }
        rejects("nonfinite time") { _ = try cue("S_TEXT/UTF8", "Text", start: .nan) }
        let track = MatroskaTextTrack(id: 4, name: "Dialogue", language: "en", cues: [ass], isDefault: true, renderingNote: "ASS/SSA text rendering; advanced styling is not yet supported")
        let restored = try JSONDecoder().decode(MatroskaTextTrack.self, from: JSONEncoder().encode(track))
        precondition(restored == track && MatroskaTextDecoder.maximumCuesPerTrack == 100_000)
        print("PASS: UTF8/ASS/SSA/WebVTT text, timing/commas/escapes/entities, drawing exclusion, Codable tracks; malformed/unsupported/time/packet-limit rejection")
    }
}
