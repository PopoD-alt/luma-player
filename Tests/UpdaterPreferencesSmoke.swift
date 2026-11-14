import Foundation

@main struct UpdaterPreferencesSmoke {
    @MainActor static func main() throws {
        let suite = "LumaUpdaterTest.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suite) else { fatalError("Cannot isolate updater preferences") }
        defer { defaults.removePersistentDomain(forName: suite) }
        let updater = AppUpdater(defaults: defaults)
        precondition(updater.automaticallyChecksForUpdates && !updater.isBusy && !updater.isConfigured)
        updater.automaticallyChecksForUpdates = false
        let restarted = AppUpdater(defaults: defaults)
        precondition(!restarted.automaticallyChecksForUpdates && !restarted.isBusy)
        restarted.automaticallyChecksForUpdates = true
        precondition(AppUpdater(defaults: defaults).automaticallyChecksForUpdates)
        // This standalone test executable has no publisher/feed bundle metadata.
        // Starting/stopping an unconfigured preview must never schedule a request.
        precondition(UpdateConfiguration(info: Bundle.main.infoDictionary ?? [:]) == nil)
        restarted.startIfConfigured(canPromptAutomatically: { false })
        precondition(!restarted.isConfigured && !restarted.isBusy)
        restarted.stop(); restarted.stop()
        precondition(!restarted.isConfigured && !restarted.isBusy)
        precondition(AppUpdater(defaults: defaults).automaticallyChecksForUpdates)
        print("PASS: actual updater automatic preference persists in isolated defaults; unconfigured preview/start/stop remains idle without feed requests")
    }
}
