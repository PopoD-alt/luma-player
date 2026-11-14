import Foundation

private func policyCheck(_ condition: @autoclosure () -> Bool, _ message: String) throws {
    guard condition() else {
        throw NSError(domain: "UpdatePolicySmoke", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }
}

@main struct UpdatePolicySmoke {
    static func main() throws {
        let now = Date(timeIntervalSince1970: 1_700_000_000)
        let day: TimeInterval = 86_400
        try policyCheck(UpdateCheckPolicy.checkInterval == day && UpdateCheckPolicy.deferInterval == day,
            "The automatic attempt and Not Now intervals must be one day")

        var fresh = UpdateCheckPolicy()
        try policyCheck(fresh.shouldCheckAutomatically(at: now, isActive: true, isBusy: false),
            "First configured active launch should check automatically")
        try policyCheck(!fresh.shouldCheckAutomatically(at: now, isActive: false, isBusy: false),
            "An inactive app should not start an automatic check")
        try policyCheck(!fresh.shouldCheckAutomatically(at: now, isActive: true, isBusy: true),
            "A concurrent check or download should suppress another automatic check")
        // Record the attempt before any simulated network result. A failure must
        // not turn the active timer or every activation into a network retry loop.
        fresh.recordAttempt(at: now)
        for seconds in [0.0, 60.0, 3_600.0, day - 1] {
            try policyCheck(!fresh.shouldCheckAutomatically(at: now.addingTimeInterval(seconds), isActive: true, isBusy: false),
                "An offline attempt was retried before the daily interval")
        }
        try policyCheck(fresh.shouldCheckAutomatically(at: now.addingTimeInterval(day), isActive: true, isBusy: false),
            "The daily interval boundary should permit another check")
        try policyCheck(fresh.shouldCheckAutomatically(at: now.addingTimeInterval(-3_600), isActive: true, isBusy: false),
            "A clock rollback must not suppress update checks indefinitely")
        let afterRestart = try JSONDecoder().decode(UpdateCheckPolicy.self, from: JSONEncoder().encode(fresh))
        try policyCheck(afterRestart == fresh && !afterRestart.shouldCheckAutomatically(at: now.addingTimeInterval(60), isActive: true, isBusy: false),
            "Restart lost the attempt timestamp and bypassed offline throttling")
        print("PASS: first launch, active/busy gating, offline attempt throttling, exact daily boundary, clock rollback and restart persistence")

        var deferred = UpdateCheckPolicy()
        deferred.deferAutomaticChecks(at: now)
        let waiting = now.addingTimeInterval(day / 2)
        try policyCheck(!deferred.shouldCheckAutomatically(at: waiting, isActive: true, isBusy: false),
            "Not Now should defer automatic network checks")
        try policyCheck(!deferred.shouldOffer(build: 3, currentBuild: 2, at: waiting, manual: false, isActive: true, canPrompt: true),
            "Not Now should defer an already fetched newer release")
        try policyCheck(deferred.shouldOffer(build: 3, currentBuild: 2, at: waiting, manual: true, isActive: false, canPrompt: false),
            "A manual check should bypass a deferred automatic prompt")
        let savedDeferral = try JSONDecoder().decode(UpdateCheckPolicy.self, from: JSONEncoder().encode(deferred))
        try policyCheck(savedDeferral == deferred && !savedDeferral.shouldOffer(build: 4, currentBuild: 2, at: waiting, manual: false, isActive: true, canPrompt: true),
            "Restart or another release bypassed Not Now's automatic deferral")
        try policyCheck(savedDeferral.shouldCheckAutomatically(at: now.addingTimeInterval(day), isActive: true, isBusy: false)
            && savedDeferral.shouldOffer(build: 3, currentBuild: 2, at: now.addingTimeInterval(day), manual: false, isActive: true, canPrompt: true),
            "Not Now should expire at the daily boundary")
        print("PASS: Not Now defers checks and pending prompts across restarts; manual checks bypass it and the deferral expires deterministically")

        var disabled = UpdateCheckPolicy(automaticallyChecks: false)
        disabled.recordAttempt(at: now)
        try policyCheck(!disabled.shouldCheckAutomatically(at: now.addingTimeInterval(day * 2), isActive: true, isBusy: false),
            "A disabled updater started an automatic check")
        try policyCheck(!disabled.shouldOffer(build: 3, currentBuild: 2, at: now, manual: false, isActive: true, canPrompt: true),
            "A disabled updater offered an automatic prompt")
        try policyCheck(disabled.shouldOffer(build: 3, currentBuild: 2, at: now, manual: true, isActive: false, canPrompt: false),
            "Disabling automatic updates must preserve explicit manual checks")
        for build in [-1, 0, 1, 2] {
            try policyCheck(!disabled.shouldOffer(build: build, currentBuild: 2, at: now, manual: true, isActive: true, canPrompt: true),
                "Manual checking offered a non-newer or invalid release build")
        }
        let savedDisabled = try JSONDecoder().decode(UpdateCheckPolicy.self, from: JSONEncoder().encode(disabled))
        try policyCheck(!savedDisabled.automaticallyChecks && savedDisabled == disabled,
            "Automatic-check preference did not survive persistence")
        disabled.automaticallyChecks = true
        try policyCheck(disabled.shouldCheckAutomatically(at: now.addingTimeInterval(day), isActive: true, isBusy: false),
            "Re-enabling the updater should restore normal daily eligibility")
        print("PASS: disabling automatic checks persists while newer-build manual checks remain available; invalid/old/current builds never prompt")

        // A successfully fetched release remains eligible once playback ends,
        // without requiring a second network request inside the daily interval.
        let pending = UpdateCheckPolicy(lastAttempt: now)
        try policyCheck(!pending.shouldOffer(build: 3, currentBuild: 2, at: now, manual: false, isActive: true, canPrompt: false),
            "Automatic prompting interrupted playback/fullscreen")
        try policyCheck(!pending.shouldOffer(build: 3, currentBuild: 2, at: now, manual: false, isActive: false, canPrompt: true),
            "An automatic prompt appeared while the app was inactive")
        try policyCheck(!pending.shouldCheckAutomatically(at: now.addingTimeInterval(60), isActive: true, isBusy: false)
            && pending.shouldOffer(build: 3, currentBuild: 2, at: now.addingTimeInterval(60), manual: false, isActive: true, canPrompt: true),
            "A pending release could not prompt after playback ended without a redundant network check")
        print("PASS: pending newer releases wait for active idle presentation, then prompt without refetching the feed")

        var downloaded = UpdateCheckPolicy()
        downloaded.recordDownloaded(build: 3)
        try policyCheck(!downloaded.shouldOffer(build: 3, currentBuild: 2, at: now, manual: false, isActive: true, canPrompt: true),
            "An already downloaded build was repeatedly offered automatically")
        try policyCheck(downloaded.shouldOffer(build: 3, currentBuild: 2, at: now, manual: true, isActive: true, canPrompt: true),
            "An explicit manual check cannot rediscover an already downloaded build")
        try policyCheck(downloaded.shouldOffer(build: 4, currentBuild: 2, at: now, manual: false, isActive: true, canPrompt: true),
            "Downloading one build suppressed a later new release")
        let savedDownload = try JSONDecoder().decode(UpdateCheckPolicy.self, from: JSONEncoder().encode(downloaded))
        try policyCheck(savedDownload == downloaded && !savedDownload.shouldOffer(build: 3, currentBuild: 2, at: now, manual: false, isActive: true, canPrompt: true),
            "Restart lost the already-downloaded build suppression")
        print("PASS: verified-download suppression persists, explicit manual checks still work and subsequent newer builds remain eligible")
    }
}
