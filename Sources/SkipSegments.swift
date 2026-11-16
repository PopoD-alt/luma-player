import Foundation

/// Only explicit metadata or user-confirmed timings become actionable ranges.
struct PlaybackSkipRange: Codable, Equatable, Identifiable {
    var kind: String
    var startSeconds: Double
    var endSeconds: Double
    var source: String
    var label: String? = nil
    var id: String { "\(kind):\(startSeconds):\(endSeconds)" }
    var actionTitle: String { kind == "intro" ? "Skip intro" : "Skip credits" }
    func validated(duration: Double = 0) -> PlaybackSkipRange? {
        guard ["intro", "credits"].contains(kind), ["manual", "chapter"].contains(source),
              startSeconds.isFinite, endSeconds.isFinite, startSeconds >= 0,
              endSeconds <= 31_536_000, endSeconds - startSeconds >= 0.25,
              duration <= 0 || endSeconds <= duration + 0.25 else { return nil }
        var result = self
        if duration > 0 { result.endSeconds = min(result.endSeconds, duration) }
        if let label { result.label = String(label.prefix(160)) }
        return result
    }
    func contains(_ seconds: Double) -> Bool { seconds.isFinite && seconds >= startSeconds && seconds < endSeconds - 0.05 }
}

struct PlaybackChapter: Equatable {
    var name: String
    var startSeconds: Double
    var durationSeconds: Double
}

enum SkipPolicy {
    static func kind(for name: String) -> String? {
        let normalized = name.lowercased().components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }.joined(separator: " ")
        if ["opening credits", "opening song", "intro song"].contains(normalized)
            || normalized.range(of: "^(intro|opening|op)( [0-9]+)?$", options: .regularExpression) != nil { return "intro" }
        if ["end credits", "ending credits", "closing credits", "ending song"].contains(normalized)
            || normalized.range(of: "^(credits|outro|ending|ed)( [0-9]+)?$", options: .regularExpression) != nil { return "credits" }
        return nil
    }
    static func ranges(chapters: [PlaybackChapter], duration: Double) -> [PlaybackSkipRange] {
        guard duration.isFinite, duration > 0, duration <= 31_536_000, chapters.count <= 1024 else { return [] }
        let ordered = chapters.filter { $0.startSeconds.isFinite && $0.startSeconds >= 0 && $0.startSeconds < duration }
            .sorted { $0.startSeconds < $1.startSeconds }
        var result: [PlaybackSkipRange] = []
        for (index, chapter) in ordered.enumerated() {
            guard let kind = kind(for: chapter.name) else { continue }
            let next = index + 1 < ordered.count ? ordered[index + 1].startSeconds : duration
            var end = next
            if chapter.durationSeconds.isFinite, chapter.durationSeconds > 0 { end = min(end, chapter.startSeconds + chapter.durationSeconds) }
            // Unusually broad labels are not sufficient evidence to skip a film.
            let length = end - chapter.startSeconds
            guard kind == "intro" ? length <= min(600, duration / 2) : length <= 1800 && chapter.startSeconds >= duration / 2,
                  let range = PlaybackSkipRange(kind: kind, startSeconds: chapter.startSeconds, endSeconds: end,
                                                source: "chapter", label: chapter.name).validated(duration: duration) else { continue }
            if !result.contains(where: { $0.kind == kind }) { result.append(range) }
        }
        return result
    }
    static func resolved(manual: [PlaybackSkipRange], chapters: [PlaybackChapter], duration: Double) -> [PlaybackSkipRange] {
        let candidates = manual.prefix(8).compactMap { $0.validated(duration: duration) }.filter { $0.source == "manual" }
        var saved: [PlaybackSkipRange] = []
        for range in candidates where !saved.contains(where: { $0.kind == range.kind || range.startSeconds < $0.endSeconds && range.endSeconds > $0.startSeconds }) { saved.append(range) }
        return (saved + ranges(chapters: chapters, duration: duration).filter { candidate in !saved.contains { $0.kind == candidate.kind || candidate.startSeconds < $0.endSeconds && candidate.endSeconds > $0.startSeconds } })
            .sorted { $0.startSeconds < $1.startSeconds }
    }
}

/// Consume before requesting a native seek, preventing repeats while it settles.
struct SkipPlaybackState {
    private(set) var consumed: Set<String> = []
    mutating func reset() { consumed.removeAll() }
    mutating func suppress(at target: Double, ranges: [PlaybackSkipRange]) {
        for range in ranges where range.contains(target) { consumed.insert(range.id) }
    }
    mutating func take(at seconds: Double, ranges: [PlaybackSkipRange], enabled: Bool, playing: Bool, seekable: Bool) -> PlaybackSkipRange? {
        guard enabled, playing, seekable, let range = ranges.first(where: { $0.contains(seconds) && !consumed.contains($0.id) }) else { return nil }
        consumed.insert(range.id); return range
    }
    mutating func consume(_ range: PlaybackSkipRange) { consumed.insert(range.id) }
}
