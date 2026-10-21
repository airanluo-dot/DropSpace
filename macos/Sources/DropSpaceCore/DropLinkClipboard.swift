import Foundation

public enum ClipboardPayloadKind: Int, Sendable { case text = 1, url, image }
public struct ClipboardEnvelope: Codable, Sendable {
    public var eventId: UUID
    public var originDeviceId: UUID
    public var originSequence: Int64
    public var kind: Int
    public var createdAtUtc: String
    public var sha256: String
    public var byteLength: Int64
    public var mime: String
    public var text: String?
    public var imageBytes: Data?
    public var isTextLike: Bool { kind == 1 || kind == 2 }
    public init(event: UUID = UUID(), origin: UUID, sequence: Int64, kind: ClipboardPayloadKind, now: Date = Date(), hash: String, bytes: Int64, mime: String, text: String? = nil, image: Data? = nil) {
        eventId = event; originDeviceId = origin; originSequence = sequence; self.kind = kind.rawValue
        createdAtUtc = DropLinkHandoffJSON.timestamp(now); sha256 = hash; byteLength = bytes; self.mime = mime; self.text = text; imageBytes = image
    }
    private enum CodingKeys: String, CodingKey { case eventId, originDeviceId, originSequence, kind, createdAtUtc, sha256, byteLength, mime, text, imageBytes }
    public init(from decoder: any Decoder) throws {
        let v = try decoder.container(keyedBy: CodingKeys.self)
        eventId = try v.decodeIfPresent(UUID.self, forKey: .eventId) ?? .zero
        originDeviceId = try v.decodeIfPresent(UUID.self, forKey: .originDeviceId) ?? .zero
        originSequence = try v.decodeIfPresent(Int64.self, forKey: .originSequence) ?? 0
        kind = Int(try v.decodeIfPresent(Int32.self, forKey: .kind) ?? 0)
        createdAtUtc = try v.decodeIfPresent(String.self, forKey: .createdAtUtc) ?? "0001-01-01T00:00:00+00:00"
        sha256 = try v.decodeIfPresent(String.self, forKey: .sha256) ?? ""
        byteLength = try v.decodeIfPresent(Int64.self, forKey: .byteLength) ?? 0
        mime = try v.decodeIfPresent(String.self, forKey: .mime) ?? ""
        text = try v.decodeIfPresent(String.self, forKey: .text)
        if let base64 = try v.decodeIfPresent(String.self, forKey: .imageBytes) {
            guard let bytes = DropLinkEncoding.base64(base64) else { throw DropSpaceError.invalid("Clipboard image Base64") }; imageBytes = bytes
        } else { imageBytes = nil }
    }
}
public struct ClipboardSyncRequest: Codable, Sendable {
    public let peerId: UUID
    public let envelope: ClipboardEnvelope?
    public init(peer: UUID, envelope: ClipboardEnvelope) { peerId = peer; self.envelope = envelope }
}
public struct ClipboardSyncResponse: Codable, Sendable {
    public let accepted: Bool
    public let errorCategory: String?
    public init(accepted: Bool, errorCategory: String? = nil) { self.accepted = accepted; self.errorCategory = errorCategory }
}
public enum DropLinkClipboardJSON {
    public static let maximumBytes = ((50 * 1024 * 1024 + 2) / 3) * 4 + 65536
    public static func encode<T: Encodable>(_ value: T) throws -> Data { try DropLinkHandoffJSON.encode(value, maximum: maximumBytes) }
    public static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        try DropLinkHandoffJSON.decode(type, from: data, maximum: maximumBytes, additionalNames: ["eventId", "originDeviceId", "originSequence", "envelope", "mime", "text", "imageBytes"])
    }
}
public enum ClipboardReceiveError: Error { case unavailable, paused }
