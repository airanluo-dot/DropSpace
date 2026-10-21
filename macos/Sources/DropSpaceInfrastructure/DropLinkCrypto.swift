import Foundation
import CryptoKit
import Security
import DropSpaceCore

public enum DropLinkCrypto {
    public static func makeHello(deviceID: UUID,name: String,fingerprint: String,capabilities: PeerCapabilities) throws -> (P256.KeyAgreement.PrivateKey,PairingHello) {
        let key = P256.KeyAgreement.PrivateKey()
        var nonce = Data(count: 32)
        let status = nonce.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault,32,$0.baseAddress!) }
        guard status == errSecSuccess else { throw DropSpaceError.unavailable("Secure random generator failed") }
        let hello = PairingHello(deviceID: deviceID,name: name,platform: .macOS,fingerprint: fingerprint,capabilities: capabilities,publicKey: key.publicKey.derRepresentation.base64EncodedString(),nonce: nonce.base64EncodedString())
        try hello.validate(); return (key,hello)
    }
    public static func deriveSecret(privateKey: P256.KeyAgreement.PrivateKey,local: PairingHello,remote: PairingHello) throws -> SymmetricKey {
        try local.validate(); try remote.validate()
        guard DropLinkEncoding.base64(local.publicKeyBase64) == privateKey.publicKey.derRepresentation else { throw DropSpaceError.invalid("Pairing key identity mismatch") }
        let remoteKey = try P256.KeyAgreement.PublicKey(derRepresentation: DropLinkEncoding.base64(remote.publicKeyBase64)!)
        let shared = try privateKey.sharedSecretFromKeyAgreement(with: remoteKey)
        // .NET ECDiffieHellman.DeriveKeyMaterial hashes the raw ECDH value with SHA-256.
        // Passing raw CryptoKit ECDH directly to HKDF would break Windows v1 interoperability.
        let hashedShared = shared.withUnsafeBytes { SHA256.hash(data: Data($0)) }
        let ordered = ordered(local,remote)
        let salt = SHA256.hash(data: Data((ordered.0.nonceBase64 + "|" + ordered.1.nonceBase64).utf8))
        return HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: hashedShared),salt: Data(salt),info: Data("DropLink pairing v1".utf8),outputByteCount: 32)
    }
    public static func sas(secret: SymmetricKey,first: PairingHello,second: PairingHello) -> Int {
        let pair = ordered(first,second)
        let values = [pair.0,pair.1].flatMap { [$0.protocol.description,$0.deviceId.dropLinkN,$0.identityFingerprint,$0.publicKeyBase64,$0.nonceBase64] }
        let digest = Array(HMAC<SHA256>.authenticationCode(for: Data(values.joined(separator:"\n").utf8),using:secret))
        let value = UInt32(digest[0]) | UInt32(digest[1]) << 8 | UInt32(digest[2]) << 16 | UInt32(digest[3]) << 24
        return Int(value % 1_000_000)
    }
    public static func authentication(secret: SymmetricKey,method: String,path: String,nonce: String,bodyHash: String) -> String {
        Data(HMAC<SHA256>.authenticationCode(for: authBytes(method:method,path:path,nonce:nonce,bodyHash:bodyHash),using:secret)).base64EncodedString()
    }
    public static func verifyAuthentication(_ supplied: String,secret: SymmetricKey,method: String,path: String,nonce: String,bodyHash: String) -> Bool {
        guard let code = DropLinkEncoding.base64(supplied), code.count == 32,DropLinkEncoding.base64(nonce)?.count == 24,bodyHash.utf8.count == 64,bodyHash.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else { return false }
        return HMAC<SHA256>.isValidAuthenticationCode(code,authenticating:authBytes(method:method,path:path,nonce:nonce,bodyHash:bodyHash),using:secret)
    }
    private static func authBytes(method: String,path: String,nonce: String,bodyHash: String) -> Data { Data(["DropLink:v1",method.uppercased(),path,nonce,bodyHash].joined(separator:"\n").utf8) }
    private static func ordered(_ a: PairingHello,_ b: PairingHello) -> (PairingHello,PairingHello) { a.deviceId.dropLinkN <= b.deviceId.dropLinkN ? (a,b) : (b,a) }
}
public actor DropLinkNonceCache {
    private var entries: [String:Date] = [:]
    public init() {}
    public func reserve(peer: UUID,nonce: String,now: Date = Date()) -> Bool {
        guard peer != .zero,!nonce.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty,nonce.utf16.count <= 256 else { return false }
        entries = entries.filter { now.timeIntervalSince($0.value) <= 600 }
        let prefix = peer.dropLinkN + ":", key = prefix + nonce
        guard entries[key] == nil,entries.count < 16384,entries.keys.filter({ $0.hasPrefix(prefix) }).count < 4096 else { return false }
        entries[key] = now; return true
    }
    public func release(peer: UUID,nonce: String) { entries.removeValue(forKey: peer.dropLinkN + ":" + nonce) }
    public func clear() { entries.removeAll() }
}
