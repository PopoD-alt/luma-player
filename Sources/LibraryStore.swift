import Foundation
import Combine

struct MediaBookmark: Identifiable, Codable, Equatable {
    var id: UUID = UUID()
    var time: Double
    var label: String
}

struct MediaItem: Identifiable, Codable, Equatable {
    var id: UUID = UUID()
    var url: URL
    var title: String
    var duration: Double = 0
    var resumeTime: Double = 0
    // Separate checkpoints make watching on the phone visible without moving
    // this computer's player or replacing its own resume position. Optional
    // fields keep version-1 libraries written by earlier builds readable.
    var computerProgressSeconds: Double? = nil
    var computerUpdatedAtUnixSeconds: Double? = nil
    var computerCompleted: Bool? = nil
    var mobileProgressSeconds: Double? = nil
    var mobileDurationSeconds: Double? = nil
    var mobileUpdatedAtUnixSeconds: Double? = nil
    var mobileCompleted: Bool? = nil
    var skipRanges: [PlaybackSkipRange]? = nil
    var addedAt: Date = Date()
    var bookmarks: [MediaBookmark] = []

    /// A missing file remains in the queue so the user can identify or remove it.
    var isAvailable: Bool { FileManager.default.fileExists(atPath: url.path) }
}

@MainActor
final class LibraryStore: ObservableObject {
    static let supportedExtensions = [
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg",
        "m2v", "m2ts", "mts", "ts", "vob", "ogv", "3gp", "asf", "divx", "rm", "rmvb",
        "mp3", "m4a", "aac", "flac", "wav", "aiff", "aif", "ogg", "opus", "wma", "alac",
        "ape", "mka", "ac3", "dts", "caf"
    ]

    @Published var items: [MediaItem] = []
    @Published var selectedID: UUID?
    @Published var query: String = ""
    @Published private(set) var persistenceError: String?

    private struct Snapshot: Codable {
        var version: Int = 1
        var items: [MediaItem]
        var selectedID: UUID?
    }

    let storageURL: URL
    private var lastProgressSave = Date.distantPast
    private var progressDirty = false
    private var pendingProgressSave: Task<Void, Never>?
    private var followsLatestActivity = true

    init(storageURL: URL? = nil) {
        self.storageURL = storageURL ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Luma Player", isDirectory: true)
            .appendingPathComponent("library.json")
        load()
    }

    var selectedItem: MediaItem? { items.first { $0.id == selectedID } }

