import Foundation
import CryptoKit
import DropSpaceCore

/// SQLite owns trust transitions; Keychain owns secret bytes. A versioned secret
/// account and durable pairing intent keep a working re-pair secret intact until
/// the replacement's metadata commits. No Windows database is read or migrated.
public actor DropLinkPeerStore {
    private let db: SQLiteConnection
    private let keychain: any DropLinkSecretStorage
    private let gate = DownloadPermitPool(limit: 1)
    public private(set) var recoveryError: String?
    public init(directory: URL, keychain: any DropLinkSecretStorage = KeychainStore()) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        db = try SQLiteConnection(url: directory.appendingPathComponent("peers.sqlite")); self.keychain = keychain
        try db.execute("CREATE TABLE IF NOT EXISTS peers(id TEXT PRIMARY KEY, record TEXT NOT NULL, secret TEXT)")
        try db.execute("CREATE TABLE IF NOT EXISTS pairing_intents(id TEXT PRIMARY KEY, record TEXT NOT NULL, secret TEXT NOT NULL, previous_secret TEXT)")
        try db.execute("CREATE TABLE IF NOT EXISTS peer_key_cleanup(peer TEXT NOT NULL, account TEXT PRIMARY KEY)")
    }
    public func peers() throws -> [DropLinkPeer] { try db.rows("SELECT record FROM peers ORDER BY id").map { try Self.decode($0["record"]!) } }
    public func pendingCleanupCount() throws -> Int { try db.rows("SELECT account FROM peer_key_cleanup").count }
    public func secretForTrustedPeer(_ id: UUID) async throws -> SymmetricKey? {
        guard let row = try row(id), try Self.decode(row["record"]!).trustState == .trusted, let account = row["secret"] else { return nil }
        try Self.validate(account, peer: id)
        let bytes = try await keychain.read(account)
        // Revocation or a re-pair may commit while the Keychain call is suspended.
        guard let current = try self.row(id), current["secret"] == account, try Self.decode(current["record"]!).trustState == .trusted else { return nil }
        guard let bytes, bytes.count == 32 else { return nil }
        return SymmetricKey(data: bytes)
    }
    public func trust(_ hello: PairingHello, secret: SymmetricKey) async throws {
        try await action { owner in
            try hello.validate(); guard secret.bitCount == 256 else { throw DropSpaceError.invalid("DropLink peer secret size") }
            let old = try owner.row(hello.deviceId), oldPeer = try old.map { try Self.decode($0["record"]!) }
            guard oldPeer?.trustState != .blocked, oldPeer?.trustState != .unpairPending else { throw DropSpaceError.unavailable("Peer is blocked or being removed") }
            var peer = DropLinkPeer(hello: hello, state: .pairingPending)
            peer.createdAtUtc = oldPeer?.createdAtUtc ?? peer.createdAtUtc
            let account = "droplink.peer." + hello.deviceId.dropLinkN + "." + UUID().dropLinkN
            let record = try Self.encode(peer)
            try owner.db.transaction {
                // Retain an already trusted record until the whole replacement commits.
                if oldPeer?.trustState != .trusted {
                    try owner.db.execute("INSERT INTO peers(id, record, secret) VALUES(?,?,NULL) ON CONFLICT(id) DO UPDATE SET record=excluded.record", [.text(peer.id.dropLinkD), .text(record)])
                }
                if let prior = try owner.db.rows("SELECT secret FROM pairing_intents WHERE id=?", [.text(peer.id.dropLinkD)]).first?["secret"] {
                    try owner.enqueueCleanup(peer.id, account: prior)
                }
                try owner.db.execute("INSERT INTO pairing_intents(id,record,secret,previous_secret) VALUES(?,?,?,?) ON CONFLICT(id) DO UPDATE SET record=excluded.record,secret=excluded.secret,previous_secret=excluded.previous_secret", [.text(peer.id.dropLinkD), .text(record), .text(account), old?["secret"].map(SQLValue.text) ?? .null])
            }
            do {
                try Task.checkCancellation()
                try await owner.keychain.save(secret.withUnsafeBytes { Data($0) }, account: account)
                try Task.checkCancellation()
            } catch is CancellationError {
                // Withdraw the durable promotion intent before asynchronous cleanup.
                // A later restart must never promote a cancelled replacement key.
                try owner.abortIntent(peer.id)
                await owner.cleanKeys()
                throw CancellationError()
            }
            try owner.completeIntent(peer.id)
            await owner.cleanKeys()
        }
    }
    public func remove(_ id: UUID, blocked: Bool = false) async throws {
        try await action { owner in
            guard let row = try owner.row(id) else { return }
            var peer = try Self.decode(row["record"]!); peer.trustState = blocked ? .blocked : .unpairPending
            try owner.db.transaction {
                try owner.db.execute("UPDATE peers SET record=? WHERE id=?", [.text(try Self.encode(peer)), .text(id.dropLinkD)])
                if let secret = row["secret"] { try owner.enqueueCleanup(id, account: secret) }
                if let intent = try owner.db.rows("SELECT secret FROM pairing_intents WHERE id=?", [.text(id.dropLinkD)]).first?["secret"] { try owner.enqueueCleanup(id, account: intent) }
                try owner.db.execute("DELETE FROM pairing_intents WHERE id=?", [.text(id.dropLinkD)])
            }
            await owner.cleanKeys()
            try owner.finishRemovals()
        }
    }
    public func recover() async throws {
        try await action { owner in
            owner.recoveryError = nil
            for intent in try owner.db.rows("SELECT id,secret FROM pairing_intents") {
                guard let id = UUID(uuidString: intent["id"]!), let account = intent["secret"] else { continue }
                do {
                    try Self.validate(account, peer: id)
                    if let bytes = try await owner.keychain.read(account), bytes.count == 32 { try owner.completeIntent(id) }
                    else {
                        try owner.db.transaction {
                            try owner.enqueueCleanup(id, account: account)
                            try owner.db.execute("DELETE FROM pairing_intents WHERE id=?", [.text(id.dropLinkD)])
                            if let row = try owner.row(id), try Self.decode(row["record"]!).trustState == .pairingPending { try owner.db.execute("DELETE FROM peers WHERE id=?", [.text(id.dropLinkD)]) }
                        }
                    }
                } catch { owner.recoveryError = String(describing: type(of: error)) }
            }
            for row in try owner.db.rows("SELECT id,record,secret FROM peers") {
                var peer = try Self.decode(row["record"]!)
                guard [.trusted, .pairingPending].contains(peer.trustState) else { continue }
                // An unresolved intent owns its own recovery. In particular, a
                // temporary Keychain access failure must not erase that obligation.
                if peer.trustState == .pairingPending,
                   !(try owner.db.rows("SELECT id FROM pairing_intents WHERE id=?", [.text(peer.id.dropLinkD)])).isEmpty { continue }
                do {
                    let account = row["secret"]
                    if let account { try Self.validate(account, peer: peer.id) }
                    let bytes: Data?
                    if let account { bytes = try await owner.keychain.read(account) } else { bytes = nil }
                    if peer.trustState == .pairingPending {
                        if bytes?.count == 32 {
                            peer.trustState = .trusted; peer.lastSeenAtUtc = Date()
                            try owner.db.execute("UPDATE peers SET record=? WHERE id=?", [.text(try Self.encode(peer)), .text(peer.id.dropLinkD)])
                        } else {
                            peer.trustState = .unpairPending
                            try owner.db.transaction {
                                try owner.db.execute("UPDATE peers SET record=? WHERE id=?", [.text(try Self.encode(peer)), .text(peer.id.dropLinkD)])
                                if let account { try owner.enqueueCleanup(peer.id, account: account) }
                            }
                        }
                    } else if bytes?.count != 32 {
                        peer.trustState = .pairingPending
                        try owner.db.execute("UPDATE peers SET record=? WHERE id=?", [.text(try Self.encode(peer)), .text(peer.id.dropLinkD)])
                    }
                } catch { owner.recoveryError = String(describing: type(of: error)) }
            }
            await owner.cleanKeys(); try owner.finishRemovals()
        }
    }
    private func completeIntent(_ id: UUID) throws {
        guard let intent = try db.rows("SELECT record,secret,previous_secret FROM pairing_intents WHERE id=?", [.text(id.dropLinkD)]).first else { return }
        var peer = try Self.decode(intent["record"]!)
        if let row = try row(id), [.blocked, .unpairPending].contains(try Self.decode(row["record"]!).trustState) { throw DropSpaceError.unavailable("Peer trust was withdrawn") }
        peer.trustState = .trusted; peer.lastSeenAtUtc = Date()
        try db.transaction {
            try db.execute("INSERT INTO peers(id,record,secret) VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET record=excluded.record,secret=excluded.secret", [.text(id.dropLinkD), .text(try Self.encode(peer)), .text(intent["secret"]!)])
            if let previous = intent["previous_secret"], previous != intent["secret"] { try enqueueCleanup(id, account: previous) }
            try db.execute("DELETE FROM pairing_intents WHERE id=?", [.text(id.dropLinkD)])
        }
    }
    private func abortIntent(_ id: UUID) throws {
        try db.transaction {
            if let intent = try db.rows("SELECT secret FROM pairing_intents WHERE id=?", [.text(id.dropLinkD)]).first {
                try enqueueCleanup(id, account: intent["secret"]!)
                try db.execute("DELETE FROM pairing_intents WHERE id=?", [.text(id.dropLinkD)])
            }
            if let row = try row(id), try Self.decode(row["record"]!).trustState == .pairingPending {
                if let secret = row["secret"] { try enqueueCleanup(id, account: secret) }
                try db.execute("DELETE FROM peers WHERE id=?", [.text(id.dropLinkD)])
            }
        }
    }
    private func cleanKeys() async {
        do {
            for row in try db.rows("SELECT peer,account FROM peer_key_cleanup") {
                guard let id = UUID(uuidString: row["peer"]!), let account = row["account"] else { continue }
                do {
                    try Self.validate(account, peer: id)
                    let references = try db.rows("SELECT record FROM peers WHERE secret=?", [.text(account)])
                    guard !references.contains(where: { (try? Self.decode($0["record"]!).trustState) == .trusted }) else { continue }
                    guard try db.rows("SELECT id FROM pairing_intents WHERE secret=?", [.text(account)]).isEmpty else { continue }
                    try await keychain.remove(account)
                    try db.transaction {
                        try db.execute("UPDATE peers SET secret=NULL WHERE secret=?", [.text(account)])
                        try db.execute("DELETE FROM peer_key_cleanup WHERE account=?", [.text(account)])
                    }
                } catch { recoveryError = String(describing: type(of: error)) }
            }
        } catch { recoveryError = String(describing: type(of: error)) }
    }
    private func finishRemovals() throws {
        for row in try db.rows("SELECT id,record FROM peers") {
            let peer = try Self.decode(row["record"]!)
            if peer.trustState == .unpairPending, try db.rows("SELECT account FROM peer_key_cleanup WHERE peer=?", [.text(peer.id.dropLinkD)]).isEmpty {
                try db.execute("DELETE FROM peers WHERE id=?", [.text(peer.id.dropLinkD)])
            }
        }
    }
    private func enqueueCleanup(_ id: UUID, account: String) throws {
        try Self.validate(account, peer: id)
        try db.execute("INSERT OR IGNORE INTO peer_key_cleanup(peer,account) VALUES(?,?)", [.text(id.dropLinkD), .text(account)])
    }
    private func row(_ id: UUID) throws -> [String: String]? { try db.rows("SELECT record,secret FROM peers WHERE id=?", [.text(id.dropLinkD)]).first }
    private static func validate(_ account: String, peer: UUID) throws {
        let prefix = "droplink.peer." + peer.dropLinkN + ".", suffix = account.dropFirst(prefix.count)
        guard account.hasPrefix(prefix), suffix.count == 32, suffix.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else { throw DropSpaceError.invalid("DropLink secret ownership") }
    }
    private static func encode(_ peer: DropLinkPeer) throws -> String { String(decoding: try JSONEncoder().encode(peer), as: UTF8.self) }
    private static func decode(_ text: String) throws -> DropLinkPeer { try JSONDecoder().decode(DropLinkPeer.self, from: Data(text.utf8)) }
    private func action<T: Sendable>(_ operation: @Sendable (isolated DropLinkPeerStore) async throws -> T) async throws -> T {
        let lease = try await gate.acquire()
        do { try Task.checkCancellation(); let result = try await operation(self); await gate.release(lease); return result }
        catch { await gate.release(lease); throw error }
    }
}
