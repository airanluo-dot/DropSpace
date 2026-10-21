import Foundation

public enum PairingState: Int, Codable, Sendable {
    case none, created, helloExchanged, awaitingLocalSasConfirmation, localConfirmed, remoteConfirmed, trusted, rejected, expired, cancelled, failed
}
public enum PairingDecision: Int, Codable, Sendable { case confirm = 1, reject, cancel }
public struct PairingOffer: Codable, Sendable {
    public let sessionId: UUID
    public let remoteHello: PairingHello
    public let localHello: PairingHello
    public let sas: Int
    public let expiresAtUtc: Date
    public var state: PairingState = .awaitingLocalSasConfirmation
    public init(sessionId: UUID, remoteHello: PairingHello, localHello: PairingHello, sas: Int, expiresAtUtc: Date) {
        self.sessionId = sessionId; self.remoteHello = remoteHello; self.localHello = localHello; self.sas = sas; self.expiresAtUtc = expiresAtUtc
    }
    private enum CodingKeys: String, CodingKey { case sessionId, remoteHello, localHello, sas, expiresAtUtc, state }
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        sessionId = try values.decodeIfPresent(UUID.self, forKey: .sessionId) ?? .zero
        remoteHello = try values.decode(PairingHello.self, forKey: .remoteHello)
        localHello = try values.decode(PairingHello.self, forKey: .localHello)
        sas = Int(try values.decodeIfPresent(Int32.self, forKey: .sas) ?? 0)
        expiresAtUtc = try values.decode(Date.self, forKey: .expiresAtUtc)
        state = try values.decodeIfPresent(PairingState.self, forKey: .state) ?? .awaitingLocalSasConfirmation
    }
}
public struct PairingConfirmationRequest: Codable, Sendable {
    public let sessionId: UUID
    public let sas: Int
    public var confirmed: Bool
    public var decision: Int
    public init(sessionId: UUID, sas: Int, confirmed: Bool, decision: Int = PairingDecision.confirm.rawValue) {
        self.sessionId = sessionId; self.sas = sas; self.confirmed = confirmed; self.decision = decision
    }
    private enum CodingKeys: String, CodingKey { case sessionId, sas, confirmed, decision }
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        sessionId = try values.decodeIfPresent(UUID.self, forKey: .sessionId) ?? .zero
        sas = Int(try values.decodeIfPresent(Int32.self, forKey: .sas) ?? 0)
        confirmed = try values.decodeIfPresent(Bool.self, forKey: .confirmed) ?? false
        decision = Int(try values.decodeIfPresent(Int32.self, forKey: .decision) ?? 1)
    }
}
public struct PairingConfirmationResponse: Codable, Sendable {
    public let trusted: Bool
    public let peerId: UUID
    public let state: PairingState
    public let errorCategory: String?
    public init(trusted: Bool, peerId: UUID, state: PairingState, errorCategory: String? = nil) {
        self.trusted = trusted; self.peerId = peerId; self.state = state; self.errorCategory = errorCategory
    }
}

/// The pairing endpoints use ASP.NET Web defaults: camel-case fields, case
/// insensitive input, Int32 numbers, and ISO8601 UTC timestamps. Canonicalization
/// is schema-scoped, retaining last duplicate members instead of changing text.
public enum DropLinkPairingJSON {
    private static let names = ["protocol", "major", "minor", "deviceId", "displayName", "platform", "capabilities", "identityFingerprint", "endpoint", "publicKeyBase64", "nonceBase64", "sessionId", "remoteHello", "localHello", "sas", "expiresAtUtc", "state", "confirmed", "decision", "trusted", "peerId", "errorCategory"]
    private static let integers: Set<String> = ["major", "minor", "platform", "capabilities", "sas", "state", "decision"]
    public static func encode<T: Encodable>(_ value: T) throws -> Data {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .custom { date, encoder in
            var value = encoder.singleValueContainer(); let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            try value.encode(formatter.string(from: date))
        }
        let result = try encoder.encode(value)
        guard result.count <= 65536 else { throw DropSpaceError.limit("DropLink pairing JSON") }; return result
    }
    public static func decode<T: Decodable>(_ type: T.Type, from bytes: Data) throws -> T {
        let map = Dictionary(uniqueKeysWithValues: names.map { ($0.lowercased(), $0) })
        func normalize(_ json: ModuleJSON) throws -> ModuleJSON {
            if case .object(let members) = json {
                var values = [ModuleJSONMember]()
                for member in members {
                    let name = map[member.name.lowercased()] ?? member.name
                    var value = try normalize(member.value)
                    if integers.contains(name) {
                        let text: String?
                        switch value { case .number(let number), .string(let number): text = number; default: text = nil }
                        if let text, let integer = Int32(text) { value = .number(String(integer)) }
                        else if text != nil { throw DropSpaceError.invalid("DropLink Int32 field") }
                    }
                    values.removeAll { $0.name == name }; values.append(.init(name, value))
                }
                return .object(values)
            }
            if case .array(let values) = json { return .array(try values.map(normalize)) }
            return json
        }
        let normalized = try normalize(ModuleJSON.decode(bytes, maximumBytes: 65536)).encoded(maximumBytes: 65536)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self), formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            if let value = formatter.date(from: text) { return value }
            formatter.formatOptions = [.withInternetDateTime]
            guard let value = formatter.date(from: text) else { throw DropSpaceError.invalid("DropLink timestamp") }; return value
        }
        return try decoder.decode(type, from: normalized)
    }
}