    var filteredItems: [MediaItem] {
        let text = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return items }
        return items.filter {
            $0.title.localizedCaseInsensitiveContains(text) ||
            $0.url.lastPathComponent.localizedCaseInsensitiveContains(text)
        }
    }

    /// Add explicitly supplied files. Existing entries keep their progress and bookmarks.
    @discardableResult
    func add(urls: [URL]) -> [MediaItem] {
        var known = Set(items.map { $0.url.standardizedFileURL.path })
        var added: [MediaItem] = []
        for url in urls where url.isFileURL {
            let normalized = url.standardizedFileURL
            guard known.insert(normalized.path).inserted else { continue }
            let item = MediaItem(url: normalized, title: normalized.deletingPathExtension().lastPathComponent)
            items.append(item)
            added.append(item)
        }
        if selectedID == nil { selectedID = items.first?.id }
        if !added.isEmpty { save() }
        return added
    }

    func select(_ id: UUID) {
        guard items.contains(where: { $0.id == id }) else { return }
        // Even choosing the already highlighted item is an explicit choice.
        followsLatestActivity = false
        guard selectedID != id else { return }
        selectedID = id
        save()
    }

    func remove(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        items.remove(at: index)
        if selectedID == id {
            selectedID = items.isEmpty ? nil : items[min(index, items.count - 1)].id
        }
        save()
    }

    func clear() {
        items.removeAll()
        selectedID = nil
        followsLatestActivity = true
        save()
    }

    /// Retain useful progress, but reopen a completed item from its beginning.
    /// Playback is considered complete at 98% of a known duration.
    func updateProgress(id: UUID, time: Double, duration: Double) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        let safeDuration = duration.isFinite && duration > 0 ? duration : items[index].duration
        var safeTime = time.isFinite ? max(0, time) : 0
        if safeDuration > 0 { safeTime = min(safeTime, safeDuration) }
        let actualTime = safeTime
        let completed = safeDuration > 0 && safeTime / safeDuration >= 0.98
        if completed { safeTime = 0 }
        guard items[index].duration != safeDuration || abs(items[index].resumeTime - safeTime) > 0.05
                || items[index].computerProgressSeconds == nil
                || abs((items[index].computerProgressSeconds ?? 0) - actualTime) > 0.05
                || items[index].computerCompleted != completed else { return }
        var item = items[index]
        item.duration = safeDuration
        item.resumeTime = safeTime
        item.computerProgressSeconds = actualTime
        item.computerUpdatedAtUnixSeconds = Date().timeIntervalSince1970
        item.computerCompleted = completed
        items[index] = item
        markProgressDirty()
    }

    /// A phone checkpoint is metadata only. In particular, its reported length
    /// must never become the computer's authoritative media duration.
    @discardableResult
    func updateMobileProgress(id: UUID, time: Double, duration: Double, completed: Bool,
                              updatedAt: Double = Date().timeIntervalSince1970) -> Bool {
        guard let index = items.firstIndex(where: { $0.id == id }), time.isFinite, duration.isFinite,
              updatedAt.isFinite, updatedAt > 0, updatedAt <= 253_402_300_799, time >= 0, duration >= 0,
              time <= 31_536_000, duration <= 31_536_000 else { return false }
        let hostDuration = items[index].duration
        let acceptedDuration = hostDuration.isFinite && hostDuration > 0 ? min(hostDuration, 31_536_000) : duration
        let acceptedTime = completed && acceptedDuration > 0 ? acceptedDuration
            : acceptedDuration > 0 ? min(time, acceptedDuration) : time
        var item = items[index]
        item.mobileProgressSeconds = acceptedTime
        item.mobileDurationSeconds = acceptedDuration > 0 ? acceptedDuration : nil
        item.mobileUpdatedAtUnixSeconds = updatedAt
        item.mobileCompleted = completed
        items[index] = item
        if followsLatestActivity { selectedID = ResumePolicy.latestItem(in: items, fallbackID: selectedID)?.id }
        markProgressDirty()
        return true
    }

    func completeProgress(id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        var item = items[index]
        item.resumeTime = 0
        item.computerProgressSeconds = item.duration > 0 ? item.duration : item.computerProgressSeconds
        item.computerCompleted = true
        item.computerUpdatedAtUnixSeconds = Date().timeIntervalSince1970
        items[index] = item
        markProgressDirty()
    }

    private func markProgressDirty() {
        progressDirty = true
        let remaining = 5 - Date().timeIntervalSince(lastProgressSave)
        if remaining <= 0 { save(); return }
        guard pendingProgressSave == nil else { return }
        pendingProgressSave = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(remaining))
            guard !Task.isCancelled, let self else { return }
            self.pendingProgressSave = nil
            self.flush()
        }
    }

    func addBookmark(id: UUID, time: Double) {
        guard time.isFinite, let index = items.firstIndex(where: { $0.id == id }) else { return }
        let duration = items[index].duration
        let safeTime = duration > 0 ? min(max(0, time), duration) : max(0, time)
        guard !items[index].bookmarks.contains(where: { abs($0.time - safeTime) < 1 }) else { return }
        let seconds = Int(safeTime)
        let label = seconds >= 3600
            ? String(format: "%d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60)
            : String(format: "%d:%02d", seconds / 60, seconds % 60)
        items[index].bookmarks.append(MediaBookmark(time: safeTime, label: label))
        items[index].bookmarks.sort { $0.time < $1.time }
        save()
    }

    @discardableResult
    func setSkipRange(id: UUID, range: PlaybackSkipRange) -> Bool { setSkipRanges([id: [range]]) }

    /// Confirm matched timings for two episodes as one local-library write.
    @discardableResult
    func setSkipRanges(_ updates: [UUID: [PlaybackSkipRange]]) -> Bool {
        guard !updates.isEmpty, updates.count <= 2 else { return false }
        var changed = items
        for (id, input) in updates {
            guard let index = changed.firstIndex(where: { $0.id == id }), !input.isEmpty, input.count <= 2 else { return false }
            var ranges = SkipPolicy.resolved(manual: changed[index].skipRanges ?? [], chapters: [], duration: changed[index].duration)
            for range in input {
                guard range.source == "manual", let valid = range.validated(duration: changed[index].duration) else { return false }
                ranges.removeAll { $0.kind == valid.kind }
                guard !ranges.contains(where: { valid.startSeconds < $0.endSeconds && valid.endSeconds > $0.startSeconds }) else { return false }
                ranges.append(valid)
            }
            changed[index].skipRanges = ranges.sorted { $0.startSeconds < $1.startSeconds }
        }
        items = changed; save(); return true
    }

    func clearSkipRanges(id: UUID, kind: String? = nil) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        let remaining = (items[index].skipRanges ?? []).filter { kind != nil && $0.kind != kind }
        items[index].skipRanges = remaining.isEmpty ? nil : remaining; save()
    }

    func removeBookmark(id: UUID, bookmarkID: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        items[index].bookmarks.removeAll { $0.id == bookmarkID }
        save()
    }

    func move(id: UUID, offset: Int) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        let destination = min(max(0, index + offset), items.count - 1)
        guard destination != index else { return }
        let item = items.remove(at: index)
        items.insert(item, at: destination)
        save()
    }

    func resetProgress(id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        var item = items[index]
        item.resumeTime = 0
        item.computerProgressSeconds = 0
        item.computerCompleted = false
        item.computerUpdatedAtUnixSeconds = Date().timeIntervalSince1970
        items[index] = item
        save()
    }

    /// Call on media changes and app termination to persist the last polling interval.
    func flush() { if progressDirty { save() } }

    private func load() {
        guard FileManager.default.fileExists(atPath: storageURL.path) else { return }
        do {
            let data = try Data(contentsOf: storageURL)
            let snapshot = try JSONDecoder().decode(Snapshot.self, from: data)
            guard snapshot.version == 1 else {
                persistenceError = "This library was saved by an unsupported version of Luma Player."
                return
            }
            items = snapshot.items.map { source in
                var item = source
                item.url = source.url.standardizedFileURL
                item.duration = source.duration.isFinite ? max(0, source.duration) : 0
                item.resumeTime = source.resumeTime.isFinite ? max(0, source.resumeTime) : 0
                if item.duration > 0 {
                    item.resumeTime = min(item.resumeTime, item.duration)
                    if item.resumeTime / item.duration >= 0.98 { item.resumeTime = 0 }
                }
                func checkpoint(_ value: Double?) -> Double? {
                    guard let value, value.isFinite, value >= 0, value <= 31_536_000 else { return nil }
                    return value
                }
                func timestamp(_ value: Double?) -> Double? {
                    guard let value, value.isFinite, value > 0, value <= 253_402_300_799 else { return nil }; return value
                }
                item.computerProgressSeconds = checkpoint(item.computerProgressSeconds)
                if let time = item.computerProgressSeconds, item.duration > 0 { item.computerProgressSeconds = min(time, item.duration) }
                item.computerUpdatedAtUnixSeconds = timestamp(item.computerUpdatedAtUnixSeconds)
                item.mobileProgressSeconds = checkpoint(item.mobileProgressSeconds)
                item.mobileDurationSeconds = checkpoint(item.mobileDurationSeconds).flatMap { $0 > 0 ? $0 : nil }
                item.mobileUpdatedAtUnixSeconds = timestamp(item.mobileUpdatedAtUnixSeconds)
                if let time = item.mobileProgressSeconds {
                    let duration = item.duration > 0 ? item.duration : item.mobileDurationSeconds ?? 0
                    if duration > 0 { item.mobileProgressSeconds = min(time, duration) }
                }
                item.skipRanges = source.skipRanges.map { Array($0.prefix(8)).compactMap { $0.validated(duration: item.duration) }.filter { $0.source == "manual" } }
                item.bookmarks = source.bookmarks.filter { $0.time.isFinite && $0.time >= 0 }
                return item
            }
            selectedID = ResumePolicy.latestItem(in: items, fallbackID: snapshot.selectedID)?.id
        } catch {
            persistenceError = "Could not read the saved library: \(error.localizedDescription)"
        }
    }

    private func save() {
        pendingProgressSave?.cancel(); pendingProgressSave = nil
        do {
            try FileManager.default.createDirectory(at: storageURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(Snapshot(items: items, selectedID: selectedID))
            try data.write(to: storageURL, options: .atomic)
            progressDirty = false
            lastProgressSave = Date()
            persistenceError = nil
        } catch {
            persistenceError = "Could not save the library: \(error.localizedDescription)"
        }
    }
}
