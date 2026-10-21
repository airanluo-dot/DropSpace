import Foundation
import DropSpaceCore

public actor ItemStore: ItemRepository {
    private let db: SQLiteConnection
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()
    public init(directory: URL) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        db = try SQLiteConnection(url: directory.appendingPathComponent("workspace.sqlite"))
        let version = Int(try db.rows("PRAGMA user_version").first?["user_version"] ?? "0") ?? 0
        guard version <= 1 else { throw DropSpaceError.storage("Database belongs to a newer version; original data was preserved.") }
        if version == 0 {
            try db.transaction {
                try db.execute("CREATE TABLE items(id TEXT PRIMARY KEY, source INTEGER NOT NULL, kind INTEGER NOT NULL, pinned INTEGER NOT NULL, created REAL NOT NULL, hidden INTEGER NOT NULL DEFAULT 0, body TEXT NOT NULL)")
                try db.execute("CREATE INDEX items_source_created ON items(source,created DESC)")
                try db.execute("CREATE INDEX items_pinned_created ON items(pinned,created DESC)")
                try db.execute("CREATE TABLE undo(slot INTEGER PRIMARY KEY CHECK(slot=1), receipt TEXT NOT NULL, originals TEXT NOT NULL)")
                try db.execute("CREATE TABLE cleanup(path TEXT PRIMARY KEY, attempts INTEGER NOT NULL DEFAULT 0, error TEXT)")
                try db.execute("PRAGMA user_version=1")
            }
        }
        // Additive local schema: a lease precedes every promised-file write.
        // Active leases are excluded from ordinary cleanup while AppKit writes.
        try db.execute("CREATE TABLE IF NOT EXISTS owned_imports(id TEXT PRIMARY KEY, paths TEXT NOT NULL)")
    }
    public func items(_ query: ItemQuery = .init()) throws -> [DropItem] {
        var whereParts = ["hidden=0"]; var args: [SQLValue] = []
        if let source = query.source { whereParts.append("source=?"); args.append(.integer(Int64(source.rawValue))) }
        if let kind = query.kind { whereParts.append("kind=?"); args.append(.integer(Int64(kind.rawValue))) }
        if query.pinnedOnly { whereParts.append("pinned=1") }
        let rows = try db.rows("SELECT body FROM items WHERE " + whereParts.joined(separator: " AND ") + " ORDER BY created DESC,id DESC", args)
        var items = try rows.map { try decode(DropItem.self,$0["body"]!) }
        if let status = query.status { items = items.filter { $0.status == status } }
        let text = ContentRules.normalizeSearch(query.text)
        if !text.isEmpty {
            func rank(_ item: DropItem) -> Int { item.searchText == text ? 0 : item.searchText.hasPrefix(text) ? 1 : 2 }
            items = items.filter { $0.searchText.contains(text) }.sorted { a,b in
                let ar = rank(a),br = rank(b)
                if ar != br { return ar < br }
                if a.createdAt != b.createdAt { return a.createdAt > b.createdAt }
                return a.id.uuidString > b.id.uuidString
            }
        }
        return Array(items.prefix(max(0,query.limit)))
    }
    public func visibleCount(source: ItemSource) throws -> Int {
        Int(try db.rows("SELECT COUNT(*) AS count FROM items WHERE hidden=0 AND source=?", [.integer(Int64(source.rawValue))]).first?["count"] ?? "0") ?? 0
    }
    public func item(_ id: UUID) throws -> DropItem? {
        try db.rows("SELECT body FROM items WHERE id=? AND hidden=0", [.text(id.uuidString)]).first
            .map { try decode(DropItem.self, $0["body"]!) }
    }
    public func refreshFileStatus(expected: DropItem, availability: FileAvailability) throws -> FileStatusRefresh {
        try Task.checkCancellation()
        return try db.transaction {
            guard var current = try item(expected.id) else { return .init(accepted: false, current: nil) }
            guard let reference = expected.file, let currentReference = current.file,
                  reference.hasSameIdentity(as: currentReference), expected.kind == current.kind,
                  expected.payloadPath == current.payloadPath else { return .init(accepted: false, current: current) }
            let changed = expected.status != availability.status || reference.availabilityReason != availability.reason
            if changed {
                guard current.revision == expected.revision else { return .init(accepted: false, current: current) }
                current.status = availability.status; current.file?.availabilityReason = availability.reason
                current.file?.lastCheckedAt = availability.checkedAt; current.revision += 1
                try put(current, insert: false)
            }
            // Reconfirm storage even when metadata was unchanged. Another window
            // may have replaced/hidden this reference while the filesystem blocked.
            return .init(accepted: current.status == availability.status && current.file?.availabilityReason == availability.reason, current: current)
        }
    }
    public func markUsed(_ ids: [UUID], now: Date = Date()) throws {
        try Task.checkCancellation()
        try db.transaction {
            for id in Set(ids) {
                guard var current = try item(id) else { continue }
                current.lastUsedAt = now; current.revision += 1
                try put(current, insert: false)
            }
        }
    }
    /// Batch commands deliberately bypass the current filtered/paged projection.
    public func batch(_ id: UUID) throws -> [DropItem] {
        try db.rows("SELECT body FROM items WHERE hidden=0 AND json_extract(body,'$.batchID')=? ORDER BY created DESC,id DESC", [.text(id.uuidString)])
            .map { try decode(DropItem.self, $0["body"]!) }
            .sorted { ($0.batchIndex ?? Int.max) < ($1.batchIndex ?? Int.max) }
    }
    public func toggleBatchPin(_ id: UUID, now: Date = Date()) throws -> UndoReceipt? {
        let members = try batch(id)
        return try setPinned(members.map(\.id), pinned: members.contains { !$0.isPinned }, now: now)
    }
    public func removeBatch(_ id: UUID, now: Date = Date()) throws -> UndoReceipt? {
        try remove(batch(id).map(\.id), now: now)
    }
    public func insert(_ items: [DropItem]) throws {
        try Task.checkCancellation()
        try db.transaction { for item in items { try put(item, insert: true) } }
    }
    /// MainViewModel.AddPathsBatchAsync commits ordinary references separately.
    /// A file-specific write failure must not discard the rest of the selection.
    public func insertFileCandidates(_ candidates: [DropItem]) throws -> ItemImportResult {
        var accepted: [DropItem] = [], rejected = 0
        for candidate in candidates {
            try Task.checkCancellation()
            guard candidate.source == .space, candidate.file != nil, candidate.payloadPath == nil else {
                throw DropSpaceError.invalid("Reference intake requires ordinary Space files")
            }
            var item = candidate; item.batchIndex = accepted.count
            do {
                try db.transaction { try put(item, insert: true) }
                accepted.append(item)
            } catch let error as DropSpaceError {
                switch error {
                case .storage, .invalid: rejected += 1
                default: throw error
                }
            }
        }
        return .init(items: accepted, rejected: rejected)
    }
    public func replace(_ item: DropItem) throws {
        try db.transaction {
            guard let body = try db.rows("SELECT body FROM items WHERE id=? AND hidden=0",[.text(item.id.uuidString)]).first?["body"] else { throw DropSpaceError.unavailable("The record was removed") }
            let old = try decode(DropItem.self,body)
            guard item.revision == old.revision + 1 else { throw DropSpaceError.unavailable("The record changed. Refresh before trying again.") }
            if let owned = old.payloadPath, owned != item.payloadPath {
                try db.execute("INSERT OR IGNORE INTO cleanup(path) VALUES(?)",[.text(owned)])
            }
            try put(item,insert: false)
        }
    }
    private func put(_ item: DropItem, insert: Bool) throws {
        let body = try encode(item)
        if insert {
            try db.execute("INSERT INTO items(id,source,kind,pinned,created,body) VALUES(?,?,?,?,?,?)",[.text(item.id.uuidString),.integer(Int64(item.source.rawValue)),.integer(Int64(item.kind.rawValue)),.integer(item.isPinned ? 1 : 0),.double(item.createdAt.timeIntervalSince1970),.text(body)])
        } else {
            try db.execute("UPDATE items SET source=?,kind=?,pinned=?,created=?,body=? WHERE id=?",[.integer(Int64(item.source.rawValue)),.integer(Int64(item.kind.rawValue)),.integer(item.isPinned ? 1 : 0),.double(item.createdAt.timeIntervalSince1970),.text(body),.text(item.id.uuidString)])
        }
    }
    public func remove(_ ids: [UUID], now: Date = Date()) throws -> UndoReceipt? {
        try mutate(ids, now: now, kind: .remove, pinned: nil)
    }
    public func setPinned(_ ids: [UUID], pinned: Bool, now: Date = Date()) throws -> UndoReceipt? {
        try mutate(ids, now: now, kind: .pin, pinned: pinned)
    }
    private func mutate(_ ids: [UUID], now: Date, kind: UndoReceipt.Kind, pinned: Bool?) throws -> UndoReceipt? {
        guard !ids.isEmpty else { return try pendingUndo() }
        return try db.transaction {
            try finalizeCore()
            var originals: [DropItem] = []
            for id in Set(ids) {
                guard let body = try db.rows("SELECT body FROM items WHERE id=? AND hidden=0",[.text(id.uuidString)]).first?["body"] else { continue }
                let original = try decode(DropItem.self,body)
                if let pinned, original.isPinned == pinned { continue }
                originals.append(original)
                if let pinned { var item = original; item.isPinned = pinned; item.revision += 1; try put(item,insert: false) }
                else { try db.execute("UPDATE items SET hidden=1 WHERE id=?",[.text(id.uuidString)]) }
            }
            guard !originals.isEmpty else { return nil }
            let receipt = UndoReceipt(expiresAt: now.addingTimeInterval(8),count: originals.count,kind: kind,pinValue: pinned)
            try db.execute("INSERT INTO undo(slot,receipt,originals) VALUES(1,?,?)",[.text(try encode(receipt)),.text(try encode(originals))])
            return receipt
        }
    }
    public func undo(_ token: UUID, now: Date = Date()) throws {
        try db.transaction {
            guard let row = try db.rows("SELECT receipt,originals FROM undo WHERE slot=1").first else { throw DropSpaceError.unavailable("Undo expired") }
            let receipt = try decode(UndoReceipt.self,row["receipt"]!)
            guard receipt.id == token, now < receipt.expiresAt else { throw DropSpaceError.unavailable("Undo expired") }
            let originals = try decode([DropItem].self,row["originals"]!)
            for original in originals {
                if receipt.kind == .pin {
                    guard let body = try db.rows("SELECT body FROM items WHERE id=? AND hidden=0",[.text(original.id.uuidString)]).first?["body"] else { continue }
                    var current = try decode(DropItem.self,body)
                    current.isPinned = original.isPinned; current.revision += 1
                    try put(current,insert: false)
                } else {
                    try put(original,insert: false)
                    try db.execute("UPDATE items SET hidden=0 WHERE id=?",[.text(original.id.uuidString)])
                }
            }
            try db.execute("DELETE FROM undo")
        }
    }
    public func pendingUndo() throws -> UndoReceipt? {
        guard let body = try db.rows("SELECT receipt FROM undo WHERE slot=1").first?["receipt"] else { return nil }
        return try decode(UndoReceipt.self,body)
    }
    public func finalizeUndo(now: Date = Date()) throws {
        if let receipt = try pendingUndo(), receipt.expiresAt <= now { try db.transaction { try finalizeCore() } }
    }
    public func finalizeOnShutdown() throws { try db.transaction { try finalizeCore() } }
    public func recoverAfterLaunch(now: Date = Date()) throws {
        for row in try db.rows("SELECT id FROM owned_imports") {
            if let id = row["id"].flatMap(UUID.init(uuidString:)) { try abandonOwnedImport(id) }
        }
        guard let receipt = try pendingUndo() else { return }
        if receipt.kind == .remove && now < receipt.expiresAt { try undo(receipt.id,now: now) }
        else { try db.transaction { try finalizeCore() } }
    }
    private func finalizeCore() throws {
        for row in try db.rows("SELECT body FROM items WHERE hidden=1") {
            if let path = try decode(DropItem.self,row["body"]!).payloadPath {
                // Only app-owned payload paths are ever queued. References are not deleted.
                try db.execute("INSERT OR IGNORE INTO cleanup(path) VALUES(?)",[.text(path)])
            }
        }
        try db.execute("DELETE FROM items WHERE hidden=1")
        try db.execute("DELETE FROM undo")
    }
    public func applyRetention(days: Int, count: Int, now: Date = Date()) throws {
        var query = ItemQuery(source: .clipboard); query.limit = Int.max
        let pending = try db.rows("SELECT originals FROM undo WHERE slot=1").first?["originals"]
        let protected = Set(try pending.map { try decode([DropItem].self,$0).map(\.id) } ?? [])
        let ids = ItemPolicy.expired(try items(query),cutoff: now.addingTimeInterval(-Double(days) * 86400),limit: count).filter { !protected.contains($0) }
        try db.transaction {
            for id in ids {
                if let body = try db.rows("SELECT body FROM items WHERE id=? AND hidden=0",[.text(id.uuidString)]).first?["body"], let path = try decode(DropItem.self,body).payloadPath {
                    try db.execute("INSERT OR IGNORE INTO cleanup(path) VALUES(?)",[.text(path)])
                }
                try db.execute("DELETE FROM items WHERE id=? AND hidden=0",[.text(id.uuidString)])
            }
        }
    }
    public func pendingCleanup() throws -> [String] { try db.rows("SELECT path FROM cleanup ORDER BY attempts LIMIT 100").compactMap { $0["path"] } }
    public func finishCleanup(_ path: String, error: String? = nil) throws {
        if let error { try db.execute("UPDATE cleanup SET attempts=attempts+1,error=? WHERE path=?",[.text(error),.text(path)]) }
        else { try db.execute("DELETE FROM cleanup WHERE path=?",[.text(path)]) }
    }
    public func payloadIsReferenced(_ path: String) throws -> Bool {
        try db.rows("SELECT body FROM items").contains { try decode(DropItem.self,$0["body"]!).payloadPath == path }
    }
    public func beginOwnedImport(_ lease: OwnedImportLease) throws {
        try Task.checkCancellation()
        try db.execute("INSERT INTO owned_imports(id,paths) VALUES(?,?)",
                       [.text(lease.id.uuidString), .text(try encode([lease.stagingName]))])
    }
    public func reserveOwnedImport(_ id: UUID, name: String) throws {
        try Task.checkCancellation()
        guard !name.isEmpty, name != ".", name != "..",
              !name.contains("/"), !name.contains("\\"), !name.contains("\0") else {
            throw DropSpaceError.invalid("Unsafe owned import name")
        }
        var paths = try ownedImportPaths(id)
        guard !paths.contains(name) else { throw DropSpaceError.invalid("Duplicate owned import reservation") }
        paths.append(name)
        try db.execute("UPDATE owned_imports SET paths=? WHERE id=?", [.text(try encode(paths)), .text(id.uuidString)])
    }
    public func commitOwnedImportItem(_ id: UUID, item: DropItem) throws {
        try Task.checkCancellation()
        try db.transaction {
            let reserved = try ownedImportPaths(id)
            guard let adopted = item.payloadPath, reserved.dropFirst().contains(adopted),
                  item.batchID == id else { throw DropSpaceError.invalid("Owned import commit does not match its reservation") }
            try put(item, insert: true)
            // One row and its custody transfer commit together. A later file
            // failure/cancellation must not roll back this successful record.
            try db.execute("UPDATE owned_imports SET paths=? WHERE id=?",
                           [.text(try encode(reserved.filter { $0 != adopted })), .text(id.uuidString)])
        }
    }
    public func abandonOwnedImport(_ id: UUID) throws {
        try db.transaction {
            for path in try ownedImportPaths(id) {
                if try !payloadIsReferenced(path) { try db.execute("INSERT OR IGNORE INTO cleanup(path) VALUES(?)", [.text(path)]) }
            }
            try db.execute("DELETE FROM owned_imports WHERE id=?", [.text(id.uuidString)])
        }
    }
    public func discardUncreatedOwnedImport(_ id: UUID) throws {
        guard try ownedImportPaths(id).count == 1 else { throw DropSpaceError.invalid("An import with payload reservations cannot be discarded") }
        try db.execute("DELETE FROM owned_imports WHERE id=?", [.text(id.uuidString)])
    }
    private func ownedImportPaths(_ id: UUID) throws -> [String] {
        guard let value = try db.rows("SELECT paths FROM owned_imports WHERE id=?", [.text(id.uuidString)]).first?["paths"] else {
            throw DropSpaceError.unavailable("Owned import reservation is no longer active")
        }
        return try decode([String].self, value)
    }
    private func encode<T: Encodable>(_ value: T) throws -> String { String(decoding: try encoder.encode(value),as: UTF8.self) }
    private func decode<T: Decodable>(_ type: T.Type, _ text: String) throws -> T { try decoder.decode(type,from: Data(text.utf8)) }
}
