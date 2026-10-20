import Foundation
import DropSpaceCore

public struct DropLinkTransferRecord: Codable, Sendable, Identifiable {
    public var id: UUID { manifest.sessionId }
    public var peer: UUID
    public var manifest: TransferManifest
    public var state: TransferSessionState
    public var createdAt: Date
    public var lastActivity: Date
    public var completedAt: Date?
    public var transferredBytes: Int64
    public var chunks: [String: [Int32]]
    public var completedPaths: [String]
    public var errorCategory: String?
    public var pendingCleanup: Bool
    public var cleanupError: String?
    public var destination: URL
    init(peer: UUID, manifest: TransferManifest, destination: URL, now: Date) {
        self.peer = peer; self.manifest = manifest; self.destination = destination
        state = .awaitingApproval; createdAt = now; lastActivity = now
        transferredBytes = 0; chunks = [:]; completedPaths = []; pendingCleanup = true
    }
    var snapshot: TransferStatusResponse { .init(session: id, state: state, bytes: transferredBytes, chunks: chunks, completed: completedPaths, error: errorCategory) }
    var completion: TransferCompleteResponse { .init(session: id, state: state, completed: completedPaths, error: errorCategory) }
}
public actor DropLinkTransferRepository {
    private let db: SQLiteConnection
    public init(directory: URL) throws {
        _ = try TransferFileSystem.directory(directory, create: true)
        db = try SQLiteConnection(url: directory.appendingPathComponent("transfers.sqlite"))
        try db.execute("CREATE TABLE IF NOT EXISTS transfer_history(id TEXT PRIMARY KEY, record TEXT NOT NULL)")
        try db.execute("CREATE TABLE IF NOT EXISTS transfer_chunks(session TEXT NOT NULL REFERENCES transfer_history(id), item TEXT NOT NULL, chunk INTEGER NOT NULL, length INTEGER NOT NULL, PRIMARY KEY(session,item,chunk))")
    }
    public func history() throws -> [DropLinkTransferRecord] {
        try db.rows("SELECT record FROM transfer_history").map { row in
            var record = try JSONDecoder().decode(DropLinkTransferRecord.self, from: Data(row["record"]!.utf8))
            for chunk in try db.rows("SELECT item,chunk FROM transfer_chunks WHERE session=? ORDER BY item,chunk", [.text(record.id.dropLinkD)]) {
                if let item = chunk["item"], let index = chunk["chunk"].flatMap(Int32.init) { record.chunks[item, default: []].append(index) }
            }
            return record
        }.sorted { $0.createdAt > $1.createdAt }
    }
    func create(_ record: DropLinkTransferRecord) throws {
        try db.execute("INSERT INTO transfer_history(id,record) VALUES(?,?)", [.text(record.id.dropLinkD), .text(try encode(record))])
    }
    func save(_ record: DropLinkTransferRecord) throws {
        try db.execute("UPDATE transfer_history SET record=? WHERE id=?", [.text(try encode(record)), .text(record.id.dropLinkD)])
    }
    func commitChunk(_ record: DropLinkTransferRecord, item: UUID, index: Int32, length: Int) throws {
        try db.transaction {
            try db.execute("INSERT INTO transfer_chunks(session,item,chunk,length) VALUES(?,?,?,?)", [.text(record.id.dropLinkD), .text(item.dropLinkD), .integer(Int64(index)), .integer(Int64(length))])
            try save(record)
        }
    }
    func contains(_ id: UUID) throws -> Bool { !(try db.rows("SELECT id FROM transfer_history WHERE id=?", [.text(id.dropLinkD)])).isEmpty }
    private func encode(_ record: DropLinkTransferRecord) throws -> String {
        var metadata = record; metadata.chunks = [:]
        return String(decoding: try JSONEncoder().encode(metadata), as: UTF8.self)
    }
}
