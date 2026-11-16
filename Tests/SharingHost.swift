import AppKit
import Foundation
import Darwin

/// Bounded interoperability host. Shares only authored fixtures, never a saved library.
@main struct SharingHost {
    @MainActor static func main() {
        setbuf(stdout, nil)
        NSApplication.shared.setActivationPolicy(.accessory)
        Task { @MainActor in
            do {
                guard CommandLine.arguments.count == 3 else { throw CocoaError(.fileReadInvalidFileName) }
                let fixtures = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
                let ready = URL(fileURLWithPath: CommandLine.arguments[2])
                let demo = UUID().uuidString.lowercased(), hevc = UUID().uuidString.lowercased()
                var items = [SharedMedia(id: demo, url: fixtures.appendingPathComponent("Luma-Demo.mp4"), title: "Luma Demo", durationSeconds: 12, resumeSeconds: 0.5),
                             SharedMedia(id: hevc, url: fixtures.appendingPathComponent("Luma-HEVC10-AAC51.mp4"), title: "Luma HEVC Main10 / AAC5.1", durationSeconds: 2, resumeSeconds: 0)]
                guard items.allSatisfy({ FileManager.default.fileExists(atPath: $0.url.path) }) else { throw CocoaError(.fileNoSuchFile) }
                let server = LocalSharingServer()
                server.start(provider: { items }, onProgress: { report, stamp in
                    guard let index = items.firstIndex(where: { $0.id == report.itemId }) else { return false }
                    items[index].mobileProgressSeconds = report.positionSeconds
                    items[index].mobileDurationSeconds = report.durationSeconds > 0 ? report.durationSeconds : nil
                    items[index].mobileUpdatedAtUnixSeconds = stamp
                    items[index].mobileCompleted = report.completed
                    return true
                })
                let deadline = Date().addingTimeInterval(15)
                while server.port == 0 {
                    guard server.isSharing, Date() < deadline else { throw CocoaError(.fileReadUnknown) }
                    try await Task.sleep(for: .milliseconds(50))
                }
                let metadata: [String: Any] = ["port": server.port, "code": server.pairingCode, "ids": ["demo": demo, "hevc10": hevc]]
                let data = try JSONSerialization.data(withJSONObject: metadata)
                let pending = ready.deletingLastPathComponent().appendingPathComponent(".luma-pair-\(UUID().uuidString).pending")
                let descriptor = pending.path.withCString { Darwin.open($0, O_WRONLY | O_CREAT | O_EXCL, 0o600) }
                guard descriptor >= 0 else { server.stop(); throw CocoaError(.fileWriteUnknown) }
                let file = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
                try file.write(contentsOf: data); try file.close()
                // Publish only complete JSON; the harness polls for this path.
                guard Darwin.link(pending.path, ready.path) == 0 else { server.stop(); try? FileManager.default.removeItem(at: pending); throw CocoaError(.fileWriteUnknown) }
                try FileManager.default.removeItem(at: pending)
                print("READY: production Mac server; original fixtures only; pairing metadata is private")
                let finish: @MainActor () -> Void = {
                    server.stop(); try? FileManager.default.removeItem(at: ready)
                    print("STOPPED: listener and paired access revoked"); exit(0)
                }
                DispatchQueue.global(qos: .utility).async {
                    while readLine() != nil { }
                    Task { @MainActor in finish() }
                }
                try await Task.sleep(for: .seconds(600))
                finish()
            } catch { print("FAIL: Mac interoperability host could not start"); exit(1) }
        }
        NSApplication.shared.run()
    }
}
