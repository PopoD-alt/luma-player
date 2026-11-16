import Foundation
import Combine
import Network
import Security
import Darwin

@MainActor final class LocalSharingServer: ObservableObject {
    @Published private(set) var isSharing = false
    @Published private(set) var status = "Sharing is off"
    @Published private(set) var addresses: [String] = []
    @Published private(set) var pairingCode = ""
    @Published private(set) var pairedDevices = 0
    @Published private(set) var activeStreams = 0
    @Published private(set) var error: String?
    private(set) var pairingExpires = Date.distantPast
    private(set) var port: UInt16 = 0
    private var listener: NWListener?
    private let queue = DispatchQueue(label: "dev.sdas.luma.sharing-network", qos: .utility)
    private var clients: [UUID: SharingConnection] = [:]
    private var tokens = Set<String>()
    private var attempts: [(Date, String)] = []
    private var provider: () -> [SharedMedia] = { [] }
    private var progressConsumer: ((SharedProgressReport, Double) -> Bool)?
    private var progressAttempts: [String: [Date]] = [:]
    private let instanceID = UUID().uuidString.lowercased()
    private var epoch = UUID()
    private var streamIDs = Set<UUID>()
    private var awakeActivity: NSObjectProtocol?
    var name = "Luma on " + (Host.current().localizedName ?? "Mac")

