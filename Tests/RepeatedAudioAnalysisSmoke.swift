import Foundation

@main struct RepeatedAudioAnalysisSmoke {
    static func check(_ value: @autoclosure () -> Bool, _ message: String) {
        if !value() { fatalError(message) }
    }
    static func signal(seconds: Double, seed: Int, gain: Double = 1) -> [Int16] {
        (0..<Int(seconds * 8000)).map { i in
            let t = Double(i) / 8000, note = Int(t * 8)
            let frequency = Double(130 + (note * 37 + seed * 13) % 800)
            let mix = 0.15 + 0.35 * Double((seed + note * 17) % 13) / 12
            let value = sin(2 * Double.pi * frequency * t) + mix * sin(2 * Double.pi * frequency * 2.37 * t + Double(seed))
            return Int16((value * 7000 * gain).rounded())
        }
    }
    static func fingerprint(_ values: [Int16], maxSeconds: Double = 6) -> RepeatedAudioFingerprint {
        var output = RepeatedAudioFingerprint(maximumSeconds: maxSeconds); output.append(values); return output
    }
    static func main() throws {
        let first = signal(seconds: 1, seed: 10) + signal(seconds: 4, seed: 123) + signal(seconds: 1, seed: 20)
        let second = signal(seconds: 2, seed: 11) + signal(seconds: 4, seed: 123, gain: 0.6)
        let a = fingerprint(first), b = fingerprint(second)
        let matches = try RepeatedAudioMatcher.match(first: a, second: b, kind: "intro", firstOffset: 0, secondOffset: 0, minimumSeconds: 3)
        check(matches.contains { abs($0.firstStartSeconds - 1) < 0.15 && abs($0.firstEndSeconds - 5) < 0.15 && abs($0.secondStartSeconds - 2) < 0.15 && $0.similarity >= 0.99 }, "Repeated varying audio/gain/offset match failed")
        let tail = fingerprint(signal(seconds: 4, seed: 456), maxSeconds: 4)
        let credits = try RepeatedAudioMatcher.match(first: tail, second: tail, kind: "credits", firstOffset: 98, secondOffset: 101, minimumSeconds: 3)
        check(credits.first?.firstStartSeconds == 98 && credits.first?.secondStartSeconds == 101, "Tail offsets were lost")
        let unrelated = try RepeatedAudioMatcher.match(first: a, second: fingerprint(signal(seconds: 6, seed: 999)), kind: "intro", firstOffset: 0, secondOffset: 0, minimumSeconds: 3)
        check(unrelated.isEmpty, "Unrelated audio matched")
        let silence = fingerprint([Int16](repeating: 0, count: 32000), maxSeconds: 4)
        check(tryMatch(silence, silence).isEmpty, "Silence matched")
        let tone = fingerprint((0..<32000).map { Int16((sin(2 * Double.pi * 220 * Double($0) / 8000) * 7000).rounded()) }, maxSeconds: 4)
        check(tryMatch(tone, tone).isEmpty, "Flat tone matched")
        let short = fingerprint(signal(seconds: 2, seed: 123), maxSeconds: 2)
        check(tryMatch(short, short).isEmpty, "A too-short repeat matched")
        var bounded = RepeatedAudioFingerprint(maximumSeconds: 2); bounded.append(signal(seconds: 6, seed: 33))
        check(bounded.frames.count == 16, "Fingerprint exceeded its frame cap")
        let invalid = RepeatedAudioFingerprint(maximumSeconds: .greatestFiniteMagnitude)
        check(invalid.frames.isEmpty, "Invalid bound allocated frames")
        let invalidMatches = try RepeatedAudioMatcher.match(first: a, second: b, kind: "intro", firstOffset: 0, secondOffset: 0, minimumSeconds: .greatestFiniteMagnitude)
        check(invalidMatches.isEmpty, "Huge minimum accepted")
        do { _ = try RepeatedAudioMatcher.match(first: a, second: b, kind: "intro", firstOffset: 0, secondOffset: 0, minimumSeconds: 3, cancelled: { true }); fatalError("Cancellation ignored") }
        catch is CancellationError { }
        print("PASS: repeated audio gain/offset matching, tail offsets, unrelated/silence/flat/short rejection, bounds and cancellation")
    }
    static func tryMatch(_ a: RepeatedAudioFingerprint, _ b: RepeatedAudioFingerprint) -> [SkipAudioCandidate] {
        (try? RepeatedAudioMatcher.match(first: a, second: b, kind: "intro", firstOffset: 0, secondOffset: 0, minimumSeconds: 3)) ?? []
    }
}
