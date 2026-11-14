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
        guard items.contains(where: { $0.id == id }), selectedID != id else { return }
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
        save()
    }

    /// Retain useful progress, but reopen a completed item from its beginning.
    /// Playback is considered complete at 98% of a known duration.
    func updateProgress(id: UUID, time: Double, duration: Double) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        let safeDuration = duration.isFinite && duration > 0 ? duration : items[index].duration
        var safeTime = time.isFinite ? max(0, time) : 0
        if safeDuration > 0 { safeTime = min(safeTime, safeDuration) }
        if safeDuration > 0 && safeTime / safeDuration >= 0.98 { safeTime = 0 }
        guard items[index].duration != safeDuration || abs(items[index].resumeTime - safeTime) > 0.05 else { return }
        items[index].duration = safeDuration
        items[index].resumeTime = safeTime
        progressDirty = true
        if Date().timeIntervalSince(lastProgressSave) >= 5 { save() }
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
        items[index].resumeTime = 0
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
                item.bookmarks = source.bookmarks.filter { $0.time.isFinite && $0.time >= 0 }
                return item
            }
            selectedID = items.contains(where: { $0.id == snapshot.selectedID }) ? snapshot.selectedID : items.first?.id
        } catch {
            persistenceError = "Could not read the saved library: \(error.localizedDescription)"
        }
    }

    private func save() {
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
