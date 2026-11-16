import Foundation

@main struct SkipPolicySmoke {
    static func check(_ condition: @autoclosure () -> Bool, _ message: String) { precondition(condition(), message) }
    @MainActor static func main() throws {
        let chapters = [PlaybackChapter(name: "Cold open", startSeconds: 0, durationSeconds: 1),
            PlaybackChapter(name: "Opening", startSeconds: 1, durationSeconds: 2),
            PlaybackChapter(name: "Story", startSeconds: 3, durationSeconds: 5),
            PlaybackChapter(name: "Credits", startSeconds: 8, durationSeconds: 2),
            PlaybackChapter(name: "Post-credits", startSeconds: 10, durationSeconds: 2)]
        let ranges = SkipPolicy.ranges(chapters: chapters, duration: 12)
        check(ranges.count == 2 && ranges[0].kind == "intro" && ranges[0].startSeconds == 1 && ranges[0].endSeconds == 3, "Explicit opening chapter boundary")
        check(ranges[1].kind == "credits" && ranges[1].startSeconds == 8 && ranges[1].endSeconds == 10, "Postcredit scene must survive")
        for name in ["Prologue", "Epilogue", "Chapter 1", "Scene 2", "Recap", "Post-credits", "Next episode", "Operation", "Credits scene", "Introduction to the story"] { check(SkipPolicy.kind(for: name) == nil, "Ambiguous chapter label \(name)") }
        check(SkipPolicy.kind(for: "[OP 1]") == "intro" && SkipPolicy.kind(for: "ENDING CREDITS") == "credits", "Conservative normalized labels")
        check(SkipPolicy.ranges(chapters: [.init(name: "Opening", startSeconds: 0, durationSeconds: 12)], duration: 12).isEmpty, "Whole-film opening label must not skip entire media")
        for invalid in [PlaybackSkipRange(kind: "intro", startSeconds: .nan, endSeconds: 3, source: "manual"),
            .init(kind: "intro", startSeconds: 3, endSeconds: 2, source: "manual"),
            .init(kind: "intro", startSeconds: -1, endSeconds: 2, source: "manual"),
            .init(kind: "intro", startSeconds: 1, endSeconds: 40, source: "manual"),
            .init(kind: "preview", startSeconds: 1, endSeconds: 3, source: "manual"),
            .init(kind: "intro", startSeconds: 1, endSeconds: 3, source: "guess")] { check(invalid.validated(duration: 12) == nil, "Unsafe skip range accepted") }
        let manual = PlaybackSkipRange(kind: "intro", startSeconds: 0.5, endSeconds: 2, source: "manual")
        let resolved = SkipPolicy.resolved(manual: [manual], chapters: chapters, duration: 12)
        check(resolved.count == 2 && resolved[0] == manual, "Manual timing overrides matching metadata")
        var state = SkipPlaybackState()
        check(state.take(at: 1.2, ranges: ranges, enabled: false, playing: true, seekable: true) == nil, "Auto-skip is opt-in")
        check(state.take(at: 1.2, ranges: ranges, enabled: true, playing: false, seekable: true) == nil, "Paused playback must stay still")
        check(state.take(at: 1.2, ranges: ranges, enabled: true, playing: true, seekable: false) == nil, "Unseekable input must stay still")
        check(state.take(at: 1.2, ranges: ranges, enabled: true, playing: true, seekable: true) == ranges[0], "Opt-in range not taken")
        check(state.take(at: 1.3, ranges: ranges, enabled: true, playing: true, seekable: true) == nil, "Native seek in-flight must not loop")
        state.reset(); state.suppress(at: 1.5, ranges: ranges)
        check(state.take(at: 1.6, ranges: ranges, enabled: true, playing: true, seekable: true) == nil, "Deliberate seek into opening must be respected")
        check(state.take(at: 8.5, ranges: ranges, enabled: true, playing: true, seekable: true) == ranges[1], "Suppressed intro must not suppress credits")
        check(state.take(at: 10.2, ranges: ranges, enabled: true, playing: true, seekable: true) == nil, "Postcredits must not be treated as credits")
        state.reset(); state.suppress(at: 1.5, ranges: ranges); state.suppress(at: 6, ranges: ranges)
        check(state.take(at: 1.6, ranges: ranges, enabled: true, playing: true, seekable: true) == nil, "A stalled user seek out of the intro must not be overridden by auto-skip")
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LumaSkipPolicy-\(UUID())")
        defer { try? FileManager.default.removeItem(at: folder) }
        let library = LibraryStore(storageURL: folder.appendingPathComponent("library.json"))
        let item = library.add(urls: [folder.appendingPathComponent("fixture.mkv")])[0]
        library.updateProgress(id: item.id, time: 0, duration: 12)
        check(library.setSkipRange(id: item.id, range: manual), "Manual range save")
        check(!library.setSkipRange(id: item.id, range: .init(kind: "credits", startSeconds: 1, endSeconds: 4, source: "manual")), "Overlapping manual ranges must be rejected")
        let restored = LibraryStore(storageURL: library.storageURL)
        check(restored.items[0].skipRanges == [manual], "Saved skip ranges must survive relaunch")
        let other = library.add(urls: [folder.appendingPathComponent("episode-two.mkv")])[0]
        library.updateProgress(id: other.id, time: 0, duration: 12)
        check(!library.setSkipRanges([item.id: [.init(kind: "intro", startSeconds: 1, endSeconds: 3, source: "manual")], other.id: [.init(kind: "credits", startSeconds: -1, endSeconds: 10, source: "manual")]]), "Invalid paired confirmation must fail")
        check(library.items[0].skipRanges == [manual] && library.items[1].skipRanges == nil, "Failed paired confirmation must not partially write")
        check(library.setSkipRanges([item.id: [.init(kind: "intro", startSeconds: 1, endSeconds: 3, source: "manual")], other.id: [.init(kind: "credits", startSeconds: 8, endSeconds: 10, source: "manual")]]), "Valid paired confirmation must persist both episodes")
        library.clearSkipRanges(id: item.id, kind: "intro")
        check(LibraryStore(storageURL: library.storageURL).items[0].skipRanges == nil, "Clear range must persist")
        var legacy = try JSONSerialization.jsonObject(with: Data(contentsOf: library.storageURL)) as! [String: Any]
        var legacyItems = legacy["items"] as! [[String: Any]]; legacyItems[0].removeValue(forKey: "skipRanges"); legacy["items"] = legacyItems
        try JSONSerialization.data(withJSONObject: legacy).write(to: library.storageURL)
        check(LibraryStore(storageURL: library.storageURL).items[0].skipRanges == nil, "Old libraries must remain readable")
        print("PASS: skip labels/boundaries preserve cold opens/postcredits; finite/bounded validation; opt-in/paused/unseekable/once-per-range/user-seek guards; manual override, nonoverlap, persistence and legacy migration")
    }
}
