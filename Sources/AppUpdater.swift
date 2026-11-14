import AppKit
import Combine
import Foundation
import SwiftUI

/// Checks the owner's signed feed and offers verified downloads. Never opens,
/// executes or installs an archive; the saved media library is not involved.
@MainActor final class AppUpdater: ObservableObject {
    static let shared = AppUpdater()
    @Published private(set) var isBusy = false
    @Published private(set) var isConfigured = false
    @Published var automaticallyChecksForUpdates: Bool {
        didSet {
            guard oldValue != automaticallyChecksForUpdates else { return }
            policy.automaticallyChecks = automaticallyChecksForUpdates
            savePolicy()
            if !automaticallyChecksForUpdates {
                pendingRelease = nil
                if operationOrigin == .automatic { cancelOperation() }
            } else { pollAutomatically() }
        }
    }

    private enum Origin { case automatic, manual }
    private static let preferenceKey = "Luma.UpdateCheckPolicy.v1"
    private let defaults: UserDefaults
    private var policy: UpdateCheckPolicy
    private var configuration: UpdateConfiguration?
    private var pendingRelease: SignedRelease?
    private var canPromptAutomatically: () -> Bool = { true }
    private var activeObserver: NSObjectProtocol?
    private var timer: AnyCancellable?
    private var task: Task<Void, Never>?
    private var operationID: UUID?
    private var operationOrigin: Origin?
    private let session = URLSession(configuration: .ephemeral, delegate: HTTPSRedirects(), delegateQueue: nil)

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        let saved = defaults.data(forKey: Self.preferenceKey)
        let loaded = saved.flatMap { $0.count <= 16_384 ? try? JSONDecoder().decode(UpdateCheckPolicy.self, from: $0) : nil }
        policy = loaded ?? UpdateCheckPolicy()
        automaticallyChecksForUpdates = policy.automaticallyChecks
    }

    /// Called at launch; repeated calls update the presentation gate safely.
    /// No feed request is made when bundle configuration is absent or invalid.
    func startIfConfigured(canPromptAutomatically: @escaping () -> Bool = { true }) {
        self.canPromptAutomatically = canPromptAutomatically
        configuration = UpdateConfiguration(info: Bundle.main.infoDictionary ?? [:])
        isConfigured = configuration != nil
        guard isConfigured else { stop(); return }
        if activeObserver == nil {
            activeObserver = NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification,
                object: nil, queue: .main) { [weak self] _ in
                    MainActor.assumeIsolated { self?.pollAutomatically() }
                }
        }
        if timer == nil {
            timer = Timer.publish(every: 60, on: .main, in: .common).autoconnect().sink { [weak self] _ in
                MainActor.assumeIsolated { self?.pollAutomatically() }
            }
        }
        pollAutomatically()
    }

    func stop() {
        timer?.cancel(); timer = nil
        if let activeObserver { NotificationCenter.default.removeObserver(activeObserver); self.activeObserver = nil }
        pendingRelease = nil
        cancelOperation()
        configuration = nil; isConfigured = false
    }

    /// Manual checks bypass automatic deferral/disable and supersede a pending
    /// release or an in-flight automatic feed request.
    func checkForUpdates() {
        guard configuration != nil else {
            message("Updates aren’t available in this preview", "Your media and saved library will stay available when you install a newer Luma Player.")
            return
        }
        if isBusy {
            guard operationOrigin == .automatic else { return }
            cancelOperation()
        }
        pendingRelease = nil
        beginCheck(origin: .manual)
    }

    private var currentBuild: Int {
        Int(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0") ?? 0
    }
    private var automaticPresentationAllowed: Bool {
        NSApp.isActive && NSApp.modalWindow == nil
            && !NSApp.windows.contains(where: { $0.attachedSheet != nil })
            && canPromptAutomatically()
    }

    private func pollAutomatically() {
        guard configuration != nil, automaticallyChecksForUpdates, !isBusy, NSApp.isActive else { return }
        let now = Date()
        // Refresh a due feed before considering an old pending release.
        if policy.shouldCheckAutomatically(at: now, isActive: true, isBusy: false) {
            beginCheck(origin: .automatic)
            return
        }
        guard let release = pendingRelease else { return }
        if release.build <= currentBuild || release.build <= (policy.downloadedBuild ?? 0) { pendingRelease = nil; return }
        guard policy.shouldOffer(build: release.build, currentBuild: currentBuild, at: now,
            manual: false, isActive: true, canPrompt: automaticPresentationAllowed) else { return }
        beginOffer(release)
    }

    private func beginCheck(origin: Origin) {
        guard let configuration, !isBusy else { return }
        let identifier = beginOperation(origin)
        // Persist before I/O so offline machines cannot retry every timer tick.
        policy.recordAttempt(at: Date()); savePolicy()
        task = Task {
            defer { finishOperation(identifier) }
            do {
                var request = URLRequest(url: configuration.feedURL)
                request.cachePolicy = .reloadIgnoringLocalCacheData
                request.timeoutInterval = 30
                let (bytes, response) = try await session.bytes(for: request)
                try Self.validate(response)
                guard response.expectedContentLength <= 262_144 else { throw SignedRelease.ValidationError.invalidManifest }
                var data = Data()
                for try await byte in bytes {
                    try Task.checkCancellation()
                    guard data.count < 262_144 else { throw SignedRelease.ValidationError.invalidManifest }
                    data.append(byte)
                }
                try Task.checkCancellation()
                guard operationID == identifier else { return }
                let release = try SignedRelease.verify(data, publicKey: configuration.publicKey)
                guard let kind = UpdateArchiveKind(url: release.url) else { throw SignedRelease.ValidationError.invalidDownload }
                guard release.build > currentBuild else {
                    pendingRelease = nil
                    if origin == .manual { message("You’re up to date", "You have the latest Luma Player release.") }
                    return
                }
                if origin == .automatic {
                    guard automaticallyChecksForUpdates else { return }
                    pendingRelease = release
                    guard policy.shouldOffer(build: release.build, currentBuild: currentBuild, at: Date(), manual: false,
                        isActive: NSApp.isActive, canPrompt: automaticPresentationAllowed) else { return }
                }
                try await offer(release, kind: kind, identifier: identifier, automatic: origin == .automatic)
            } catch is CancellationError {
                return
            } catch {
                guard operationID == identifier, !Task.isCancelled else { return }
                // Network/signature failures on automatic checks are quiet. Once
                // Download is accepted, failures belong to an explicit action.
                if operationOrigin == .manual { downloadError() }
            }
        }
    }

    private func beginOffer(_ release: SignedRelease) {
        guard let kind = UpdateArchiveKind(url: release.url), !isBusy else { return }
        let identifier = beginOperation(.automatic)
        task = Task {
            defer { finishOperation(identifier) }
            do { try await offer(release, kind: kind, identifier: identifier, automatic: true) }
            catch is CancellationError { return }
            catch {
                guard operationID == identifier, !Task.isCancelled else { return }
                if operationOrigin == .manual { downloadError() }
            }
        }
    }

    private func offer(_ release: SignedRelease, kind: UpdateArchiveKind, identifier: UUID, automatic: Bool) async throws {
        try Task.checkCancellation()
        guard operationID == identifier else { return }
        if automatic {
            guard policy.shouldOffer(build: release.build, currentBuild: currentBuild, at: Date(), manual: false,
                isActive: NSApp.isActive, canPrompt: automaticPresentationAllowed) else { return }
        }
        let prompt = NSAlert()
        prompt.messageText = "Luma Player \(release.version) is available"
        prompt.informativeText = String(release.notes.prefix(2000)) + "\n\nDownload the verified update, then install it manually. Your saved library stays in place."
        prompt.addButton(withTitle: "Download Update")
        prompt.addButton(withTitle: "Not Now")
        let accepted = prompt.runModal() == .alertFirstButtonReturn
        policy.deferAutomaticChecks(at: Date()); savePolicy()
        pendingRelease = nil
        try Task.checkCancellation()
        guard operationID == identifier, accepted else { return }
        operationOrigin = .manual // Approved download survives disabling autos.
        var request = URLRequest(url: release.url)
        request.timeoutInterval = 300
        let (temporary, response) = try await session.download(for: request)
        defer { try? FileManager.default.removeItem(at: temporary) }
        try Self.validate(response)
        try Task.checkCancellation()
        let verification = Task.detached(priority: .utility) {
            try Task.checkCancellation()
            try release.verifyDownload(at: temporary)
            try Task.checkCancellation()
        }
        try await withTaskCancellationHandler(operation: { try await verification.value }, onCancel: { verification.cancel() })
        try Task.checkCancellation()
        guard operationID == identifier else { return }
        guard let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first else { throw SignedRelease.ValidationError.invalidDownload }
        let destination = downloads.appendingPathComponent("Luma-\(release.build)-\(UUID().uuidString.prefix(8)).\(kind.filenameExtension)")
        try FileManager.default.moveItem(at: temporary, to: destination)
        policy.recordDownloaded(build: release.build); savePolicy()
        try Task.checkCancellation()
        guard operationID == identifier else { return }
        message("Verified update downloaded", kind.installationInstructions + "\n\nYour saved library stays in place.")
        try Task.checkCancellation()
        guard operationID == identifier else { return }
        NSWorkspace.shared.activateFileViewerSelecting([destination])
    }

    private func beginOperation(_ origin: Origin) -> UUID {
        let identifier = UUID()
        operationID = identifier; operationOrigin = origin; isBusy = true
        return identifier
    }
    private func cancelOperation() {
        operationID = nil; operationOrigin = nil
        task?.cancel(); task = nil; isBusy = false
    }
    private func finishOperation(_ identifier: UUID) {
        guard operationID == identifier else { return }
        operationID = nil; operationOrigin = nil; task = nil; isBusy = false
    }
    private func savePolicy() {
        if let data = try? JSONEncoder().encode(policy) { defaults.set(data, forKey: Self.preferenceKey) }
    }
    private static func validate(_ response: URLResponse) throws {
        guard let response = response as? HTTPURLResponse, response.statusCode == 200,
              response.url?.scheme == "https", response.url?.user == nil, response.url?.password == nil else { throw SignedRelease.ValidationError.invalidDownload }
    }
    private func downloadError() {
        message("The update could not be verified", "Nothing was installed. Check your connection and try again later.")
    }
    private func message(_ title: String, _ detail: String) {
        let alert = NSAlert()
        alert.messageText = title; alert.informativeText = detail
        alert.addButton(withTitle: "OK"); alert.runModal()
    }
}

/// Refuse HTTPS-to-HTTP or credential-bearing redirects for feeds and archives.
private final class HTTPSRedirects: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        guard let url = request.url, url.scheme == "https", url.user == nil, url.password == nil else {
            completionHandler(nil); return
        }
        completionHandler(request)
    }
}

struct CheckForUpdatesCommand: View {
    @ObservedObject private var updater = AppUpdater.shared
    var body: some View {
        Button(updater.isBusy ? "Checking for Updates…" : "Check for Updates…", action: updater.checkForUpdates)
            .disabled(updater.isBusy)
        Toggle("Automatically Check for Updates", isOn: $updater.automaticallyChecksForUpdates)
            .disabled(!updater.isConfigured)
    }
}
