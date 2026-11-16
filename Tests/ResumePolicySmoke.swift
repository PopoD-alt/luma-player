import Foundation

@main struct ResumePolicySmoke {
    @MainActor static func main() throws {
        typealias Checkpoint = ResumePolicy.Checkpoint
        let empty = Checkpoint(position: nil, updatedAt: nil)
        func resolve(_ computer: Checkpoint = Checkpoint(position: nil, updatedAt: nil),
                     _ phone: Checkpoint = Checkpoint(position: nil, updatedAt: nil), legacy: Double = 0) -> ResumePolicy.Selection {
            ResumePolicy.preferred(computer: computer, phone: phone, legacyPosition: legacy, duration: 120)
        }
        precondition(resolve().startPosition == 0 && resolve().device == .legacy, "New media starts at zero")
        let computer = Checkpoint(position: 42, updatedAt: 100)
        let phone = Checkpoint(position: 75, updatedAt: 101)
        precondition(resolve(computer, phone).device == .phone && resolve(computer, phone).startPosition == 75, "Newest phone checkpoint must win")
        precondition(resolve(Checkpoint(position: 20, updatedAt: 102), phone).startPosition == 20, "Newest computer checkpoint must win")
        precondition(resolve(computer, Checkpoint(position: 0, updatedAt: 101)).startPosition == 0
                     && resolve(computer, Checkpoint(position: 0, updatedAt: 101)).device == .phone, "Known zero must not fall back to old progress")
        precondition(resolve(computer, Checkpoint(position: 120, updatedAt: 101, completed: true)).startPosition == 0, "Newest completed phone checkpoint restarts")
        precondition(resolve(Checkpoint(position: 120, updatedAt: 102, completed: true), phone).startPosition == 0, "Newest completed computer checkpoint restarts")
        precondition(resolve(computer, Checkpoint(position: 75, updatedAt: 100)).device == .computer, "Computer wins timestamp tie")
        precondition(resolve(Checkpoint(position: 99, updatedAt: nil), Checkpoint(position: 80, updatedAt: nil), legacy: 17).startPosition == 17, "Untimestamped metadata must use the legacy computer position")
        precondition(resolve(empty, empty, legacy: 119).completed && resolve(empty, empty, legacy: 119).startPosition == 0, "Legacy completion must restart")
        for invalid in [Double.nan, .infinity, -1, 31_536_001] {
            precondition(resolve(computer, Checkpoint(position: invalid, updatedAt: 200)).device == .computer, "Invalid positions cannot beat known progress")
        }
        for invalid in [Double.nan, .infinity, 0, -1, 253_402_300_800] {
            precondition(resolve(computer, Checkpoint(position: 80, updatedAt: invalid)).device == .computer, "Invalid timestamp cannot beat known progress")
        }
        precondition(resolve(computer, Checkpoint(position: nil, updatedAt: 200)).device == .computer, "A timestamp without a known position is not activity")
        precondition(resolve(computer, Checkpoint(position: 150, duration: 500, updatedAt: 200)).position == 120, "Canonical computer duration must bound phone position")
        let unknownLength = ResumePolicy.preferred(computer: empty, phone: Checkpoint(position: 80, duration: 100, updatedAt: 200), legacyPosition: 0, duration: 0)
        precondition(unknownLength.position == 80 && unknownLength.duration == 100, "Phone duration can describe a checkpoint without rewriting canonical metadata")

        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LumaResumePolicy-\(UUID())")
        defer { try? FileManager.default.removeItem(at: folder) }
        let library = LibraryStore(storageURL: folder.appendingPathComponent("library.json"))
        let items = library.add(urls: [folder.appendingPathComponent("One.mp4"), folder.appendingPathComponent("Two.mp4")])
        library.updateProgress(id: items[0].id, time: 10, duration: 120)
        library.select(items[0].id)
        let newer = Date().timeIntervalSince1970 + 10
        precondition(library.updateMobileProgress(id: items[1].id, time: 30, duration: 120, completed: false, updatedAt: newer))
        precondition(library.selectedID == items[0].id, "Incoming metadata must not steal an explicit selection")
        library.flush()
        let restored = LibraryStore(storageURL: library.storageURL)
        precondition(restored.selectedID == items[1].id && restored.selectedItem?.mobileProgressSeconds == 30, "Startup selects latest cross-device item")
        precondition(restored.updateMobileProgress(id: items[0].id, time: 0, duration: 120, completed: false, updatedAt: newer + 1))
        precondition(restored.selectedID == items[0].id, "An untouched idle startup follows new phone activity, including zero")
        restored.select(items[0].id)
        precondition(restored.updateMobileProgress(id: items[1].id, time: 60, duration: 120, completed: false, updatedAt: newer + 2))
        precondition(restored.selectedID == items[0].id, "Even choosing the highlighted item freezes automatic selection")
        restored.flush()
        var older = items[0]; older.resumeTime = 12
        precondition(ResumePolicy.latestItem(in: [older, items[1]], fallbackID: items[1].id)?.id == items[1].id, "Legacy libraries preserve previous selection without invented activity")
        print("PASS: newest cross-device resume, known zero, completed restart, computer ties, legacy fallback, invalid metadata, canonical bounds, startup latest selection and explicit idle selection protection")
    }
}