    func start(provider: @escaping () -> [SharedMedia]) { start(provider: provider, onProgress: nil) }
    func start(provider: @escaping () -> [SharedMedia], onProgress: ((SharedProgressReport, Double) -> Bool)?) {
        stop(); error = nil; self.provider = provider; progressConsumer = onProgress; epoch = UUID(); let revision = epoch
        do {
            try renewPairingCode()
            let listener = try NWListener(using: .tcp, on: .any)
            let txt = ["v=1", "id=" + instanceID].reduce(into: Data()) { data, value in data.append(UInt8(value.utf8.count)); data.append(contentsOf: value.utf8) }
            var serviceName = ""
            for scalar in name.unicodeScalars { let next = String(scalar); if serviceName.utf8.count + next.utf8.count > 60 { break }; serviceName += next }
            listener.service = NWListener.Service(name: serviceName, type: "_luma-player._tcp", domain: nil, txtRecord: txt)
            listener.stateUpdateHandler = { [weak self, weak listener] state in
                Task { @MainActor in
                    guard let self, self.epoch == revision else { return }
                    switch state {
                    case .ready:
                        self.port = listener?.port?.rawValue ?? 0
                        self.addresses = Self.localAddresses().map { "http://\($0):\(self.port)" }
                        self.status = "Sharing on your local network"
                    case .failed(let failure): self.stop(); self.error = failure.localizedDescription; self.status = "Sharing could not start"
                    case .waiting: self.status = "Waiting for local network access"
                    default: break
                    }
                }
            }
            listener.newConnectionHandler = { [weak self] connection in
                Task { @MainActor in
                    guard let self, self.epoch == revision, self.isSharing, self.clients.count < 12 else { connection.cancel(); return }
                    guard case let .hostPort(host, _) = connection.endpoint, SharingHTTP.privateAddress(String(describing: host)) else { connection.cancel(); return }
                    let client = SharingConnection(connection: connection, server: self, peer: String(describing: host))
                    self.clients[client.id] = client; client.start(queue: self.queue)
                }
            }
            self.listener = listener; isSharing = true; status = "Starting local sharing…"
            listener.start(queue: queue)
        } catch { self.error = error.localizedDescription; status = "Sharing could not start"; isSharing = false }
    }
    func stop() {
        epoch = UUID(); listener?.cancel(); listener = nil
        let connections = Array(clients.values); clients.removeAll(); connections.forEach { $0.close() }
        tokens.removeAll(); pairedDevices = 0; pairingCode = ""; pairingExpires = .distantPast
        streamIDs.removeAll(); activeStreams = 0
        if let awakeActivity { ProcessInfo.processInfo.endActivity(awakeActivity); self.awakeActivity = nil }
        addresses = []; port = 0; isSharing = false; status = "Sharing is off"; attempts = []; provider = { [] }
        progressConsumer = nil; progressAttempts = [:]
    }
    func renewPairingCode() throws {
        var random: UInt32 = 0
        repeat {
            guard SecRandomCopyBytes(kSecRandomDefault, MemoryLayout.size(ofValue: random), &random) == errSecSuccess else { throw CocoaError(.coderInvalidValue) }
        } while random >= UInt32.max - UInt32.max % 1_000_000
        pairingCode = String(format: "%06u", random % 1_000_000); pairingExpires = Date().addingTimeInterval(600)
    }
    func playlistDidChange() { Array(clients.values).forEach { $0.validateLiveItem() } }
    fileprivate func finished(_ id: UUID) {
        clients.removeValue(forKey: id); streamIDs.remove(id); activeStreams = streamIDs.count
        if streamIDs.isEmpty, let awakeActivity { ProcessInfo.processInfo.endActivity(awakeActivity); self.awakeActivity = nil }
    }
    fileprivate func beganStream(_ id: UUID) {
        streamIDs.insert(id); activeStreams = streamIDs.count
        if awakeActivity == nil { awakeActivity = ProcessInfo.processInfo.beginActivity(options: [.idleSystemSleepDisabled], reason: "Streaming your playlist to a paired Luma device") }
    }
    fileprivate func allowsStream(id: String, url: URL) -> Bool {
        isSharing && provider().contains { $0.id == id && $0.url.standardizedFileURL == url.standardizedFileURL }
            && FileManager.default.fileExists(atPath: url.path)
    }
    fileprivate func route(_ request: SharingHTTP.Request, client: SharingConnection) {
        guard isSharing, let parts = URLComponents(string: "http://luma.local" + request.target), parts.fragment == nil else { client.reply(400); return }
        let path = parts.path
        if ProcessInfo.processInfo.environment["LUMA_SHARING_TRACE"] == "1" { print("Sharing request: \(request.method) \(path)") }
        if path == "/v1/info", request.method == "GET", parts.query == nil {
            client.json(["version": 1, "name": name, "id": instanceID, "pairingRequired": true,
                         "progressReporting": progressConsumer != nil]); return
        }
        if path == "/v1/pair", request.method == "POST", parts.query == nil {
            attempts.removeAll { Date().timeIntervalSince($0.0) > 60 }
            guard attempts.count < 20, attempts.filter({ $0.1 == client.peer }).count < 5, tokens.count < 16 else { client.reply(429); return }
            attempts.append((Date(), client.peer))
            guard request.body.count <= 1024, pairingExpires > Date(), let data = try? JSONSerialization.jsonObject(with: request.body) as? [String: Any],
                  let code = data["code"] as? String, code.utf8.count == 6, SharingHTTP.equalSecret(code, pairingCode) else { client.reply(401); return }
            var bytes = [UInt8](repeating: 0, count: 32)
            guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { client.reply(500); return }
            let token = bytes.map { String(format: "%02x", $0) }.joined()
            tokens.insert(token); pairedDevices = tokens.count
            client.json(["token": token, "name": name]); return
        }
        if path == "/v1/playlist", request.method == "GET", parts.query == nil {
            guard let token = bearer(request), authorized(token) else { client.reply(401); return }
            let available = provider().filter { $0.url.isFileURL && FileManager.default.fileExists(atPath: $0.url.path) }
            let items: [[String: Any]] = available.prefix(1000).map { item in
                var result: [String: Any] = ["id": item.id, "title": String(decoding: item.title.utf16.prefix(512), as: UTF16.self),
                 "durationSeconds": item.durationSeconds.isFinite ? min(31_536_000, max(0, item.durationSeconds)) : 0,
                 "resumeSeconds": item.resumeSeconds.isFinite ? min(31_536_000, max(0, item.resumeSeconds)) : 0,
                 "mimeType": SharingHTTP.mime(item.url), "streamPath": "/v1/media/\(item.id)?token=\(token)"]
                for (key, value) in [("computerProgressSeconds", item.computerProgressSeconds),
                                     ("mobileProgressSeconds", item.mobileProgressSeconds),
                                     ("mobileDurationSeconds", item.mobileDurationSeconds)] {
                    if let value, value.isFinite, (0...31_536_000).contains(value) { result[key] = value }
                }
                for (key, value) in [("computerUpdatedAtUnixSeconds", item.computerUpdatedAtUnixSeconds),
                                     ("mobileUpdatedAtUnixSeconds", item.mobileUpdatedAtUnixSeconds)] {
                    if let value, value.isFinite, value > 0, value <= 253_402_300_799 { result[key] = value }
                }
                if let completed = item.computerCompleted { result["computerCompleted"] = completed }
                if let completed = item.mobileCompleted { result["mobileCompleted"] = completed }
                return result
            }
            client.json(["version": 1, "name": name, "items": items, "truncated": available.count > items.count, "totalItems": available.count]); return
        }
        if path == "/v1/progress", request.method == "POST", parts.query == nil {
            guard let token = bearer(request), authorized(token) else { client.reply(401); return }
            guard let progressConsumer else { client.reply(404); return }
            let now = Date()
            var recent = (progressAttempts[token] ?? []).filter { now.timeIntervalSince($0) < 60 }
            guard recent.count < 30 else { client.reply(429); return }
            recent.append(now); progressAttempts[token] = recent
            guard request.headers["content-type"]?.split(separator: ";", maxSplits: 1).first?
                .trimmingCharacters(in: .whitespaces).lowercased() == "application/json",
                  let raw = try? SharedProgressReport.parse(request.body, hostDuration: 0) else { client.reply(400); return }
            guard let item = provider().first(where: { $0.id == raw.itemId }), item.url.isFileURL,
                  FileManager.default.fileExists(atPath: item.url.path) else { client.reply(404); return }
            guard let report = try? SharedProgressReport.parse(request.body, hostDuration: item.durationSeconds) else { client.reply(400); return }
            let updatedAt = now.timeIntervalSince1970
            guard progressConsumer(report, updatedAt) else { client.reply(404); return }
            client.json(["version": 1, "positionSeconds": report.positionSeconds,
                         "durationSeconds": report.durationSeconds, "completed": report.completed,
                         "updatedAtUnixSeconds": updatedAt]); return
        }
        if path.hasPrefix("/v1/media/"), ["GET", "HEAD"].contains(request.method) {
            let parameters = parts.queryItems ?? []
            guard parameters.count == 1, parameters[0].name == "token", let token = parameters[0].value, authorized(token) else { client.reply(401); return }
            let identifier = String(path.dropFirst("/v1/media/".count))
            guard UUID(uuidString: identifier) != nil, let item = provider().first(where: { $0.id == identifier }), item.url.isFileURL,
                  let file = try? SharingFile(url: item.url) else { client.reply(404); return }
            do {
                let range = try SharingHTTP.range(request.headers["range"], size: file.size)
                var headers = ["Content-Type": SharingHTTP.mime(item.url), "Accept-Ranges": "bytes"]
                if range.partial { headers["Content-Range"] = "bytes \(range.start)-\(range.start + range.length - 1)/\(file.size)" }
                client.stream(file: file, itemID: item.id, url: item.url, start: range.start, length: range.length, status: range.partial ? 206 : 200, headers: headers, head: request.method == "HEAD")
            } catch { file.close(); client.reply(416, headers: ["Content-Range": "bytes */\(file.size)", "Accept-Ranges": "bytes"]) }
            return
        }
        client.reply(404)
    }
    private func bearer(_ request: SharingHTTP.Request) -> String? {
        guard let header = request.headers["authorization"], header.hasPrefix("Bearer ") else { return nil }
        return String(header.dropFirst(7))
    }
    private func authorized(_ token: String) -> Bool {
        guard token.utf8.count == 64 else { return false }
        return tokens.contains { SharingHTTP.equalSecret($0, token) }
    }
    private static func localAddresses() -> [String] {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0 else { return ["127.0.0.1"] }
        defer { freeifaddrs(list) }
        var current = list, result: [String] = []
        while let item = current {
            defer { current = item.pointee.ifa_next }
            guard let address = item.pointee.ifa_addr, address.pointee.sa_family == AF_INET,
                  item.pointee.ifa_flags & UInt32(IFF_UP) != 0 else { continue }
            var text = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(address, socklen_t(address.pointee.sa_len), &text, socklen_t(text.count), nil, 0, NI_NUMERICHOST) == 0 {
                let host = String(cString: text)
                if SharingHTTP.privateAddress(host), !host.hasPrefix("127.") { result.append(host) }
            }
        }
        return result.isEmpty ? ["127.0.0.1"] : Array(Set(result)).sorted()
    }
}

