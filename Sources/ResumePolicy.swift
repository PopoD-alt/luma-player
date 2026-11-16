import Foundation

/// Resolves saved metadata only. Calling this policy never moves a live player.
enum ResumePolicy {
    enum Device: String { case computer, phone, legacy }
    struct Checkpoint {
        var position: Double?
        var duration: Double = 0
        var updatedAt: Double?
        var completed: Bool = false
    }
    struct Selection: Equatable {
        let device: Device
        let position: Double
        let duration: Double
        let completed: Bool
        let updatedAt: Double?
        var startPosition: Double { completed ? 0 : position }
    }

    static func validTimestamp(_ value: Double?) -> Double? {
        guard let value, value.isFinite, value > 0, value <= 253_402_300_799 else { return nil }
        return value
    }

    private static func bounded(_ value: Double?) -> Double? {
        guard let value, value.isFinite, value >= 0, value <= 31_536_000 else { return nil }
        return value
    }

    static func checkpoint(_ input: Checkpoint, device: Device, duration: Double) -> Selection? {
        guard let position = bounded(input.position), let stamp = validTimestamp(input.updatedAt) else { return nil }
        let length = bounded(duration).flatMap { $0 > 0 ? $0 : nil } ?? bounded(input.duration) ?? 0
        let time = length > 0 ? min(position, length) : position
        return Selection(device: device, position: time, duration: length, completed: input.completed, updatedAt: stamp)
    }

    static func preferred(computer: Checkpoint, phone: Checkpoint, legacyPosition: Double, duration: Double) -> Selection {
        let local = checkpoint(computer, device: .computer, duration: duration)
        let remote = checkpoint(phone, device: .phone, duration: duration)
        if let local, let remote { return remote.updatedAt! > local.updatedAt! ? remote : local }
        if let known = local ?? remote { return known }
        let length = bounded(duration) ?? 0
        let time = min(bounded(legacyPosition) ?? 0, length > 0 ? length : 31_536_000)
        return Selection(device: .legacy, position: time, duration: length,
                         completed: length > 0 && time / length >= 0.98, updatedAt: nil)
    }

    static func preferred(for item: MediaItem) -> Selection {
        let computer = Checkpoint(position: item.computerProgressSeconds, updatedAt: item.computerUpdatedAtUnixSeconds, completed: item.computerCompleted == true)
        let phone = Checkpoint(position: item.mobileProgressSeconds, duration: item.mobileDurationSeconds ?? 0,
                               updatedAt: item.mobileUpdatedAtUnixSeconds, completed: item.mobileCompleted == true)
        return preferred(computer: computer, phone: phone, legacyPosition: item.resumeTime, duration: item.duration)
    }

    static func latestItem(in items: [MediaItem], fallbackID: UUID?) -> MediaItem? {
        // Stable queue order resolves an inter-item tie, with the current item
        // preferred so a metadata refresh does not make the selection flicker.
        let fallback = items.first { $0.id == fallbackID }
        var latest = fallback.flatMap { preferred(for: $0).updatedAt != nil ? $0 : nil }
        for item in items {
            guard let stamp = preferred(for: item).updatedAt else { continue }
            if latest == nil || stamp > (preferred(for: latest!).updatedAt ?? 0) { latest = item }
        }
        return latest ?? fallback ?? items.first
    }
}
