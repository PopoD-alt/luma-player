import Foundation
import CryptoKit

@main struct UpdateSmoke {
    static func main() throws {
        let signer = Curve25519.Signing.PrivateKey()
        let publicKey = signer.publicKey.rawRepresentation.base64EncodedString()
        let archive = Data("synthetic release archive".utf8)
        let digest = SHA256.hash(data: archive).map { String(format: "%02x", $0) }.joined()
        let release = SignedRelease(version: "1.0.1", build: 3, url: URL(string: "https://example.com/Luma.zip")!, sha256: digest, size: Int64(archive.count), notes: "Test release")
        func envelope(_ item: SignedRelease) throws -> Data {
            let payload = try JSONEncoder().encode(item)
            return try JSONEncoder().encode(SignedRelease.Envelope(payload: payload.base64EncodedString(), signature: signer.signature(for: payload).base64EncodedString()))
        }
        func rejects(_ label: String, _ action: () throws -> Void) {
            do { try action() } catch { return }
            fatalError("Accepted invalid input: \(label)")
        }
        let valid = try envelope(release)
        let verified = try SignedRelease.verify(valid, publicKey: publicKey)
        precondition(verified == release)
        precondition(UpdateArchiveKind(url: verified.url) == .zip)
        let diskImage = SignedRelease(version: "1.0.2", build: 4, url: URL(string: "https://example.com/Luma.DMG?download=1")!, sha256: digest, size: Int64(archive.count), notes: "Synthetic DMG-kind verification")
        let verifiedDiskImage = try SignedRelease.verify(envelope(diskImage), publicKey: publicKey)
        precondition(UpdateArchiveKind(url: verifiedDiskImage.url) == .dmg)
        precondition(UpdateArchiveKind.dmg.filenameExtension == "dmg" && UpdateArchiveKind.zip.filenameExtension == "zip")
        precondition(UpdateArchiveKind.dmg.installationInstructions.contains("DMG") && UpdateArchiveKind.zip.installationInstructions.contains("ZIP"))
        for url in ["https://example.com/Luma.pkg", "https://example.com/Luma.app", "https://example.com/Luma.tar.gz", "https://example.com/download?filename=Luma.dmg"] {
            precondition(UpdateArchiveKind(url: URL(string: url)!) == nil, "Unsupported archive kind was accepted")
        }
        rejects("wrong publisher key") { _ = try SignedRelease.verify(valid, publicKey: Curve25519.Signing.PrivateKey().publicKey.rawRepresentation.base64EncodedString()) }
        let tampered = try JSONEncoder().encode(SignedRelease.Envelope(payload: Data("tampered".utf8).base64EncodedString(), signature: Data(repeating: 0, count: 64).base64EncodedString()))
        rejects("tampered payload") { _ = try SignedRelease.verify(tampered, publicKey: publicKey) }
        let insecure = SignedRelease(version: "1.0.1", build: 3, url: URL(string: "http://example.com/Luma.zip")!, sha256: digest, size: Int64(archive.count), notes: "")
        rejects("HTTP archive") { _ = try SignedRelease.verify(envelope(insecure), publicKey: publicKey) }
        rejects("oversized manifest") { _ = try SignedRelease.verify(Data(repeating: 0, count: 262_145), publicKey: publicKey) }
        let temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: temp) }
        try archive.write(to: temp)
        try verified.verifyDownload(at: temp)
        try verifiedDiskImage.verifyDownload(at: temp)
        try Data(repeating: 0, count: archive.count).write(to: temp)
        rejects("archive digest mismatch") { try verified.verifyDownload(at: temp) }
        try Data().write(to: temp)
        rejects("truncated archive") { try verified.verifyDownload(at: temp) }
        precondition(UpdateConfiguration(info: [:]) == nil)
        precondition(UpdateConfiguration(info: ["LumaUpdateFeedURL": "http://example.com/feed.json", "LumaUpdatePublicKey": publicKey]) == nil)
        precondition(UpdateConfiguration(info: ["LumaUpdateFeedURL": "https://example.com/feed.json", "LumaUpdatePublicKey": publicKey]) != nil)
        print("PASS: publisher signature, wrong-key/tamper rejection, HTTPS/size validation, ZIP/DMG suffix selection, unsupported archive rejection, archive digest/length, preview configuration")
    }
}
