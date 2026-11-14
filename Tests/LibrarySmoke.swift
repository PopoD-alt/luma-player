import Foundation

@main
struct LibrarySmoke {
    @MainActor
    static func main() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("luma-library-test-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: folder) }
        let storage = folder.appendingPathComponent("library.json")
        let library = LibraryStore(storageURL: storage)
        let firstURL = folder.appendingPathComponent("First Film.mkv")
        let secondURL = folder.appendingPathComponent("Second Song.flac")
        let first = library.add(urls: [firstURL, firstURL, secondURL])
        precondition(first.count == 2 && library.items.count == 2, "Adding files must deduplicate paths.")
        precondition(library.items[0].title == "First Film", "The title should omit the extension.")
        precondition(!library.items[0].isAvailable, "Missing files must remain represented.")
        let id = first[0].id
        library.updateProgress(id: id, time: 42, duration: 120)
        library.addBookmark(id: id, time: 42)
        library.addBookmark(id: id, time: 42.2)
        library.flush()
        let restored = LibraryStore(storageURL: storage)
        precondition(restored.items[0].resumeTime == 42, "Progress must survive a fresh store.")
        precondition(restored.items[0].bookmarks.count == 1, "Bookmarks within a second should deduplicate.")
        restored.query = "SONG"
        precondition(restored.filteredItems.count == 1, "Queue search must ignore case.")
        restored.move(id: id, offset: 1)
        precondition(restored.items[1].id == id, "Reordering must preserve item identity.")
        restored.updateProgress(id: id, time: 119, duration: 120)
        restored.flush()
        precondition(LibraryStore(storageURL: storage).items[1].resumeTime == 0, "Completed items should resume from the start.")
        restored.select(id)
        restored.remove(id)
        precondition(restored.selectedID == first[1].id, "Removing the selection should select its neighbor.")
        restored.updateProgress(id: first[1].id, time: -.infinity, duration: .nan)
        precondition(restored.items[0].resumeTime == 0, "Invalid progress must be sanitized.")
        restored.clear()
        precondition(LibraryStore(storageURL: storage).items.isEmpty, "Clear must persist.")
        print("Library smoke passed: deduplication, missing files, persistence, bookmarks, search, reorder, completion, selection, invalid progress, clear.")
    }
}
