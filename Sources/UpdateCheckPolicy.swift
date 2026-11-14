import Foundation

/// Persistent scheduling decisions, independent of networking, AppKit and the library.
struct UpdateCheckPolicy: Codable, Equatable, Sendable {
    static let checkInterval: TimeInterval = 86_400
    static let deferInterval: TimeInterval = 86_400
    var automaticallyChecks: Bool
    var lastAttempt: Date?
    var deferredUntil: Date?
    var downloadedBuild: Int?

    init(automaticallyChecks: Bool = true, lastAttempt: Date? = nil,
         deferredUntil: Date? = nil, downloadedBuild: Int? = nil) {
        self.automaticallyChecks = automaticallyChecks
        self.lastAttempt = lastAttempt
        self.deferredUntil = deferredUntil
        self.downloadedBuild = downloadedBuild
    }
    func shouldCheckAutomatically(at now: Date, isActive: Bool, isBusy: Bool) -> Bool {
        guard automaticallyChecks, isActive, !isBusy, !isDeferred(at: now) else { return false }
        guard let lastAttempt else { return true }
        let elapsed = now.timeIntervalSince(lastAttempt)
        return elapsed < 0 || elapsed >= Self.checkInterval
    }
    mutating func recordAttempt(at now: Date) { lastAttempt = now }
    mutating func deferAutomaticChecks(at now: Date) { deferredUntil = now.addingTimeInterval(Self.deferInterval) }
    mutating func recordDownloaded(build: Int) {
        guard build > 0 else { return }
        downloadedBuild = max(downloadedBuild ?? 0, build)
    }
    func shouldOffer(build: Int, currentBuild: Int, at now: Date, manual: Bool,
                     isActive: Bool, canPrompt: Bool) -> Bool {
        guard build > 0, build > currentBuild else { return false }
        if manual { return true }
        return automaticallyChecks && isActive && canPrompt && !isDeferred(at: now)
            && build > (downloadedBuild ?? 0)
    }
    private func isDeferred(at now: Date) -> Bool {
        guard let deferredUntil else { return false }
        return now < deferredUntil
    }
}

/// The signed URL determines its preserved suffix and manual instructions.
/// Query strings and redirects never change a verified release's archive kind.
enum UpdateArchiveKind: String, CaseIterable, Sendable {
    case zip, dmg
    init?(url: URL) { self.init(rawValue: url.pathExtension.lowercased()) }
    var filenameExtension: String { rawValue }
    var installationInstructions: String {
        switch self {
        case .zip:
            return "Quit Luma, extract the downloaded ZIP, and replace your installed copy. macOS will perform its normal security checks when you open it."
        case .dmg:
            return "Quit Luma, open the downloaded DMG yourself, and drag Luma Player to Applications to replace your installed copy. macOS will perform its normal security checks when you open it."
        }
    }
}
