import Foundation

struct UpdateConfiguration {
    let feedURL: URL
    let publicKey: String

    init?(info: [String: Any]) {
        guard let text = info["LumaUpdateFeedURL"] as? String,
              let url = URL(string: text), url.scheme == "https",
              let host = url.host, !host.isEmpty, url.user == nil, url.password == nil,
              let key = info["LumaUpdatePublicKey"] as? String,
              let bytes = Data(base64Encoded: key), bytes.count == 32 else { return nil }
        feedURL = url
        publicKey = key
    }
}
