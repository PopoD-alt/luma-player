import Foundation
import Security
import CryptoKit

/// The private release key stays in the macOS login Keychain and is never printed.
@main struct UpdateSigner {
    static func main() throws {
        let args = Array(CommandLine.arguments.dropFirst())
        let account = "sdas-luma-updates"
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "dev.sdas.lumaplayer.release-signing", kSecAttrAccount as String: account]
        var lookup = query
        lookup[kSecReturnData as String] = true
        lookup[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(lookup as CFDictionary, &result)
        let key: Curve25519.Signing.PrivateKey
        if status == errSecItemNotFound && args == ["--generate-key"] {
            key = Curve25519.Signing.PrivateKey()
            var addition = query
            addition[kSecValueData as String] = key.rawRepresentation
            addition[kSecAttrLabel as String] = "Luma Player release signing (sdas)"
            guard SecItemAdd(addition as CFDictionary, nil) == errSecSuccess else { throw NSError(domain: "LumaKeychain", code: 1) }
        } else {
            guard status == errSecSuccess, let data = result as? Data else {
                fputs("No accessible Luma signing key. Run --generate-key once and approve the Keychain prompt.\n", stderr); exit(1)
            }
            key = try Curve25519.Signing.PrivateKey(rawRepresentation: data)
        }
        if args == ["--generate-key"] || args == ["--public-key"] {
            print(key.publicKey.rawRepresentation.base64EncodedString())
        } else if args.count == 3, args[0] == "--sign" {
            let payload = try Data(contentsOf: URL(fileURLWithPath: args[1]))
            let envelope = SignedRelease.Envelope(payload: payload.base64EncodedString(), signature: try key.signature(for: payload).base64EncodedString())
            let data = try JSONEncoder().encode(envelope)
            _ = try SignedRelease.verify(data, publicKey: key.publicKey.rawRepresentation.base64EncodedString())
            try data.write(to: URL(fileURLWithPath: args[2]), options: .atomic)
        } else { fputs("Usage: update-signer --generate-key | --public-key | --sign payload.json manifest.json\n", stderr); exit(1) }
    }
}
