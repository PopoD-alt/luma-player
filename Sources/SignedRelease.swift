import Foundation
import CryptoKit

struct SignedRelease: Codable, Equatable {
    let version: String
    let build: Int
    let url: URL
    let sha256: String
    let size: Int64
    let notes: String

    enum ValidationError: Error { case invalidManifest, invalidSignature, invalidDownload }
    struct Envelope: Codable { let payload: String; let signature: String }

    static func verify(_ data: Data, publicKey: String) throws -> SignedRelease {
        guard data.count <= 262_144 else { throw ValidationError.invalidManifest }
        let envelope = try JSONDecoder().decode(Envelope.self, from: data)
        guard let payload = Data(base64Encoded: envelope.payload),
              let signature = Data(base64Encoded: envelope.signature), signature.count == 64,
              let key = Data(base64Encoded: publicKey), key.count == 32 else { throw ValidationError.invalidManifest }
        let verifier = try Curve25519.Signing.PublicKey(rawRepresentation: key)
        guard verifier.isValidSignature(signature, for: payload) else { throw ValidationError.invalidSignature }
        let release = try JSONDecoder().decode(SignedRelease.self, from: payload)
        guard release.build > 0, !release.version.isEmpty, release.version.count < 80,
              release.url.scheme == "https", release.url.host != nil,
              release.url.user == nil, release.url.password == nil,
              release.sha256.count == 64,
              release.sha256.allSatisfy({ "0123456789abcdef".contains($0) }),
              release.size > 0, release.size <= 1_073_741_824,
              release.notes.count <= 16_384 else { throw ValidationError.invalidManifest }
        return release
    }

    func verifyDownload(at url: URL) throws {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard (attributes[.size] as? NSNumber)?.int64Value == size else { throw ValidationError.invalidDownload }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hash = SHA256()
        while let block = try handle.read(upToCount: 1_048_576), !block.isEmpty { hash.update(data: block) }
        let digest = hash.finalize().map { String(format: "%02x", $0) }.joined()
        guard digest == sha256 else { throw ValidationError.invalidDownload }
    }
}
