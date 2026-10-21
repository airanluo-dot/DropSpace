import Foundation
import CryptoKit
import DropSpaceCore

public struct EncryptedShareManifestItem: Codable, Sendable, Equatable {
    public let fileId: UUID, displayName: String, mimeType: String, plainLength: Int64, chunkCount: Int32, sha256: String, noncePrefix: Data
    public init(fileID: UUID, name: String, mime: String, length: Int64, chunks: Int32, hash: String, noncePrefix: Data) {
        fileId = fileID; displayName = name; mimeType = mime; plainLength = length; chunkCount = chunks; sha256 = hash; self.noncePrefix = noncePrefix
    }
    private enum CodingKeys: String, CodingKey { case fileId, displayName, mimeType, plainLength, chunkCount, sha256, noncePrefix }
    public func encode(to encoder: any Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        // The existing browser compares text and builds case-sensitive object
        // paths. Foundation UUID's uppercase encoding is not wire-compatible.
        try values.encode(fileId.dropLinkD, forKey: .fileId); try values.encode(displayName, forKey: .displayName)
        try values.encode(mimeType, forKey: .mimeType); try values.encode(plainLength, forKey: .plainLength)
        try values.encode(chunkCount, forKey: .chunkCount); try values.encode(sha256, forKey: .sha256); try values.encode(noncePrefix, forKey: .noncePrefix)
    }
}
public enum ShareCrypto {
    private struct Manifest: Codable {
        let shareId: UUID; let items: [EncryptedShareManifestItem]
        private enum CodingKeys: String, CodingKey { case shareId, items }
        func encode(to encoder: any Encoder) throws {
            var values = encoder.container(keyedBy: CodingKeys.self)
            try values.encode(shareId.dropLinkD, forKey: .shareId); try values.encode(items, forKey: .items)
        }
    }
    public static func createMasterKey() -> SymmetricKey { SymmetricKey(size: .bits256) }
    /// Salt order is .NET Guid.ToByteArray, not RFC4122 order. This is part of
    /// the published Windows/browser protocol and cannot be platform-specific.
    public static func guidWireBytes(_ id: UUID) -> Data {
        var value = id.uuid
        let bytes = withUnsafeBytes(of: &value) { Array($0) }
        return Data([bytes[3], bytes[2], bytes[1], bytes[0], bytes[5], bytes[4], bytes[7], bytes[6]] + bytes[8...15])
    }
    private static func derived(_ key: SymmetricKey, share: UUID, info: String) throws -> SymmetricKey {
        guard key.bitCount == 256, share != .zero else { throw DropSpaceError.invalid("Share key or identity") }
        return HKDF<SHA256>.deriveKey(inputKeyMaterial: key, salt: guidWireBytes(share), info: Data(info.utf8), outputByteCount: 32)
    }
    private static func nonce(prefix: Data, index: Int32) throws -> AES.GCM.Nonce {
        guard prefix.count == 8, index >= 0 else { throw DropSpaceError.invalid("Share chunk nonce") }
        var number = UInt32(index).bigEndian
        return try AES.GCM.Nonce(data: prefix + withUnsafeBytes(of: &number) { Data($0) })
    }
    private static func aad(share: UUID, file: UUID, index: Int32, length: Int) -> Data {
        Data("DropSpaceShare:v1\n\(share.dropLinkN)\n\(file.dropLinkN)\n\(index)\n\(length)".utf8)
    }
    public static func encryptChunk(key: SymmetricKey, share: UUID, file: UUID, index: Int32, plaintext: Data, noncePrefix: Data) throws -> Data {
        guard file != .zero else { throw DropSpaceError.invalid("Share file identity") }
        let box = try AES.GCM.seal(plaintext, using: derived(key, share: share, info: "file:" + file.dropLinkN), nonce: nonce(prefix: noncePrefix, index: index), authenticating: aad(share: share, file: file, index: index, length: plaintext.count))
        return box.ciphertext + box.tag
    }
    public static func decryptChunk(key: SymmetricKey, share: UUID, file: UUID, index: Int32, plainLength: Int, wire: Data, noncePrefix: Data) throws -> Data {
        guard file != .zero, plainLength >= 0, wire.count >= 16, wire.count - 16 == plainLength else { throw DropSpaceError.invalid("Encrypted share chunk metadata") }
        let box = try AES.GCM.SealedBox(nonce: nonce(prefix: noncePrefix, index: index), ciphertext: wire.dropLast(16), tag: wire.suffix(16))
        return try AES.GCM.open(box, using: derived(key, share: share, info: "file:" + file.dropLinkN), authenticating: aad(share: share, file: file, index: index, length: plainLength))
    }
    public static func encryptManifest(key: SymmetricKey, share: UUID, items: [EncryptedShareManifestItem]) throws -> Data {
        try encryptManifest(key: key, share: share, items: items, nonce: AES.GCM.Nonce().withUnsafeBytes { Data($0) })
    }
    static func encryptManifest(key: SymmetricKey, share: UUID, items: [EncryptedShareManifestItem], nonce: Data) throws -> Data {
        guard nonce.count == 12 else { throw DropSpaceError.invalid("Share manifest nonce") }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        var plaintext = try encoder.encode(Manifest(shareId: share, items: items))
        defer { plaintext.resetBytes(in: plaintext.startIndex..<plaintext.endIndex) }
        let box = try AES.GCM.seal(plaintext, using: derived(key, share: share, info: "manifest"), nonce: AES.GCM.Nonce(data: nonce), authenticating: Data(("DropSpaceShare:v1\n" + share.dropLinkN).utf8))
        return nonce + box.ciphertext + box.tag
    }
    public static func decryptManifest(key: SymmetricKey, share: UUID, wire: Data) throws -> [EncryptedShareManifestItem] {
        guard wire.count >= 28 else { throw DropSpaceError.invalid("Truncated encrypted share manifest") }
        let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: wire.prefix(12)), ciphertext: wire.dropFirst(12).dropLast(16), tag: wire.suffix(16))
        var plaintext = try AES.GCM.open(box, using: derived(key, share: share, info: "manifest"), authenticating: Data(("DropSpaceShare:v1\n" + share.dropLinkN).utf8))
        defer { plaintext.resetBytes(in: plaintext.startIndex..<plaintext.endIndex) }
        let manifest = try JSONDecoder().decode(Manifest.self, from: plaintext)
        guard manifest.shareId == share else { throw DropSpaceError.invalid("Manifest belongs to another share") }; return manifest.items
    }
    public static func fragment(_ key: SymmetricKey) throws -> String {
        guard key.bitCount == 256 else { throw DropSpaceError.invalid("Share master key") }
        return key.withUnsafeBytes { Data($0).base64EncodedString() }.replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
    public static func key(fragment: String) throws -> SymmetricKey {
        var text = fragment.hasPrefix("#k=") ? String(fragment.dropFirst(3)) : fragment
        text = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        text += String(repeating: "=", count: (4 - text.utf16.count % 4) % 4)
        guard var bytes = DropLinkEncoding.base64(text), bytes.count == 32 else { throw DropSpaceError.invalid("Secure share URL key") }
        defer { bytes.resetBytes(in: bytes.startIndex..<bytes.endIndex) }; return SymmetricKey(data: bytes)
    }
}
