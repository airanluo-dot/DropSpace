import Foundation

public enum TransferSessionState: Int, Codable, Sendable {
    case offered = 1, awaitingApproval, accepted, transferring, verifying, completed, rejected, cancelled, failed
    public var isTerminal: Bool { [.completed, .rejected, .cancelled, .failed].contains(self) }
    public var retention: TimeInterval { self == .completed ? 300 : 120 }
}
public struct TransferLimits: Sendable {
    public var chunkBytes = 4 * 1024 * 1024
    public var maxItems = 100
    public var maxTotalBytes: Int64 = 8 * 1024 * 1024 * 1024
    public var maxRelativePathLength = 1024
    public var maxConcurrentChunks = 2
    public init() {}
    public func validate() throws {
        guard (65536...16_777_216).contains(chunkBytes), (1...10000).contains(maxItems),
              (1...1_099_511_627_776).contains(maxTotalBytes), (64...4096).contains(maxRelativePathLength),
              (1...8).contains(maxConcurrentChunks) else { throw DropSpaceError.invalid("Transfer limits") }
    }
}
public struct TransferItemManifest: Codable, Sendable {
    public var id: UUID
    public var kind: Int32
    public var displayName: String
    public var relativePath: String
    public var size: Int64
    public var sha256: String
    public var mimeType: String?
    public var chunkCount: Int32?
    public init(id: UUID = UUID(), kind: Int32 = 1, name: String, path: String, size: Int64, hash: String, mime: String?, chunks: Int32?) {
        self.id = id; self.kind = kind; displayName = name; relativePath = path; self.size = size; sha256 = hash; mimeType = mime; chunkCount = chunks
    }
    private enum CodingKeys: String, CodingKey { case id, kind, displayName, relativePath, size, sha256, mimeType, chunkCount }
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decode(UUID.self, forKey: .id); kind = try values.decode(Int32.self, forKey: .kind)
        displayName = try values.decode(String.self, forKey: .displayName); relativePath = try values.decode(String.self, forKey: .relativePath)
        size = values.contains(.size) ? try values.decode(Int64.self, forKey: .size) : 0
        sha256 = try values.decode(String.self, forKey: .sha256); mimeType = try values.decodeIfPresent(String.self, forKey: .mimeType)
        chunkCount = try values.decodeIfPresent(Int32.self, forKey: .chunkCount)
    }
}
public struct TransferManifest: Codable, Sendable {
    public var sessionId: UUID
    public var `protocol`: DropLinkVersion
    public var items: [TransferItemManifest]
    public var totalBytes: Int64
    public var createdAtUtc: String
    public var chunkBytes: Int32
    public init(session: UUID, items: [TransferItemManifest], total: Int64, now: Date, chunkBytes: Int32) {
        sessionId = session; `protocol` = .init(); self.items = items; totalBytes = total
        createdAtUtc = DropLinkHandoffJSON.timestamp(now); self.chunkBytes = chunkBytes
    }
    private enum CodingKeys: String, CodingKey { case sessionId, `protocol`, items, totalBytes, createdAtUtc, chunkBytes }
    private enum VersionKeys: String, CodingKey { case major, minor }
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        sessionId = try values.decode(UUID.self, forKey: .sessionId)
        let version = try values.nestedContainer(keyedBy: VersionKeys.self, forKey: .protocol)
        `protocol` = .init(major: Int(try version.decode(Int32.self, forKey: .major)), minor: version.contains(.minor) ? Int(try version.decode(Int32.self, forKey: .minor)) : 0)
        items = try values.decode([TransferItemManifest].self, forKey: .items)
        totalBytes = values.contains(.totalBytes) ? try values.decode(Int64.self, forKey: .totalBytes) : 0
        createdAtUtc = values.contains(.createdAtUtc) ? try values.decode(String.self, forKey: .createdAtUtc) : "0001-01-01T00:00:00+00:00"
        chunkBytes = values.contains(.chunkBytes) ? try values.decode(Int32.self, forKey: .chunkBytes) : 4_194_304
    }
}
public struct TransferOfferRequest: Codable, Sendable {
    public let peerId: UUID; public let manifest: TransferManifest
    public init(peer: UUID, manifest: TransferManifest) { peerId = peer; self.manifest = manifest }
}
public struct TransferOfferResponse: Codable, Sendable {
    public let sessionId: UUID; public let state: TransferSessionState; public let errorCategory: String?
    public init(session: UUID, state: TransferSessionState, error: String? = nil) { sessionId = session; self.state = state; errorCategory = error }
}
public struct TransferAcceptRequest: Codable, Sendable {
    public let accepted: Bool
    public init(_ accepted: Bool) { self.accepted = accepted }
    private enum CodingKeys: String, CodingKey { case accepted }
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        accepted = values.contains(.accepted) ? try values.decode(Bool.self, forKey: .accepted) : false
    }
}
public struct TransferStatusResponse: Codable, Sendable {
    public let sessionId: UUID; public let state: TransferSessionState; public let transferredBytes: Int64
    // String keys produce .NET's JSON object; Swift's [UUID: Value] encodes an array.
    public let receivedChunks: [String: [Int32]]; public let completedRelativePaths: [String]; public let errorCategory: String?
    public init(session: UUID, state: TransferSessionState, bytes: Int64, chunks: [String: [Int32]], completed: [String], error: String? = nil) {
        sessionId = session; self.state = state; transferredBytes = bytes; receivedChunks = chunks; completedRelativePaths = completed; errorCategory = error
    }
}
public struct TransferCompleteResponse: Codable, Sendable {
    public let sessionId: UUID; public let state: TransferSessionState; public let completedRelativePaths: [String]; public let errorCategory: String?
    public init(session: UUID, state: TransferSessionState, completed: [String], error: String? = nil) { sessionId = session; self.state = state; completedRelativePaths = completed; errorCategory = error }
}
public enum DropLinkTransferJSON {
    public static func encode<T: Encodable>(_ value: T) throws -> Data { try DropLinkHandoffJSON.encode(value) }
    public static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        func chunkMaps(_ value: ModuleJSON) throws -> ModuleJSON {
            switch value {
            case .array(let values): return .array(try values.map(chunkMaps))
            case .object(let members):
                return .object(try members.map { member in
                    guard member.name.lowercased() == "receivedchunks", case .object(let chunks) = member.value else { return .init(member.name, try chunkMaps(member.value)) }
                    var result: [ModuleJSONMember] = []
                    for chunk in chunks {
                        guard let id = UUID(uuidString: chunk.name), case .array(let values) = chunk.value else { throw DropSpaceError.invalid("Transfer received chunk map") }
                        let normalized = try values.map { value -> ModuleJSON in
                            if case .string(let text) = value {
                                guard let number = Int32(text) else { throw DropSpaceError.invalid("Transfer chunk index") }; return .number(String(number))
                            }
                            return value
                        }
                        result.removeAll { $0.name == id.dropLinkD }; result.append(.init(id.dropLinkD, .array(normalized)))
                    }
                    return .init(member.name, .object(result))
                })
            default: return value
            }
        }
        let normalized = try chunkMaps(ModuleJSON.decode(data, maximumBytes: DropLinkHandoffJSON.maximumBytes)).encoded(maximumBytes: DropLinkHandoffJSON.maximumBytes)
        return try DropLinkHandoffJSON.decode(type, from: normalized,
            additionalNames: ["manifest", "protocol", "major", "minor", "items", "id", "displayName", "relativePath", "size", "mimeType", "chunkCount", "totalBytes", "chunkBytes", "state", "transferredBytes", "receivedChunks", "completedRelativePaths"],
            integerNames: ["major", "minor", "size", "chunkCount", "totalBytes", "chunkBytes", "state", "transferredBytes"])
    }
}
