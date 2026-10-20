import Foundation

public enum DevicePlatform: Int,Codable,Sendable { case unknown,windows,macOS,iOS,iPadOS,android,linux }
public struct PeerCapabilities: OptionSet,Codable,Sendable,Equatable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }
    public static let handoffFiles = Self(rawValue: 1 << 0)
    public static let handoffFolders = Self(rawValue: 1 << 1)
    public static let handoffText = Self(rawValue: 1 << 2)
    public static let handoffURL = Self(rawValue: 1 << 3)
    public static let clipboardText = Self(rawValue: 1 << 4)
    public static let clipboardURL = Self(rawValue: 1 << 5)
    public static let clipboardImage = Self(rawValue: 1 << 6)
    public static let nearbyBrowserShare = Self(rawValue: 1 << 7)
    public init(from decoder: any Decoder) throws { self.init(rawValue: try decoder.singleValueContainer().decode(Int.self)) }
    public func encode(to encoder: any Encoder) throws { var container = encoder.singleValueContainer(); try container.encode(rawValue) }
}
public struct DropLinkVersion: Codable,Sendable,Equatable {
    public let major: Int; public let minor: Int
    public init(major: Int = 1,minor: Int = 0) { self.major = major; self.minor = minor }
    public var description: String { "\(major).\(minor)" }
}
public struct PairingHello: Codable,Sendable {
    public var `protocol`: DropLinkVersion
    public var deviceId: UUID
    public var displayName: String
    public var platform: DevicePlatform
    public var identityFingerprint: String
    public var capabilities: PeerCapabilities
    public var publicKeyBase64: String
    public var nonceBase64: String
    public init(protocol version: DropLinkVersion = .init(),deviceID: UUID,name: String,platform: DevicePlatform,fingerprint: String,capabilities: PeerCapabilities,publicKey: String,nonce: String) {
        self.protocol = version; deviceId = deviceID; displayName = name; self.platform = platform; identityFingerprint = fingerprint; self.capabilities = capabilities; publicKeyBase64 = publicKey; nonceBase64 = nonce
    }
    public func validate() throws {
        guard `protocol`.major == 1,Int32(exactly: `protocol`.minor) != nil,Int32(exactly: capabilities.rawValue) != nil,deviceId != UUID.zero,!displayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,displayName.utf16.count <= 64,!displayName.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }),platform != .unknown,identityFingerprint.count == 64,identityFingerprint.unicodeScalars.allSatisfy({ CharacterSet(charactersIn: "0123456789abcdefABCDEF").contains($0) }),let key = DropLinkEncoding.base64(publicKeyBase64), (64...256).contains(key.count),DropLinkEncoding.base64(nonceBase64)?.count == 32 else { throw DropSpaceError.invalid("Invalid pairing hello") }
    }
}
public extension UUID {
    static let zero = UUID(uuidString: "00000000-0000-0000-0000-000000000000")!
    var dropLinkD: String { uuidString.lowercased() }
    var dropLinkN: String { dropLinkD.replacingOccurrences(of: "-",with: "") }
}
public enum DropLinkRoute {
    public static let device = "/v1/device", pairingHello = "/v1/pairing/hello", pairingConfirm = "/v1/pairing/confirm", clipboard = "/v1/clipboard", handoffText = "/v1/handoff/text", transferOffers = "/v1/transfers/offers"
    public static func transfer(_ id: UUID,action: String) throws -> String {
        guard ["status","accept","cancel","complete"].contains(action) else { throw DropSpaceError.invalid("Transfer action") }
        return "/v1/transfers/\(id.dropLinkD)/\(action)"
    }
    public static func chunk(_ session: UUID,item: UUID,index: Int) -> String { "/v1/transfers/\(session.dropLinkD)/items/\(item.dropLinkD)/chunks/\(index)" }
    private static func matches(_ path: String,_ route: String) -> Bool { var value = path; while value.hasSuffix("/") { value.removeLast() }; return value.lowercased() == route }
    public static func isPairing(_ path: String) -> Bool { matches(path,pairingHello) || matches(path,pairingConfirm) }
    public static func requiresAuthentication(_ path: String) -> Bool { matches(path,clipboard) || matches(path,handoffText) || path.lowercased().hasPrefix("/v1/transfers/") }
    public static func bodyLimit(_ path: String) -> Int { matches(path,clipboard) ? ((50 * 1024 * 1024 + 2) / 3) * 4 + 64 * 1024 : 16 * 1024 * 1024 + 64 * 1024 }
}

public enum DropLinkEncoding {
    /// Convert.FromBase64String accepts ASCII space, tab, CR and LF; retain the
    /// original text for SAS/HMAC transcripts and strip only for byte decoding.
    public static func base64(_ value: String) -> Data? {
        Data(base64Encoded: String(decoding: value.utf8.filter { ![9, 10, 13, 32].contains($0) }, as: UTF8.self))
    }
}
