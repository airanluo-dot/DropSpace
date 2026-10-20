import Foundation
import DropSpaceCore

public struct InternetShareRecord: Codable, Sendable, Identifiable {
    public enum State: String, Codable, Sendable { case reserved, creating, uploading, completed, revokePending, revoked }
    public let id: UUID, endpoint: URL, itemCount: Int, totalBytes: Int64, displayName: String
    public var expiresAt: Date
    public var state: State = .reserved
    public var stagingPending = true
    public var error: String?
    public var cleanupError: String?
    var account: String { "internet-share.revoke." + id.dropLinkN }
}
/// SQLite records custody and capacity; the authenticated session exists only
/// in Keychain. A reserved row precedes remote creation and any local staging.
/// Missing/denied credentials never silently erase a live recovery obligation.
public actor InternetShareStore {
    private let db: SQLiteConnection
    private let keys: any DropLinkSecretStorage
    private let gate = DownloadPermitPool(limit: 1)
    public init(directory: URL, keys: any DropLinkSecretStorage = KeychainStore()) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        db = try SQLiteConnection(url: directory.appendingPathComponent("internet-shares.sqlite")); self.keys = keys
        try db.execute("CREATE TABLE IF NOT EXISTS shares(id TEXT PRIMARY KEY, record TEXT NOT NULL)")
        try db.execute("CREATE TABLE IF NOT EXISTS share_key_cleanup(id TEXT PRIMARY KEY)")
    }
    public func records() throws -> [InternetShareRecord] { try db.rows("SELECT record FROM shares ORDER BY id").map { try decode($0["record"]!) } }
    public func record(_ id: UUID) throws -> InternetShareRecord? { try db.rows("SELECT record FROM shares WHERE id=?", [.text(id.dropLinkD)]).first.map { try decode($0["record"]!) } }
    public func reserve(endpoint: URL, expires: Date, count: Int, bytes: Int64, name: String, now: Date = Date()) async throws -> InternetShareRecord {
        try await mutation { owner in
            guard ShareBackendSession.safe(endpoint), expires > now, expires <= now.addingTimeInterval(7 * 86400), (1...100).contains(count), (1...2_147_483_648).contains(bytes) else { throw DropSpaceError.invalid("Internet share reservation") }
            try await owner.prune(now: now)
            // All unresolved custody consumes capacity, even if the URL expired
            // but a local staging cleanup still needs permission or IO recovery.
            guard try owner.records().count < 128 else { throw DropSpaceError.limit("Secure share revoke-handle capacity is full") }
            let record = InternetShareRecord(id: UUID(), endpoint: endpoint, itemCount: count, totalBytes: bytes, displayName: name, expiresAt: expires)
            try owner.save(record); return record
        }
    }
    func beginCreation(_ id: UUID, lifetime: TimeInterval) async throws -> InternetShareRecord {
        try await mutation { owner in
            guard var record = try owner.record(id), record.state == .reserved, lifetime.isFinite, lifetime > 0, lifetime <= 7 * 86400 else { throw DropSpaceError.invalid("Share creation reservation") }
            record.expiresAt = Date().addingTimeInterval(lifetime); record.state = .creating
            try owner.save(record); return record
        }
    }
    public func attach(_ session: ShareBackendSession, id: UUID) async throws {
        try await mutation { owner in
            guard var record = try owner.record(id), [.creating, .uploading, .revokePending].contains(record.state) else { throw DropSpaceError.invalid("Share reservation is no longer active") }
            try session.validate(origin: record.endpoint)
            var data = try JSONEncoder().encode(session); defer { data.resetBytes(in: data.startIndex..<data.endIndex) }
            guard data.count <= 32768 else { throw DropSpaceError.limit("Secure share revoke handle") }
            try await owner.keys.save(data, account: record.account)
            // If this SQLite write fails, the prior reservation still names the
            // exact Keychain account, allowing restart to recover and revoke.
            if record.state != .revokePending { record.state = .uploading }; record.error = nil
            try owner.save(record)
        }
    }
    public func session(_ id: UUID) async throws -> ShareBackendSession? {
        guard let record = try record(id), record.state != .revoked else { return nil }
        guard var bytes = try await keys.read(record.account) else { return nil }
        defer { bytes.resetBytes(in: bytes.startIndex..<bytes.endIndex) }
        guard bytes.count <= 32768 else { throw DropSpaceError.invalid("Secure share revoke record size") }
        let session = try JSONDecoder().decode(ShareBackendSession.self, from: bytes); try session.validate(origin: record.endpoint)
        guard let current = try self.record(id), current.state != .revoked else { return nil }; return session
    }
    public func transition(_ id: UUID, to state: InternetShareRecord.State, error: String? = nil) async throws {
        try await mutation { owner in
            guard var record = try owner.record(id) else { throw DropSpaceError.invalid("Missing share custody record") }
            let allowed: Bool
            switch (record.state, state) {
            case (.reserved, .creating), (.uploading, .completed), (.reserved, .revokePending), (.creating, .revokePending), (.uploading, .revokePending), (.completed, .revokePending), (.revokePending, .revokePending): allowed = true
            default: allowed = false
            }
            guard allowed else { throw DropSpaceError.invalid("Invalid secure share state transition") }
            record.state = state; record.error = error; try owner.save(record)
        }
    }
    public func stagingCleaned(_ id: UUID, error: String? = nil) async throws {
        try await mutation { owner in
            guard var record = try owner.record(id) else { return }
            record.stagingPending = error != nil; record.cleanupError = error; try owner.save(record)
            try await owner.prune(now: Date())
        }
    }
    public func didRevoke(_ id: UUID) async throws {
        try await mutation { owner in
            guard var record = try owner.record(id) else { return }
            record.state = .revoked; record.error = nil
            try owner.db.transaction {
                try owner.save(record)
                try owner.db.execute("INSERT OR IGNORE INTO share_key_cleanup(id) VALUES(?)", [.text(id.dropLinkD)])
            }
            try await owner.cleanKeys()
            try await owner.prune(now: Date())
        }
    }
    public func recoverExpired(now: Date = Date()) async throws { try await mutation { try await $0.prune(now: now) } }
    private func prune(now: Date) async throws {
        for var record in try records() where record.expiresAt <= now && record.state != .reserved {
            record.state = .revoked
            try db.transaction {
                try save(record)
                try db.execute("INSERT OR IGNORE INTO share_key_cleanup(id) VALUES(?)", [.text(record.id.dropLinkD)])
            }
        }
        try await cleanKeys()
        for record in try records() where record.state == .revoked && !record.stagingPending {
            guard try db.rows("SELECT id FROM share_key_cleanup WHERE id=?", [.text(record.id.dropLinkD)]).isEmpty else { continue }
            try db.execute("DELETE FROM shares WHERE id=?", [.text(record.id.dropLinkD)])
        }
    }
    private func cleanKeys() async throws {
        for row in try db.rows("SELECT id FROM share_key_cleanup") {
            guard let id = UUID(uuidString: row["id"]!) else { throw DropSpaceError.invalid("Share cleanup identity") }
            try await keys.remove("internet-share.revoke." + id.dropLinkN)
            try db.execute("DELETE FROM share_key_cleanup WHERE id=?", [.text(id.dropLinkD)])
        }
    }
    private func save(_ record: InternetShareRecord) throws {
        let json = String(decoding: try JSONEncoder().encode(record), as: UTF8.self)
        try db.execute("INSERT INTO shares(id,record) VALUES(?,?) ON CONFLICT(id) DO UPDATE SET record=excluded.record", [.text(record.id.dropLinkD), .text(json)])
    }
    private func decode(_ text: String) throws -> InternetShareRecord {
        let value = try JSONDecoder().decode(InternetShareRecord.self, from: Data(text.utf8))
        guard value.id != .zero, ShareBackendSession.safe(value.endpoint), (1...100).contains(value.itemCount), (1...2_147_483_648).contains(value.totalBytes) else { throw DropSpaceError.invalid("Secure share custody record") }; return value
    }
    private func mutation<T: Sendable>(_ operation: @Sendable (isolated InternetShareStore) async throws -> T) async throws -> T {
        let permit = try await gate.acquire()
        do { let result = try await operation(self); await gate.release(permit); return result }
        catch { await gate.release(permit); throw error }
    }
}