@MainActor private final class SharingConnection {
    let id = UUID()
    let peer: String
    private let connection: NWConnection
    private weak var server: LocalSharingServer?
    private var input = Data()
    private var closed = false
    private var handled = false
    private var file: SharingFile?
    private var itemID: String?
    private var itemURL: URL?
    private var deadline: Task<Void, Never>?
    init(connection: NWConnection, server: LocalSharingServer, peer: String) { self.connection = connection; self.server = server; self.peer = peer }
    func start(queue: DispatchQueue) {
        connection.stateUpdateHandler = { [weak self] state in
            if case .failed = state { Task { @MainActor in self?.close() } }
            if case .cancelled = state { Task { @MainActor in self?.close() } }
        }
        connection.start(queue: queue)
        deadline = Task { [weak self] in try? await Task.sleep(for: .seconds(15)); guard !Task.isCancelled else { return }; self?.close() }
        receive()
    }
    private func receive() {
        guard !closed, !handled else { return }
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [weak self] data, _, complete, error in
            Task { @MainActor in
                guard let self, !self.closed else { return }
                if let data { self.input.append(data) }
                do {
                    if let request = try SharingHTTP.parse(self.input) {
                        self.handled = true; self.deadline?.cancel(); self.deadline = nil
                        self.server?.route(request, client: self)
                    } else if complete || error != nil { self.close() } else { self.receive() }
                } catch { self.reply(400) }
            }
        }
    }
    func json(_ value: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: value), data.count <= 4 * 1024 * 1024 else { reply(500); return }
        reply(200, headers: ["Content-Type": "application/json; charset=utf-8"], body: data)
    }
    private func header(_ status: Int, headers: [String: String], length: Int64) -> Data {
        let reason = [200: "OK", 206: "Partial Content", 400: "Bad Request", 401: "Unauthorized", 404: "Not Found", 416: "Range Not Satisfiable", 429: "Too Many Requests", 500: "Internal Server Error"][status] ?? "Error"
        var output = "HTTP/1.1 \(status) \(reason)\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Length: \(length)\r\n"
        for key in headers.keys.sorted() { output += "\(key): \(headers[key]!)\r\n" }
        return Data((output + "\r\n").utf8)
    }
    func reply(_ status: Int, headers: [String: String] = [:], body: Data = Data()) {
        guard !closed else { return }; handled = true
        if ProcessInfo.processInfo.environment["LUMA_SHARING_TRACE"] == "1" { print("Sharing reply: \(status), bytes=\(body.count)") }
        let payload = header(status, headers: headers, length: Int64(body.count)) + body
        send(payload) { [weak self] in self?.close() }
    }
    func stream(file: SharingFile, itemID: String, url: URL, start: Int64, length: Int64, status: Int, headers: [String: String], head: Bool) {
        if ProcessInfo.processInfo.environment["LUMA_SHARING_TRACE"] == "1" { print("Sharing stream: \(status), start=\(start), length=\(length), head=\(head)") }
        self.file = file
        self.itemID = itemID; itemURL = url
        if !head, length > 0 { server?.beganStream(id) }
        send(header(status, headers: headers, length: length)) { [weak self] in
            guard let self else { return }
            if head || length == 0 { self.close() } else { self.next(offset: start, remaining: length) }
        }
    }
    private func next(offset: Int64, remaining: Int64) {
        guard !closed, let file, let itemID, let itemURL, server?.allowsStream(id: itemID, url: itemURL) == true else { close(); return }
        file.read(at: offset, count: Int(min(262_144, remaining))) { [weak self] data in
            Task { @MainActor in
                guard let self, !self.closed, let data, !data.isEmpty else { self?.close(); return }
                guard let itemID = self.itemID, let itemURL = self.itemURL, self.server?.allowsStream(id: itemID, url: itemURL) == true else { self.close(); return }
                self.send(data) { [weak self] in
                    guard let self else { return }
                    let left = remaining - Int64(data.count)
                    if left > 0 { self.next(offset: offset + Int64(data.count), remaining: left) } else { self.close() }
                }
            }
        }
    }
    private func send(_ data: Data, completion: @escaping @MainActor () -> Void) {
        guard !closed else { return }
        deadline?.cancel()
        // A paired media client can pause while its TCP buffers fill. Keep
        // request/header timeouts short; media still cancels immediately when
        // the owner stops sharing or removes the item.
        let timeout: Double = file == nil ? 30 : 15 * 60
        deadline = Task { [weak self] in try? await Task.sleep(for: .seconds(timeout)); guard !Task.isCancelled else { return }; self?.close() }
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            Task { @MainActor in
                guard let self, !self.closed else { return }
                self.deadline?.cancel(); self.deadline = nil
                if error != nil { self.close() } else { completion() }
            }
        })
    }
    func validateLiveItem() {
        if let itemID, let itemURL, server?.allowsStream(id: itemID, url: itemURL) != true { close() }
    }
    func close() {
        guard !closed else { return }; closed = true; deadline?.cancel(); deadline = nil
        connection.stateUpdateHandler = nil; connection.cancel(); file?.close(); file = nil
        server?.finished(id)
    }
}
