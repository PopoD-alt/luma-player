import Foundation

struct MatroskaTextCue: Sendable, Equatable, Codable {
    let start: Double
    let end: Double
    let text: String
}

struct MatroskaTextTrack: Sendable, Equatable, Codable, Identifiable {
    let id: Int32
    let name: String
    let language: String
    var cues: [MatroskaTextCue]
    let isDefault: Bool
    let isForced: Bool
    let renderingNote: String?

    init(id: Int32, name: String, language: String, cues: [MatroskaTextCue],
         isDefault: Bool = false, isForced: Bool = false, renderingNote: String? = nil) {
        self.id = id; self.name = name; self.language = language; self.cues = cues
        self.isDefault = isDefault; self.isForced = isForced; self.renderingNote = renderingNote
    }
}

enum MatroskaTextError: Error, LocalizedError {
    case malformed(String), unsupported(String), capacity
    var errorDescription: String? {
        switch self {
        case .malformed(let detail): return "Malformed Matroska text subtitle: \(detail)"
        case .unsupported(let detail): return "Unsupported Matroska text subtitle: \(detail)"
        case .capacity: return "Matroska text subtitles exceed the supported packet or cue limit."
        }
    }
}

/// Original plain-text decoding of the public Matroska subtitle mappings:
/// https://www.matroska.org/technical/subtitles.html
/// ASS/SSA styling, fonts, animation, positioning, karaoke and vector drawings
/// are not rendered. S_TEXT/WEBVTT supplies cue text directly in its Block;
/// legacy D_WEBVTT packet layouts and BlockAddition layout data are not parsed.
enum MatroskaTextDecoder {
    static let maximumPacketBytes = 1_048_576
    /// The demuxer accumulating cues must enforce this per-track bound.
    static let maximumCuesPerTrack = 100_000

    static func decode(codecID: String, packet: Data, start: Double, duration: Double) throws -> MatroskaTextCue? {
        guard packet.count <= maximumPacketBytes else { throw MatroskaTextError.capacity }
        guard start.isFinite, duration.isFinite, duration >= 0, (start + duration).isFinite else {
            throw MatroskaTextError.malformed("non-finite or negative cue duration")
        }
        guard var text = String(data: packet, encoding: .utf8) else { throw MatroskaTextError.malformed("packet is not valid UTF-8") }
        if text.first == "\u{feff}" { text.removeFirst() }
        text = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        switch codecID {
        case "S_TEXT/ASS", "S_TEXT/SSA":
            let fields = text.split(separator: ",", maxSplits: 8, omittingEmptySubsequences: false)
            guard fields.count == 9, let readOrder = Int64(fields[0].trimmingCharacters(in: .whitespaces)), readOrder >= 0 else {
                throw MatroskaTextError.malformed("ASS/SSA packet must contain ReadOrder and eight following fields")
            }
            text = try assPlainText(String(fields[8]))
        case "S_TEXT/UTF8", "S_TEXT/WEBVTT": break
        default: throw MatroskaTextError.unsupported(codecID)
        }
        text = decodeEntities(stripMarkup(text, webVTT: codecID == "S_TEXT/WEBVTT"))
        text = String(String.UnicodeScalarView(text.unicodeScalars.filter {
            !CharacterSet.controlCharacters.contains($0) || $0 == "\n" || $0 == "\t"
        })).trimmingCharacters(in: .whitespacesAndNewlines)
        guard duration > 0, !text.isEmpty else { return nil }
        return MatroskaTextCue(start: start, end: start + duration, text: text)
    }

