import Foundation

/// Repeated audio is a reviewable candidate, never proof that a scene is credits.
struct SkipAudioCandidate: Identifiable, Sendable, Equatable {
    var kind: String
    var firstStartSeconds: Double
    var firstEndSeconds: Double
    var secondStartSeconds: Double
    var secondEndSeconds: Double
    var similarity: Double
    var id: String { "\(kind):\(firstStartSeconds):\(secondStartSeconds)" }
}

/// Original bounded spectral fingerprint; no model, codec or external process.
struct RepeatedAudioFingerprint {
    static let sampleRate = 8000
    static let frameSamples = 1000
    static let frameSeconds = 0.125
    static let frequencies: [Double] = [100, 150, 210, 290, 390, 520, 690, 910, 1200, 1570, 2050, 2670, 3450]
    private static let coefficients = frequencies.map { 2 * cos(2 * Double.pi * $0 / Double(sampleRate)) }
    private(set) var frames: [[Double]?] = []
    private var pending: [Int16] = []
    private let maximumFrames: Int
    init(maximumSeconds: Double) {
        maximumFrames = maximumSeconds.isFinite && maximumSeconds >= 0 && maximumSeconds <= 180 ? Int((maximumSeconds / Self.frameSeconds).rounded(.down)) : 0
        pending.reserveCapacity(Self.frameSamples)
    }
    mutating func append(_ samples: UnsafeBufferPointer<Int16>) {
        for value in samples {
            guard frames.count < maximumFrames else { return }
            pending.append(value)
            if pending.count == Self.frameSamples { frames.append(Self.feature(pending)); pending.removeAll(keepingCapacity: true) }
        }
    }
    mutating func append(_ samples: [Int16]) { samples.withUnsafeBufferPointer { append($0) } }
    private static func feature(_ samples: [Int16]) -> [Double]? {
        let rms = samples.reduce(0.0) { $0 + Double($1) * Double($1) } / Double(frameSamples)
        guard rms >= 10000 else { return nil } // Reject silence/near silence.
        let energies = coefficients.map { coefficient -> Double in
            var a = 0.0, b = 0.0
            for sample in samples { let next = Double(sample) + coefficient * a - b; b = a; a = next }
            return log1p(max(0, a * a + b * b - coefficient * a * b))
        }
        let mean = energies.reduce(0, +) / Double(energies.count)
        let centered = energies.map { $0 - mean }, norm = sqrt(centered.reduce(0) { $0 + $1 * $1 })
        return norm > 0.000001 ? centered.map { $0 / norm } : nil
    }
}

enum RepeatedAudioMatcher {
    /// Diagonal sequence matches allow a different cold-open duration per file.
    /// A strict threshold and diverse frames reduce obvious flat-tone matches.
    static func match(first: RepeatedAudioFingerprint, second: RepeatedAudioFingerprint,
                      kind: String, firstOffset: Double, secondOffset: Double,
                      minimumSeconds: Double = 15, cancelled: () -> Bool = { false }) throws -> [SkipAudioCandidate] {
        guard ["intro", "credits"].contains(kind), minimumSeconds.isFinite, minimumSeconds >= 1, minimumSeconds <= 180,
              firstOffset.isFinite, secondOffset.isFinite, firstOffset >= 0, secondOffset >= 0, firstOffset <= 31536000, secondOffset <= 31536000 else { return [] }
        let a = first.frames, b = second.frames
        guard !a.isEmpty, !b.isEmpty else { return [] }
        let minimum = Int(ceil(minimumSeconds / RepeatedAudioFingerprint.frameSeconds))
        var output: [SkipAudioCandidate] = []
        func accept(_ start: Int, _ count: Int, _ offset: Int, _ score: Double) {
            guard count >= minimum else { return }
            let values = a[start..<(start + count)].compactMap { $0 }
            let unique = Set(values.map { row in row.map { Int(($0 * 10).rounded()) }.map(String.init).joined(separator: ",") })
            guard unique.count >= min(16, max(6, minimum / 2)) else { return }
            // A constant tone, including an exact duplicate, must not become a candidate.
            let centroid = (0..<13).map { i in values.reduce(0) { $0 + $1[i] } / Double(values.count) }
            let variance = values.reduce(0.0) { sum, row in sum + zip(row, centroid).reduce(0) { $0 + pow($1.0 - $1.1, 2) } } / Double(values.count)
            guard variance >= 0.01 else { return }
            let step = RepeatedAudioFingerprint.frameSeconds
            output.append(SkipAudioCandidate(kind: kind, firstStartSeconds: firstOffset + Double(start) * step,
                firstEndSeconds: firstOffset + Double(start + count) * step,
                secondStartSeconds: secondOffset + Double(start + offset) * step,
                secondEndSeconds: secondOffset + Double(start + offset + count) * step,
                similarity: min(1, max(0, score / Double(count)))))
        }
        for offset in (1 - a.count)..<b.count {
            if cancelled() { throw CancellationError() }
            var run = 0, start = 0, score = 0.0
            for i in max(0, -offset)..<min(a.count, b.count - offset) {
                let j = i + offset
                let similarity = a[i].flatMap { x in b[j].map { y in zip(x, y).reduce(0) { $0 + $1.0 * $1.1 } } } ?? -1
                if similarity >= 0.99 { if run == 0 { start = i }; run += 1; score += similarity }
                else { accept(start, run, offset, score); run = 0; score = 0 }
            }
            accept(start, run, offset, score)
        }
        output.sort { $0.firstEndSeconds - $0.firstStartSeconds > $1.firstEndSeconds - $1.firstStartSeconds }
        var picked: [SkipAudioCandidate] = []
        for candidate in output {
            guard !picked.contains(where: { candidate.firstStartSeconds >= $0.firstStartSeconds && candidate.firstEndSeconds <= $0.firstEndSeconds }) else { continue }
            picked.append(candidate)
            if picked.count == 4 { break }
        }
        return picked
    }
}
