import Foundation

public enum HandoffMessageKind: Int, Sendable { case text = 1, url }
public struct HandoffMessage: Codable, Sendable {
    public var sessionId: UUID
    public var senderDeviceId: UUID
    public var senderDisplayName: String
    public var kind: Int
    public var byteLength: Int64
    public var sha256: String
    public var utf8Payload: String
    public var displayLabel: String?
    /// Retain the wire offset: Date alone would silently accept non-UTC input.
    public var createdAtUtc: String
    public init(session: UUID = UUID(), sender: UUID, name: String, kind: HandoffMessageKind, bytes: Int64, hash: String, payload: String, label: String?, now: Date) {
        sessionId = session; senderDeviceId = sender; senderDisplayName = name; self.kind = kind.rawValue
        byteLength = bytes; sha256 = hash; utf8Payload = payload; displayLabel = label
        createdAtUtc = DropLinkHandoffJSON.timestamp(now)
    }
    private enum CodingKeys: String, CodingKey { case sessionId, senderDeviceId, senderDisplayName, kind, byteLength, sha256, utf8Payload, displayLabel, createdAtUtc }
    public init(from decoder: any Decoder) throws {
        let v = try decoder.container(keyedBy: CodingKeys.self)
        sessionId = try v.decodeIfPresent(UUID.self, forKey: .sessionId) ?? .zero
        senderDeviceId = try v.decodeIfPresent(UUID.self, forKey: .senderDeviceId) ?? .zero
        senderDisplayName = try v.decodeIfPresent(String.self, forKey: .senderDisplayName) ?? ""
        kind = Int(try v.decodeIfPresent(Int32.self, forKey: .kind) ?? 0)
        byteLength = try v.decodeIfPresent(Int64.self, forKey: .byteLength) ?? 0
        sha256 = try v.decodeIfPresent(String.self, forKey: .sha256) ?? ""
        utf8Payload = try v.decodeIfPresent(String.self, forKey: .utf8Payload) ?? ""
        displayLabel = try v.decodeIfPresent(String.self, forKey: .displayLabel)
        createdAtUtc = try v.decodeIfPresent(String.self, forKey: .createdAtUtc) ?? "0001-01-01T00:00:00+00:00"
    }
}
public struct HandoffMessageRequest: Codable, Sendable {
    public let peerId: UUID
    public let message: HandoffMessage?
    public init(peerId: UUID, message: HandoffMessage?) { self.peerId = peerId; self.message = message }
}
public struct HandoffMessageResponse: Codable, Sendable {
    public let sessionId: UUID
    public let accepted: Bool
    public let errorCategory: String?
    public init(sessionId: UUID, accepted: Bool, errorCategory: String? = nil) { self.sessionId = sessionId; self.accepted = accepted; self.errorCategory = errorCategory }
}

public enum DropLinkHandoffJSON {
    public static let maximumBytes = 16 * 1024 * 1024 + 65536
    public static func timestamp(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
    public static func utcDate(_ text: String) -> Date? {
        guard text.hasSuffix("Z") || text.hasSuffix("+00:00") || text.hasSuffix("-00:00") else { return nil }
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: text) { return date }
        formatter.formatOptions = [.withInternetDateTime]; return formatter.date(from: text)
    }
    public static func encode<T: Encodable>(_ value: T, maximum: Int = maximumBytes) throws -> Data {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        let data = try encoder.encode(value)
        guard data.count <= maximum else { throw DropSpaceError.limit("DropLink message JSON") }; return data
    }
    public static func decode<T: Decodable>(_ type: T.Type, from data: Data, maximum: Int = maximumBytes, additionalNames: [String] = [], integerNames: Set<String> = []) throws -> T {
        let names = ["peerId", "message", "sessionId", "senderDeviceId", "senderDisplayName", "kind", "byteLength", "sha256", "utf8Payload", "displayLabel", "createdAtUtc", "accepted", "errorCategory"] + additionalNames
        let map = Dictionary(uniqueKeysWithValues: names.map { ($0.lowercased(), $0) })
        func normalize(_ value: ModuleJSON) throws -> ModuleJSON {
            if case .array(let values) = value { return .array(try values.map(normalize)) }
            guard case .object(let members) = value else { return value }
            var result: [ModuleJSONMember] = []
            for member in members {
                let name = map[member.name.lowercased()] ?? member.name
                var value = try normalize(member.value)
                if name == "kind" || name == "byteLength" || name == "originSequence" || integerNames.contains(name) {
                    if case .string(let text) = value {
                        guard let number = Int64(text), name != "kind" || Int32(exactly: number) != nil else { throw DropSpaceError.invalid("DropLink integer") }
                        value = .number(String(number))
                    }
                }
                result.removeAll { $0.name == name }; result.append(.init(name, value))
            }
            return .object(result)
        }
        let normalized = try normalize(ModuleJSON.decode(data, maximumBytes: maximum)).encoded(maximumBytes: maximum)
        return try JSONDecoder().decode(type, from: normalized)
    }
}
