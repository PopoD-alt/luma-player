import Foundation

@main
struct LibrarySmoke {
    @MainActor
    static func main() async throws {
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
        // A pre-upgrade library contains none of the optional device fields.
        var legacy = try JSONSerialization.jsonObject(with: Data(contentsOf: storage)) as! [String: Any]
        var legacyItems = legacy["items"] as! [[String: Any]]
        for index in legacyItems.indices {
            for key in ["computerProgressSeconds", "computerUpdatedAtUnixSeconds", "computerCompleted", "mobileProgressSeconds", "mobileDurationSeconds", "mobileUpdatedAtUnixSeconds", "mobileCompleted"] { legacyItems[index].removeValue(forKey: key) }
        }
        legacy["items"] = legacyItems
        let oldStorage = folder.appendingPathComponent("legacy.json")
        try JSONSerialization.data(withJSONObject: legacy).write(to: oldStorage)
        let upgraded = LibraryStore(storageURL: oldStorage)
        precondition(upgraded.persistenceError == nil && upgraded.items[0].resumeTime == 42 && upgraded.items[0].mobileProgressSeconds == nil,
                     "Existing libraries must migrate without inventing phone progress.")
        precondition(restored.updateMobileProgress(id: id, time: 90, duration: 999, completed: false, updatedAt: 12345), "Known phone checkpoint rejected.")
        precondition(restored.items[0].resumeTime == 42 && restored.items[0].duration == 120,
                     "Phone reports must not overwrite this computer's resume position or canonical duration.")
        precondition(restored.items[0].mobileProgressSeconds == 90 && restored.items[0].mobileDurationSeconds == 120,
                     "Known duration must bound the phone's display metadata.")
        for (time, duration, stamp) in [(-1.0, 1.0, 12345.0), (Double.nan, 1, 12345), (1, Double.infinity, 12345), (31_536_001, 1, 12345), (1, 1, 0)] {
            precondition(!restored.updateMobileProgress(id: id, time: time, duration: duration, completed: false, updatedAt: stamp), "Unsafe phone checkpoint accepted.")
        }
        let unknown = first[1].id
        precondition(restored.updateMobileProgress(id: unknown, time: 17, duration: 100, completed: false, updatedAt: 12346), "Unknown-duration phone checkpoint rejected.")
        precondition(restored.items[1].duration == 0 && restored.items[1].mobileDurationSeconds == 100,
                     "A phone-reported duration must remain separate when desktop metadata is unknown.")
        // Phone-only activity has no desktop playback tick to trigger a save.
        // The bounded pending flush must persist its final paused checkpoint.
        try await Task.sleep(for: .seconds(5.2))
        let phoneRestored = LibraryStore(storageURL: storage)
        precondition(phoneRestored.items[0].mobileProgressSeconds == 90 && phoneRestored.items[0].mobileUpdatedAtUnixSeconds == 12345,
                     "Phone-only progress must persist without a desktop playback timer.")
        precondition(restored.updateMobileProgress(id: id, time: 1, duration: 120, completed: true, updatedAt: 12347)
                     && restored.items[0].mobileProgressSeconds == 120 && restored.items[0].mobileCompleted == true,
                     "A completed phone checkpoint must remain visibly finished.")
        restored.query = "SONG"
        precondition(restored.filteredItems.count == 1, "Queue search must ignore case.")
        restored.move(id: id, offset: 1)
        precondition(restored.items[1].id == id, "Reordering must preserve item identity.")
        restored.updateProgress(id: id, time: 119, duration: 120)
        restored.flush()
        precondition(LibraryStore(storageURL: storage).items[1].resumeTime == 0, "Completed items should resume from the start.")
        precondition(restored.items[1].computerProgressSeconds == 119 && restored.items[1].computerCompleted == true,
                     "Completion must keep actual desktop progress separately from restart-at-zero resume behavior.")
        restored.resetProgress(id: id)
        precondition(restored.items[1].computerProgressSeconds == 0 && restored.items[1].computerCompleted == false
                     && restored.items[1].mobileProgressSeconds == 120, "Resetting the computer checkpoint must not reset the phone.")
        restored.select(id)
        restored.remove(id)
        precondition(restored.selectedID == first[1].id, "Removing the selection should select its neighbor.")
        restored.updateProgress(id: first[1].id, time: -.infinity, duration: .nan)
        precondition(restored.items[0].resumeTime == 0, "Invalid progress must be sanitized.")
        restored.clear()
        precondition(LibraryStore(storageURL: storage).items.isEmpty, "Clear must persist.")
        print("Library smoke passed: existing-library migration, independent computer/phone checkpoints, canonical duration, bounded invalid-report rejection, phone-only deferred persistence, completion/restart semantics, bookmarks and queue operations.")
    }
}
