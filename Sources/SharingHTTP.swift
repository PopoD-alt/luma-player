import Foundation
import Darwin

struct SharedMedia {
    let id: String
    let url: URL
    let title: String
    let durationSeconds: Double
    let resumeSeconds: Double
    var computerProgressSeconds: Double? = nil
    var computerUpdatedAtUnixSeconds: Double? = nil
    var computerCompleted: Bool? = nil
    var mobileProgressSeconds: Double? = nil
    var mobileDurationSeconds: Double? = nil
    var mobileUpdatedAtUnixSeconds: Double? = nil
    var mobileCompleted: Bool? = nil
    var skipRanges: [SharedSkipRange] = []
}


/// Read-only skip metadata; this never authorizes remote playback commands.
struct SharedSkipRange: Codable {
    let kind: String
    let startSeconds: Double
    let endSeconds: Double
    let source: String
    var label: String? = nil

    static func validated(_ input: [SharedSkipRange], duration: Double) -> [SharedSkipRange] {
        let candidates = input.prefix(32).filter {
            ["intro", "credits"].contains($0.kind) && ["manual", "chapter"].contains($0.source)
                && $0.startSeconds.isFinite && $0.endSeconds.isFinite && $0.startSeconds >= 0
                && $0.endSeconds <= 31_536_000 && $0.endSeconds - $0.startSeconds >= 0.25
                && (!(duration.isFinite && duration > 0) || $0.endSeconds <= duration + 0.25)
        }.sorted { $0.startSeconds < $1.startSeconds }
        return Array(candidates.enumerated().filter { index, item in
            !candidates.enumerated().contains { otherIndex, other in
                index != otherIndex && item.startSeconds < other.endSeconds && other.startSeconds < item.endSeconds
            }
        }.prefix(8).map { _, item in
            SharedSkipRange(kind: item.kind, startSeconds: item.startSeconds,
                endSeconds: duration.isFinite && duration > 0 ? min(duration, item.endSeconds) : item.endSeconds,
                source: item.source, label: item.label.map { String(decoding: $0.utf16.prefix(64), as: UTF16.self) })
        })
    }
}

struct SharedProgressReport: Decodable {
    let itemId: String
    let positionSeconds: Double
    let durationSeconds: Double
    let completed: Bool

    static func parse(_ body: Data, hostDuration: Double) throws -> SharedProgressReport {
        guard body.count <= 4096, String(data: body, encoding: .utf8) != nil,
              let object = try JSONSerialization.jsonObject(with: body) as? [String: Any],
              Set(object.keys) == ["itemId", "positionSeconds", "durationSeconds", "completed"] else { throw SharingHTTP.Failure.malformed }
        let value = try JSONDecoder().decode(Self.self, from: body)
        guard UUID(uuidString: value.itemId) != nil, value.itemId.utf8.count <= 200,
              value.positionSeconds.isFinite, value.durationSeconds.isFinite,
              (0...31_536_000).contains(value.positionSeconds),
              (0...31_536_000).contains(value.durationSeconds) else { throw SharingHTTP.Failure.malformed }
        let duration = hostDuration.isFinite && hostDuration > 0 ? min(31_536_000, hostDuration) : value.durationSeconds
        let position = value.completed && duration > 0 ? duration
            : duration > 0 ? min(value.positionSeconds, duration) : value.positionSeconds
        return SharedProgressReport(itemId: value.itemId, positionSeconds: position,
                                    durationSeconds: duration, completed: value.completed)
    }
}