    private static func assPlainText(_ input: String) throws -> String {
        let scalars = Array(input.unicodeScalars)
        var output = String.UnicodeScalarView(), override = String.UnicodeScalarView()
        var inOverride = false, drawing = false, index = 0
        while index < scalars.count {
            let scalar = scalars[index]
            if inOverride {
                if scalar == "}" {
                    drawing = drawingMode(String(override), previous: drawing)
                    override.removeAll(keepingCapacity: true)
                    inOverride = false
                } else {
                    override.append(scalar)
                    if scalar == "\\", index + 1 < scalars.count, scalars[index + 1] == "}" {
                        index += 1; override.append(scalars[index])
                    }
                }
            } else if scalar == "\\", index + 1 < scalars.count {
                let next = scalars[index + 1]
                let replacement: UnicodeScalar?
                switch next {
                case "N", "n": replacement = "\n"
                case "h": replacement = "\u{00a0}"
                case "{", "}", "\\": replacement = next
                default: replacement = nil
                }
                if let replacement {
                    if !drawing { output.append(replacement) }
                    index += 1
                } else if !drawing { output.append(scalar) }
            } else if scalar == "{" {
                inOverride = true
            } else if !drawing { output.append(scalar) }
            index += 1
        }
        guard !inOverride else { throw MatroskaTextError.malformed("unterminated ASS/SSA override block") }
        return String(output)
    }

    private static func drawingMode(_ override: String, previous: Bool) -> Bool {
        let scalars = Array(override.unicodeScalars)
        var drawing = previous, index = 0
        while index + 1 < scalars.count {
            if scalars[index] == "\\", scalars[index + 1] == "p" {
                var cursor = index + 2
                while cursor < scalars.count, scalars[cursor] == " " || scalars[cursor] == "\t" { cursor += 1 }
                var value = 0, found = false
                while cursor < scalars.count, scalars[cursor].value >= 48, scalars[cursor].value <= 57 {
                    found = true
                    value = min(1_000_000, value * 10 + Int(scalars[cursor].value - 48))
                    cursor += 1
                }
                if found { drawing = value > 0; index = cursor - 1 }
            }
            index += 1
        }
        return drawing
    }

    private static func stripMarkup(_ input: String, webVTT: Bool) -> String {
        let known = Set(["b", "i", "u", "s", "font", "span", "c", "v", "lang", "ruby", "rt", "p", "div", "br"])
        var result = "", candidate = "", inside = false
        for scalar in input.unicodeScalars {
            if !inside {
                if scalar == "<" { inside = true; candidate = "" }
                else { result.unicodeScalars.append(scalar) }
            } else if scalar == ">" {
                let token = candidate.trimmingCharacters(in: .whitespaces)
                let trimmed = token.hasPrefix("/") ? String(token.dropFirst()) : token
                let name = String(trimmed.prefix { $0.isLetter }).lowercased()
                let timestamp = webVTT && !token.isEmpty && token.first?.isNumber == true
                    && token.contains(":") && token.allSatisfy { $0.isNumber || $0 == ":" || $0 == "." }
                if name == "br" || ((name == "p" || name == "div") && token.hasPrefix("/")) { result.append("\n") }
                else if !known.contains(name) && !timestamp { result += "<" + candidate + ">" }
                inside = false; candidate = ""
            } else { candidate.unicodeScalars.append(scalar) }
        }
        if inside { result += "<" + candidate }
        return result
    }

    private static func decodeEntities(_ input: String) -> String {
        let scalars = Array(input.unicodeScalars)
        let named = ["amp": "&", "lt": "<", "gt": ">", "quot": "\"", "apos": "'", "nbsp": "\u{00a0}", "lrm": "\u{200e}", "rlm": "\u{200f}"]
        var output = String.UnicodeScalarView(), index = 0
        while index < scalars.count {
            guard scalars[index] == "&" else { output.append(scalars[index]); index += 1; continue }
            var end = index + 1
            while end < scalars.count, end - index <= 32, scalars[end] != ";", scalars[end] != "&" { end += 1 }
            if end < scalars.count, scalars[end] == ";", end - index <= 32 {
                let entity = String(String.UnicodeScalarView(scalars[index + 1..<end]))
                var replacement = named[entity]
                if entity.hasPrefix("#") {
                    let hex = entity.hasPrefix("#x") || entity.hasPrefix("#X")
                    let digits = String(entity.dropFirst(hex ? 2 : 1))
                    if let value = UInt32(digits, radix: hex ? 16 : 10), value != 0, let scalar = UnicodeScalar(value) { replacement = String(scalar) }
                }
                if let replacement {
                    output.append(contentsOf: replacement.unicodeScalars); index = end + 1; continue
                }
            }
            output.append(scalars[index]); index += 1
        }
        return String(output)
    }
}
