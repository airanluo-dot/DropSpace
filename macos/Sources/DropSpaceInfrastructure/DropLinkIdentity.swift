import Foundation
import CryptoKit
import Security
import DropSpaceCore

/// An immutable, per-installation TLS identity. This is a DropLink device
/// certificate, unrelated to Apple application signing or notarization.
public final class DropLinkIdentity: @unchecked Sendable {
    public let deviceID: UUID
    public let displayName: String
    public let certificate: SecCertificate
    public let identity: SecIdentity
    public let fingerprint: String
    public var platform: DevicePlatform { .macOS }
    private let stored: Stored
    private struct Stored: Codable {
        let version: Int
        let id: UUID
        let name: String
        let certificate: Data
        let privateKey: Data
    }
    private init(_ stored: Stored) throws {
        guard stored.version == 1, stored.id != .zero, stored.certificate.count < 16384,
              !stored.name.isEmpty, stored.name.utf16.count <= 64,
              !stored.name.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }),
              stored.privateKey.count == 97 else { throw DropSpaceError.invalid("Invalid DropLink identity") }
        var error: Unmanaged<CFError>?
        let attributes: [String: Any] = [kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom, kSecAttrKeyClass as String: kSecAttrKeyClassPrivate, kSecAttrKeySizeInBits as String: 256]
        guard let certificate = SecCertificateCreateWithData(nil, stored.certificate as CFData),
              let key = SecKeyCreateWithData(stored.privateKey as CFData, attributes as CFDictionary, &error),
              let identity = SecIdentityCreate(nil, certificate, key) else { throw DropSpaceError.invalid("DropLink certificate and private key do not match") }
        self.stored = stored; deviceID = stored.id; displayName = stored.name
        self.certificate = certificate; self.identity = identity; fingerprint = Digests.sha256(stored.certificate)
    }
    public static func create(name: String, now: Date = Date()) throws -> DropLinkIdentity {
        let id = UUID(), key = P256.Signing.PrivateKey()
        let clean = name.trimmingCharacters(in: .whitespacesAndNewlines)
        var units = Array((clean.isEmpty ? "Mac" : clean).utf16.prefix(64))
        if let last = units.last, (0xD800...0xDBFF).contains(last) { units.removeLast() }
        let certificate = try DropLinkCertificate.create(id: id, key: key, now: now)
        return try DropLinkIdentity(Stored(version: 1, id: id, name: String(decoding: units, as: UTF16.self), certificate: certificate, privateKey: key.x963Representation))
    }
    // Private material is encoded only for the app's Keychain value. Callers
    // receive no filesystem path or exported PFX and never install a trust anchor.
    func keychainData() throws -> Data { try JSONEncoder().encode(stored) }
    static func restore(_ data: Data) throws -> DropLinkIdentity {
        guard data.count <= 32768 else { throw DropSpaceError.limit("DropLink identity size") }
        return try DropLinkIdentity(JSONDecoder().decode(Stored.self, from: data))
    }
}

public actor DropLinkIdentityStore {
    private let keychain: KeychainStore
    private var loading: Task<DropLinkIdentity, any Error>?
    private var generation = UUID()
    public init(keychain: KeychainStore = KeychainStore()) { self.keychain = keychain }
    public func getOrCreate(name: String) async throws -> DropLinkIdentity {
        if loading == nil {
            let keychain = keychain; generation = UUID()
            loading = Task {
                if let bytes = try await keychain.read("droplink.identity.v1") { return try DropLinkIdentity.restore(bytes) }
                let identity = try DropLinkIdentity.create(name: name)
                try await keychain.save(identity.keychainData(), account: "droplink.identity.v1")
                return identity
            }
        }
        let request = loading!, token = generation
        do { let identity = try await request.value; try Task.checkCancellation(); return identity }
        catch { if generation == token { loading = nil }; throw error }
    }
}

/// Minimal DER writer for our fixed X.509 v3 certificate, not a general ASN.1
/// parser. Key generation and ECDSA/SHA-256 signing use CryptoKit and Security.
private enum DropLinkCertificate {
    private static func tlv(_ tag: UInt8, _ bytes: Data) -> Data {
        var length = Data()
        if bytes.count < 128 { length.append(UInt8(bytes.count)) }
        else {
            var value = bytes.count, encoded = [UInt8]()
            while value > 0 { encoded.insert(UInt8(value & 255), at: 0); value >>= 8 }
            length.append(0x80 | UInt8(encoded.count)); length.append(contentsOf: encoded)
        }
        return Data([tag]) + length + bytes
    }
    private static func sequence(_ values: Data...) -> Data { tlv(0x30, values.reduce(Data(), +)) }
    private static func oid(_ bytes: [UInt8]) -> Data { tlv(0x06, Data(bytes)) }
    private static func time(_ date: Date) -> Data {
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.timeZone = TimeZone(secondsFromGMT: 0)
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let generalized = calendar.component(.year, from: date) >= 2050
        formatter.dateFormat = generalized ? "yyyyMMddHHmmss'Z'" : "yyMMddHHmmss'Z'"
        return tlv(generalized ? 0x18 : 0x17, Data(formatter.string(from: date).utf8))
    }
    static func create(id: UUID, key: P256.Signing.PrivateKey, now: Date) throws -> Data {
        let algorithm = sequence(oid([0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02]))
        let name = sequence(tlv(0x31, sequence(oid([0x55, 0x04, 0x03]), tlv(0x0c, Data(("DropSpace-" + id.dropLinkN).utf8)))))
        var serial = Data(count: 16)
        let status = serial.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }
        guard status == errSecSuccess else { throw DropSpaceError.unavailable("DropLink certificate random source") }
        // Positive, minimal DER integer with at least 120 bits of randomness.
        serial[0] = (serial[0] & 0x7f) | 1
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        guard let end = calendar.date(byAdding: .year, value: 10, to: now) else { throw DropSpaceError.invalid("DropLink certificate validity") }
        let constraints = sequence(oid([0x55, 0x1d, 0x13]), tlv(0x04, sequence()))
        let usage = sequence(oid([0x55, 0x1d, 0x0f]), tlv(0x04, tlv(0x03, Data([5, 0xa0]))))
        let extended = sequence(oid([0x55, 0x1d, 0x25]), tlv(0x04, sequence(oid([0x2b, 6, 1, 5, 5, 7, 3, 1]), oid([0x2b, 6, 1, 5, 5, 7, 3, 2]))))
        let tbs = sequence(tlv(0xa0, tlv(0x02, Data([2]))), tlv(0x02, serial), algorithm, name,
                           sequence(time(now.addingTimeInterval(-60)), time(end)), name, key.publicKey.derRepresentation,
                           tlv(0xa3, sequence(constraints, usage, extended)))
        let signature = try key.signature(for: tbs).derRepresentation
        return sequence(tbs, algorithm, tlv(0x03, Data([0]) + signature))
    }
}