enum SharingHTTP {
    enum Failure: Error { case malformed, range }
    struct Request {
        let method: String
        let target: String
        let headers: [String: String]
        let body: Data
    }
    /// nil means the bounded request is incomplete. Request pipelining and
    /// transfer-encoded bodies are deliberately unsupported on this small API.
    static func parse(_ data: Data) throws -> Request? {
        guard data.count <= 36_864 else { throw Failure.malformed }
        guard let end = data.range(of: Data("\r\n\r\n".utf8)) else {
            if data.count > 32_768 { throw Failure.malformed }; return nil
        }
        guard end.upperBound <= 32_768, let text = String(data: data[..<end.lowerBound], encoding: .utf8),
              !text.utf8.contains(0) else { throw Failure.malformed }
        let lines = text.components(separatedBy: "\r\n")
        guard lines.count <= 101 else { throw Failure.malformed }
        let start = lines[0].split(separator: " ", omittingEmptySubsequences: false)
        guard start.count == 3, ["HTTP/1.1", "HTTP/1.0"].contains(String(start[2])),
              start[1].hasPrefix("/"), !start[1].hasPrefix("//"), start[1].count <= 4096,
              ["GET", "HEAD", "POST"].contains(String(start[0])) else { throw Failure.malformed }
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard !line.hasPrefix(" "), !line.hasPrefix("\t"), let colon = line.firstIndex(of: ":") else { throw Failure.malformed }
            let key = line[..<colon].lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            guard !key.isEmpty, key.utf8.allSatisfy({ (97...122).contains($0) || (48...57).contains($0) || $0 == 45 }),
                  headers[key] == nil, !value.contains("\r"), !value.contains("\n") else { throw Failure.malformed }
            headers[key] = value
        }
        guard headers["transfer-encoding"] == nil else { throw Failure.malformed }
        let rawLength = headers["content-length"] ?? "0"
        guard !rawLength.isEmpty, rawLength.utf8.allSatisfy({ (48...57).contains($0) }),
              let length = Int(rawLength), length <= 4096,
              start[0] == "POST" || length == 0 else { throw Failure.malformed }
        let needed = end.upperBound + length
        guard data.count >= needed else { return nil }
        guard data.count == needed else { throw Failure.malformed }
        return Request(method: String(start[0]), target: String(start[1]), headers: headers, body: data[end.upperBound..<needed])
    }
    static func range(_ header: String?, size: Int64) throws -> (start: Int64, length: Int64, partial: Bool) {
        guard size >= 0 else { throw Failure.range }
        guard let header else { return (0, size, false) }
        guard header.hasPrefix("bytes="), !header.contains(","), size > 0 else { throw Failure.range }
        let values = header.dropFirst(6).split(separator: "-", omittingEmptySubsequences: false)
        guard values.count == 2 else { throw Failure.range }
        func number(_ text: Substring) -> Int64? {
            guard !text.isEmpty, text.utf8.allSatisfy({ (48...57).contains($0) }) else { return nil }; return Int64(text)
        }
        if values[0].isEmpty {
            guard let suffix = number(values[1]), suffix > 0 else { throw Failure.range }
            let length = min(suffix, size); return (size - length, length, true)
        }
        guard let start = number(values[0]), start < size else { throw Failure.range }
        let end: Int64
        if values[1].isEmpty { end = size - 1 }
        else { guard let supplied = number(values[1]), supplied >= start else { throw Failure.range }; end = min(size - 1, supplied) }
        return (start, end - start + 1, true)
    }
    static func equalSecret(_ a: String, _ b: String) -> Bool {
        let left = Array(a.utf8), right = Array(b.utf8)
        guard left.count == right.count else { return false }
        var difference: UInt8 = 0
        for index in left.indices { difference |= left[index] ^ right[index] }
        return difference == 0
    }
    static func privateAddress(_ address: String) -> Bool {
        let host = address.split(separator: "%", maxSplits: 1).first.map(String.init) ?? address
        var ipv4 = in_addr()
        if inet_pton(AF_INET, host, &ipv4) == 1 {
            let value = UInt32(bigEndian: ipv4.s_addr)
            return value >> 24 == 10 || value >> 24 == 127 || value >> 20 == 0xAC1 || value >> 16 == 0xC0A8 || value >> 16 == 0xA9FE
        }
        var ipv6 = in6_addr()
        if inet_pton(AF_INET6, host, &ipv6) == 1 {
            let bytes = withUnsafeBytes(of: &ipv6) { Array($0) }
            if bytes.prefix(15).allSatisfy({ $0 == 0 }) && bytes[15] == 1 { return true }
            if bytes[0] & 0xFE == 0xFC || bytes[0] == 0xFE && bytes[1] & 0xC0 == 0x80 { return true }
            if bytes.prefix(10).allSatisfy({ $0 == 0 }) && bytes[10] == 255 && bytes[11] == 255 {
                return privateAddress(bytes[12...15].map(String.init).joined(separator: "."))
            }
        }
        return false
    }
    static func mime(_ url: URL) -> String {
        switch url.pathExtension.lowercased() {
        case "mp4", "m4v": return "video/mp4"
        case "mov": return "video/quicktime"
        case "mkv": return "video/x-matroska"
        case "webm": return "video/webm"
        case "avi": return "video/x-msvideo"
        case "mp3": return "audio/mpeg"
        case "m4a": return "audio/mp4"
        case "aac": return "audio/aac"
        case "flac": return "audio/flac"
        case "wav": return "audio/wav"
        default: return "application/octet-stream"
        }
    }
}

/// Serial descriptor ownership prevents stop/close racing a background read.
final class SharingFile: @unchecked Sendable {
    private var fd: Int32
    let size: Int64
    private let queue = DispatchQueue(label: "dev.sdas.luma.sharing-file", qos: .utility)
    init(url: URL) throws {
        let descriptor = Darwin.open(url.path, O_RDONLY | O_CLOEXEC | O_NONBLOCK)
        guard descriptor >= 0 else { throw CocoaError(.fileReadNoSuchFile) }
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_size >= 0 else {
            Darwin.close(descriptor); throw CocoaError(.fileReadUnsupportedScheme)
        }
        fd = descriptor; size = info.st_size
    }
    func read(at offset: Int64, count: Int, completion: @escaping @Sendable (Data?) -> Void) {
        queue.async { [self] in
            guard fd >= 0 else { completion(nil); return }
            var bytes = [UInt8](repeating: 0, count: min(262_144, count))
            var result: Int
            repeat { result = pread(fd, &bytes, bytes.count, offset) } while result < 0 && errno == EINTR
            completion(result > 0 ? Data(bytes.prefix(result)) : nil)
        }
    }
    func close() { queue.async { [self] in if fd >= 0 { Darwin.close(fd); fd = -1 } } }
    deinit { if fd >= 0 { Darwin.close(fd) } }
}
