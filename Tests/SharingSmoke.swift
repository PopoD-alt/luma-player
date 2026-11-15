import AppKit
import Foundation

@main struct SharingSmoke {
    enum Failure: Error { case check(String) }
    @MainActor static func main() {
        setbuf(stdout, nil); NSApplication.shared.setActivationPolicy(.accessory)
        Task { @MainActor in do { try await run(); exit(0) } catch { print("FAIL: \(error)"); exit(1) } }
        NSApplication.shared.run()
    }
    static func check(_ condition: @autoclosure () -> Bool, _ text: String) throws { if !condition() { throw Failure.check(text) } }
    @MainActor static func run() async throws {
        let root = URL(fileURLWithPath: CommandLine.arguments[1])
        let server = LocalSharingServer()
        let id = UUID().uuidString.lowercased(), largeID = UUID().uuidString.lowercased()
        let original = root.appendingPathComponent("Luma-Demo.mp4")
        let hevcID = UUID().uuidString.lowercased()
        let bytes = try Data(contentsOf: original)
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("Luma-sharing-test-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        defer { server.stop(); try? FileManager.default.removeItem(at: temporary) }
        let sparse = temporary.appendingPathComponent("sparse.mp4")
        FileManager.default.createFile(atPath: sparse.path, contents: nil)
        let writer = try FileHandle(forWritingTo: sparse)
        let largeSize = UInt64(5) * 1024 * 1024 * 1024
        try writer.truncate(atOffset: largeSize); try writer.seek(toOffset: largeSize - 8); try writer.write(contentsOf: Data("LUMA-END".utf8)); try writer.close()
        var items = [SharedMedia(id: id, url: original, title: "Original AVC test", durationSeconds: 12, resumeSeconds: 0.5),
                     SharedMedia(id: largeID, url: sparse, title: "Sparse range test", durationSeconds: 0, resumeSeconds: 0),
                     SharedMedia(id: hevcID, url: root.appendingPathComponent("Luma-HEVC10-AAC51.mp4"), title: "Original HEVC / AAC 5.1", durationSeconds: 2, resumeSeconds: 0)]
        server.start { items }
        try await wait { server.port > 0 }
        let base = URL(string: "http://127.0.0.1:\(server.port)")!
        let config = URLSessionConfiguration.ephemeral; config.timeoutIntervalForRequest = 5
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        func request(_ path: String, method: String = "GET", token: String? = nil, range: String? = nil, code: String? = nil) async throws -> (Data, HTTPURLResponse) {
            var request = URLRequest(url: URL(string: path, relativeTo: base)!.absoluteURL); request.httpMethod = method
            if let token { request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization") }
            if let range { request.setValue(range, forHTTPHeaderField: "Range") }
            if let code { request.setValue("application/json", forHTTPHeaderField: "Content-Type"); request.httpBody = try JSONSerialization.data(withJSONObject: ["code": code]) }
            let (data, response) = try await session.data(for: request)
            return (data, response as! HTTPURLResponse)
        }
        let (info, infoResponse) = try await request("/v1/info")
        try check(infoResponse.statusCode == 200 && !String(decoding: info, as: UTF8.self).contains(root.path), "Public info leaked a path or failed")
        let (_, denied) = try await request("/v1/playlist"); try check(denied.statusCode == 401, "Playlist without a token")
        let (paired, pairedResponse) = try await request("/v1/pair", method: "POST", code: server.pairingCode)
        let token = (try JSONSerialization.jsonObject(with: paired) as! [String: Any])["token"] as! String
        try check(pairedResponse.statusCode == 200 && token.count == 64, "Pairing/token generation")
        let (playlist, playlistResponse) = try await request("/v1/playlist", token: token)
        let listed = (try JSONSerialization.jsonObject(with: playlist) as! [String: Any])["items"] as! [[String: Any]]
        try check(playlistResponse.statusCode == 200 && listed.count == 3 && !String(decoding: playlist, as: UTF8.self).contains(root.path), "Opaque playlist")
        let path = listed.first(where: { $0["id"] as? String == id })!["streamPath"] as! String
        let (range, partial) = try await request(path, range: "bytes=17-63")
        try check(partial.statusCode == 206 && range == bytes[17...63] && partial.value(forHTTPHeaderField: "Content-Range") == "bytes 17-63/\(bytes.count)", "Exact range bytes")
        let (empty, head) = try await request(path, method: "HEAD", range: "bytes=0-31")
        try check(head.statusCode == 206 && empty.isEmpty && head.value(forHTTPHeaderField: "Content-Length") == "32", "HEAD range")
        let (suffix, _) = try await request(path, range: "bytes=-8"); try check(suffix == bytes.suffix(8), "Suffix bytes")
        let (tail, _) = try await request(path, range: "bytes=\(bytes.count - 8)-999999999"); try check(tail == bytes.suffix(8), "Clamped range")
        for header in ["bytes=9999999-", "bytes=9-3", "bytes=0-1,3-4", "bytes=-0", "bytes=18446744073709551616-"] {
            let (_, response) = try await request(path, range: header); try check(response.statusCode == 416, "Invalid range accepted: \(header)")
        }
        let largePath = listed.first(where: { $0["id"] as? String == largeID })!["streamPath"] as! String
        let (largeTail, largeResponse) = try await request(largePath, range: "bytes=-8")
        try check(largeResponse.statusCode == 206 && largeTail == Data("LUMA-END".utf8), "Sparse >4GiB streaming offset")
        let removal = RemovedStreamProbe()
        let delivered = try await removal.run(url: URL(string: largePath, relativeTo: base)!.absoluteURL) { items.removeAll { $0.id == largeID }; server.playlistDidChange() }
        try check(delivered > 0 && delivered < 16 * 1024 * 1024, "Removing an active item did not promptly stop its stream")
        let (_, traversal) = try await request("/v1/media/%2e%2e%2fprivate?token=" + token); try check(traversal.statusCode == 404, "Arbitrary filesystem path")
        print("PASS: actual Mac listener pairing/auth, opaque live playlist, exact GET/HEAD/suffix/clamped/invalid ranges, sparse 5GiB reads and removed-active-stream cancellation")

        let window = NSWindow(contentRect: NSRect(x: 100, y: 100, width: 640, height: 360), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false; window.orderFront(nil)
        let canvas = NSView(frame: window.contentView!.bounds); canvas.wantsLayer = true; window.contentView = canvas
        let player = try VLCBridge(); player.attach(canvas); player.volume(0)
        defer { player.shutdown(); window.close() }
        try player.load(URL(string: path, relativeTo: base)!.absoluteURL)
        do { try await wait { (player.statistics?.displayedVideo ?? 0) > 0 } }
        catch { print("Native HTTP playback state=\(player.state), time=\(player.time), stats=\(String(describing: player.statistics))"); throw error }
        print("HTTP decoded state=\(player.state), time=\(player.time), duration=\(player.duration), seekable=\(player.seekable), stats=\(String(describing: player.statistics))")
        player.pause(); try await wait { player.state == 4 }
        player.seek(3); try await wait { abs(player.time - 3) < 0.3 }
        player.play()
        do { try await wait { player.playing && player.time > 3.1 } }
        catch { print("HTTP resume state=\(player.state), time=\(player.time), stats=\(String(describing: player.statistics))"); throw error }
        player.unload()
        print("PASS: real libVLC HTTP playback from the production Mac sharing server decoded/displayed AVC and sought/resumed with actual native time; no saved video copy")
        let hevcPath = listed.first(where: { $0["id"] as? String == hevcID })!["streamPath"] as! String
        try player.load(URL(string: hevcPath, relativeTo: base)!.absoluteURL)
        try await wait { (player.statistics?.displayedVideo ?? 0) > 0 && (player.statistics?.decodedAudio ?? 0) > 0 }
        player.unload()
        print("PASS: actual libVLC HTTP decoded/displayed HEVC Main10 plus decoded AAC5.1 from the Mac sharing server; physical audio/A-V sync remain separate")

        items = []
        let (_, removed) = try await request(path); try check(removed.statusCode == 404, "Removed item stayed accessible")
        items = [SharedMedia(id: id, url: original, title: "Returned", durationSeconds: 2, resumeSeconds: 0)]
        for _ in 0..<4 { _ = try await request("/v1/pair", method: "POST", code: "badbad") }
        let (_, limited) = try await request("/v1/pair", method: "POST", code: server.pairingCode); try check(limited.statusCode == 429, "Pairing attempts not rate limited")
        server.stop(); server.start { items }; try await wait { server.port > 0 }
        var revokedRequest = URLRequest(url: URL(string: "http://127.0.0.1:\(server.port)/v1/playlist")!); revokedRequest.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        let (_, revoked) = try await session.data(for: revokedRequest); try check((revoked as! HTTPURLResponse).statusCode == 401, "Token survived stop/restart")
        server.stop()
        try check(server.activeStreams == 0, "Stop retained a streaming power activity")
        for host in ["127.0.0.1", "192.168.1.2", "10.1.2.3", "172.16.0.1", "172.31.255.254", "169.254.2.3", "::1", "fd00::1", "fe80::1%en0", "::ffff:192.168.1.2"] { try check(SharingHTTP.privateAddress(host), "Private address rejected") }
        for host in ["8.8.8.8", "172.15.1.1", "172.32.1.1", "0.0.0.0", "2001:4860:4860::8888", "example.com", "::ffff:8.8.8.8"] { try check(!SharingHTTP.privateAddress(host), "Public/hostname address accepted") }
        for raw in ["GET / HTTP/1.1\r\nContent-Length: 1\r\n\r\nx", "POST /v1/pair HTTP/1.1\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\nx", "POST /v1/pair HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n", "GET http://example.com HTTP/1.1\r\n\r\n"] {
            do { _ = try SharingHTTP.parse(Data(raw.utf8)); throw Failure.check("Malformed request accepted") } catch SharingHTTP.Failure.malformed { }
        }
        print("PASS: removal revokes new media access, pairing rate limit, stop/restart token revocation, private-address limits and malformed/pipelined request rejection")
    }
    @MainActor static func wait(_ condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(15)
        while !condition() { guard Date() < deadline else { throw Failure.check("Timed out") }; try await Task.sleep(for: .milliseconds(50)) }
    }
}

private final class RemovedStreamProbe: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let serial = OperationQueue()
    private var received: Int64 = 0
    private var removed = false
    private var hitLimit = false
    private var timedOut = false
    private var done: CheckedContinuation<Int64, Error>?
    private var remove: (@MainActor () -> Void)?
    private var session: URLSession?
    private var task: URLSessionDataTask?
    @MainActor func run(url: URL, remove: @escaping @MainActor () -> Void) async throws -> Int64 {
        serial.maxConcurrentOperationCount = 1; self.remove = remove
        return try await withCheckedThrowingContinuation { continuation in
            done = continuation
            let configuration = URLSessionConfiguration.ephemeral; configuration.timeoutIntervalForRequest = 8
            let session = URLSession(configuration: configuration, delegate: self, delegateQueue: serial)
            self.session = session; let task = session.dataTask(with: url); self.task = task; task.resume()
            DispatchQueue.global().asyncAfter(deadline: .now() + 10) { [weak self] in
                self?.serial.addOperation { [weak self] in guard let self, self.done != nil else { return }; self.timedOut = true; self.task?.cancel() }
            }
        }
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        received += Int64(data.count)
        if !removed { removed = true; let callback = remove; Task { @MainActor in callback?() } }
        if received >= 16 * 1024 * 1024 { hitLimit = true; dataTask.cancel() }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        let continuation = done; done = nil
        if received == 0 || hitLimit || timedOut { continuation?.resume(throwing: SharingSmoke.Failure.check("Active stream was not revoked within its bounded deadline")) }
        else { continuation?.resume(returning: received) }
        session.invalidateAndCancel(); self.session = nil; self.task = nil; remove = nil
    }
}
