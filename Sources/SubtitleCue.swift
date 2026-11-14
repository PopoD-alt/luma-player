import Foundation

struct SubtitleCue {
    let start: Double
    let end: Double
    let text: String
    static func read(_ url: URL) throws -> [SubtitleCue] {
        guard ["srt", "vtt"].contains(url.pathExtension.lowercased()) else { throw CodecFailure.unsupported("The new subtitle renderer currently supports SRT and VTT files.") }
        let data = try Data(contentsOf: url)
        guard data.count <= 2_097_152, let text = String(data: data, encoding: .utf8) else { throw CodecFailure.unsupported("Use a UTF-8 subtitle file smaller than 2 MB.") }
        let blocks = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n\n")
        var cues: [SubtitleCue] = []
        for block in blocks {
            let lines = block.components(separatedBy: "\n")
            guard let index = lines.firstIndex(where: { $0.contains("-->") }) else { continue }
            let times = lines[index].components(separatedBy: "-->")
            guard times.count == 2, let start = timestamp(times[0]), let end = timestamp(times[1]), end > start else { throw CodecFailure.malformed("subtitle timestamp") }
            cues.append(SubtitleCue(start: start, end: end, text: lines.dropFirst(index + 1).joined(separator: "\n")))
        }
        guard !cues.isEmpty else { throw CodecFailure.malformed("no subtitle cues") }
        return cues.sorted { $0.start < $1.start }
    }
    private static func timestamp(_ string: String) -> Double? {
        guard let token = string.trimmingCharacters(in: .whitespaces).split(separator: " ").first else { return nil }
        let segments = token.replacingOccurrences(of: ",", with: ".").split(separator: ":", omittingEmptySubsequences: false)
        let parts = segments.compactMap { Double($0) }
        guard parts.count == segments.count else { return nil }
        guard (2...3).contains(parts.count), parts.allSatisfy({ $0.isFinite && $0 >= 0 }), let seconds = parts.last, seconds < 60 else { return nil }
        if parts.count == 2 { return parts[0] * 60 + seconds }
        guard parts[1] < 60 else { return nil }
        return parts[0] * 3600 + parts[1] * 60 + seconds
    }
}
