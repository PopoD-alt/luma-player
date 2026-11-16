import Foundation

/// Exercises the actual production listener and store with authored fixtures.
/// No video output or saved personal library is opened by this test.
@main struct SharingProgressSmoke {
    enum Failure: Error { case check(String) }
    static func check(_ condition: @autoclosure () throws -> Bool, _ text: String) throws {
        if try condition() == false { throw Failure.check(text) }
    }
    @MainActor static func main() async throws {
        let fixtures = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("luma-progress-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        let library = LibraryStore(storageURL: temporary.appendingPathComponent("library.json"))
        let added = library.add(urls: [fixtures.appendingPathComponent("Luma-Demo.mp4"), fixtures.appendingPathComponent("Luma-HEVC10-AAC51.mp4")])
        let id = added[0].id, opaque = id.uuidString.lowercased()
        library.updateProgress(id: id, time: 3, duration: 12)
        let server = LocalSharingServer()
        defer { server.stop(); try? FileManager.default.removeItem(at: temporary) }
        var accepted = 0
        func snapshot() -> [SharedMedia] {
            library.items.map { item in SharedMedia(id: item.id.uuidString.lowercased(), url: item.url, title: item.title,
                durationSeconds: item.duration, resumeSeconds: item.resumeTime,
                computerProgressSeconds: item.computerProgressSeconds, computerUpdatedAtUnixSeconds: item.computerUpdatedAtUnixSeconds,
                computerCompleted: item.computerCompleted, mobileProgressSeconds: item.mobileProgressSeconds,
                mobileDurationSeconds: item.mobileDurationSeconds, mobileUpdatedAtUnixSeconds: item.mobileUpdatedAtUnixSeconds,
                mobileCompleted: item.mobileCompleted) }
        }
        func start(writable: Bool = true) async throws {
            server.start(provider: snapshot, onProgress: writable ? { report, stamp in
                accepted += 1
                return library.updateMobileProgress(id: UUID(uuidString: report.itemId)!, time: report.positionSeconds,
                    duration: report.durationSeconds, completed: report.completed, updatedAt: stamp)
            } : nil)
            let deadline = Date().addingTimeInterval(15)
            while server.port == 0 {
                guard server.isSharing && Date() < deadline else { throw Failure.check("Listener did not start") }
                try await Task.sleep(for: .milliseconds(30))
            }
        }
        try await start()
        var base = URL(string: "http://127.0.0.1:\(server.port)")!
        let config = URLSessionConfiguration.ephemeral; config.timeoutIntervalForRequest = 5
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        func request(_ path: String, method: String = "GET", token: String? = nil, body: Data? = nil) async throws -> (Data, Int) {
            var request = URLRequest(url: URL(string: path, relativeTo: base)!.absoluteURL)
            request.httpMethod = method; request.httpBody = body
            if body != nil { request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type") }
            if let token { request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization") }
            let (data, response) = try await session.data(for: request)
            return (data, (response as! HTTPURLResponse).statusCode)
        }
        func json(_ data: Data) throws -> [String: Any] { try JSONSerialization.jsonObject(with: data) as! [String: Any] }
        func payload(_ item: String = opaque, position: Double = 5, duration: Double = 999, completed: Bool = false) throws -> Data {
            try JSONSerialization.data(withJSONObject: ["itemId": item, "positionSeconds": position, "durationSeconds": duration, "completed": completed])
        }
        func pair() async throws -> String {
            let body = try JSONSerialization.data(withJSONObject: ["code": server.pairingCode])
            let (data, status) = try await request("/v1/pair", method: "POST", body: body)
            try check(status == 200, "Pairing failed")
            return try json(data)["token"] as! String
        }
        let (info, _) = try await request("/v1/info")
        try check(try json(info)["progressReporting"] as? Bool == true, "Writable host must advertise progress capability")
        let token = try await pair()
        let good = try payload()
        let (_, denied) = try await request("/v1/progress", method: "POST", body: Data([0xff]))
        try check(denied == 401 && accepted == 0, "Unauthorized progress must be rejected before JSON decoding or mutation")
        let (response, status) = try await request("/v1/progress", method: "POST", token: token, body: good)
        let result = try json(response)
        try check(status == 200 && result["positionSeconds"] as? Double == 5 && result["durationSeconds"] as? Double == 12,
                  "Canonical host duration was not used")
        try check(accepted == 1 && library.items[0].resumeTime == 3 && library.items[0].duration == 12,
                  "Phone metadata moved the desktop or overwrote host duration")
        let (listed, _) = try await request("/v1/playlist", token: token)
        let item = (try json(listed)["items"] as! [[String: Any]])[0]
        try check(item["resumeSeconds"] as? Double == 3 && item["computerProgressSeconds"] as? Double == 3
                  && item["mobileProgressSeconds"] as? Double == 5 && item["mobileDurationSeconds"] as? Double == 12
                  && (item["mobileUpdatedAtUnixSeconds"] as? Double ?? 0) > 0,
                  "Independent checkpoints were not published")
        try check(!String(decoding: listed, as: UTF8.self).contains(fixtures.path), "Playlist leaked a filesystem path")
        let (_, clipped) = try await request("/v1/progress", method: "POST", token: token, body: try payload(position: 50))
        try check(clipped == 200 && library.items[0].mobileProgressSeconds == 12, "Known duration did not clamp position")
        let (_, finished) = try await request("/v1/progress", method: "POST", token: token, body: try payload(position: 1, completed: true))
        try check(finished == 200 && library.items[0].mobileCompleted == true && library.items[0].mobileProgressSeconds == 12,
                  "Completed phone progress was not retained")
        let unknownID = added[1].id.uuidString.lowercased()
        let (_, unknown) = try await request("/v1/progress", method: "POST", token: token,
                                            body: try payload(unknownID, position: 1, duration: 2))
        try check(unknown == 200 && library.items[1].duration == 0 && library.items[1].mobileDurationSeconds == 2,
                  "Phone duration replaced unknown computer metadata")

        let invalid = [Data([0xff]), Data("{\"itemId\":\"\(opaque)\",\"positionSeconds\":true,\"durationSeconds\":12,\"completed\":false}".utf8),
            Data("{\"itemId\":\"\(opaque)\",\"positionSeconds\":1e400,\"durationSeconds\":12,\"completed\":false}".utf8),
            Data("{\"itemId\":\"\(opaque)\",\"positionSeconds\":5,\"durationSeconds\":12,\"completed\":1}".utf8),
            Data("{\"itemId\":\"\(opaque)\",\"positionSeconds\":5,\"durationSeconds\":12,\"completed\":false,\"path\":\"/private/file\"}".utf8),
            try payload(position: -1), try payload(duration: 31_536_001), try payload(position: 31_536_001),
            Data("{}".utf8), Data(repeating: 32, count: 4097)]
        let countBeforeInvalid = accepted
        for body in invalid {
            let (_, status) = try await request("/v1/progress", method: "POST", token: token, body: body)
            try check(status == 400, "Unsafe progress body accepted")
        }
        try check(accepted == countBeforeInvalid, "Unsafe progress mutated a checkpoint")
        var padded = good; padded.append(Data(repeating: 32, count: 4096 - good.count))
        let (_, bound) = try await request("/v1/progress", method: "POST", token: token, body: padded)
        try check(bound == 200, "A valid 4096-byte bounded body was rejected")
        library.remove(added[1].id)
        let (_, removed) = try await request("/v1/progress", method: "POST", token: token,
                                            body: try payload(unknownID, position: 1, duration: 2))
        try check(removed == 404, "Removed item accepted a checkpoint")
        print("PASS: real Mac authenticated progress POST, strict body/finite/type bounds, canonical duration, independent persisted metadata, optional capability/playlist fields and removed-item rejection")

        server.stop(); try await start(); base = URL(string: "http://127.0.0.1:\(server.port)")!
        let (_, revoked) = try await request("/v1/progress", method: "POST", token: token, body: good)
        try check(revoked == 401, "Progress token survived stop/restart")
        let fresh = try await pair()
        for _ in 0..<30 {
            let (_, status) = try await request("/v1/progress", method: "POST", token: fresh, body: good)
            try check(status == 200, "Progress limiter rejected permitted budget")
        }
        let (_, throttled) = try await request("/v1/progress", method: "POST", token: fresh, body: good)
        try check(throttled == 429, "Progress limiter did not reject the thirty-first update")
        server.stop(); try await start(writable: false); base = URL(string: "http://127.0.0.1:\(server.port)")!
        let (readOnlyInfo, _) = try await request("/v1/info")
        try check(try json(readOnlyInfo)["progressReporting"] as? Bool == false, "Read-only fixture falsely advertised progress")
        let readOnlyToken = try await pair()
        let beforeReadOnly = accepted
        let (_, readOnly) = try await request("/v1/progress", method: "POST", token: readOnlyToken, body: good)
        try check(readOnly == 404 && accepted == beforeReadOnly, "Missing consumer accepted a mutation")
        library.flush()
        let restored = LibraryStore(storageURL: library.storageURL)
        try check(restored.items[0].mobileProgressSeconds == 5 && restored.items[0].resumeTime == 3,
                  "Received checkpoint did not survive store reload independently")
        print("PASS: progress rate30/token/min, stop/restart revocation, read-only compatibility and source-store checkpoint persistence")
    }
}
