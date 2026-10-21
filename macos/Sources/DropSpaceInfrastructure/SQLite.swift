import Foundation
import CSQLite
import DropSpaceCore

enum SQLValue { case text(String), integer(Int64), double(Double), null }
// This connection is confined to its owning repository actor. No pointer escapes.
final class SQLiteConnection: @unchecked Sendable {
    private var handle: OpaquePointer?
    init(url: URL) throws {
        guard sqlite3_open_v2(url.path, &handle, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, nil) == SQLITE_OK else {
            let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "Database open failed"
            if let handle { sqlite3_close(handle) }; handle = nil
            throw DropSpaceError.storage(message)
        }
        sqlite3_busy_timeout(handle, 3000)
        try execute("PRAGMA journal_mode=WAL")
        try execute("PRAGMA foreign_keys=ON")
        try execute("PRAGMA synchronous=FULL")
    }
    deinit { if let handle { sqlite3_close_v2(handle) } }
    func execute(_ sql: String, _ values: [SQLValue] = []) throws {
        try statement(sql, values) { statement in
            var result = sqlite3_step(statement)
            while result == SQLITE_ROW { result = sqlite3_step(statement) }
            guard result == SQLITE_DONE else { throw failure() }
        }
    }
    func rows(_ sql: String, _ values: [SQLValue] = []) throws -> [[String: String]] {
        try statement(sql, values) { statement in
            var rows: [[String: String]] = []
            while true {
                let status = sqlite3_step(statement)
                if status == SQLITE_DONE { return rows }
                guard status == SQLITE_ROW else { throw failure() }
                var row: [String: String] = [:]
                for index in 0..<sqlite3_column_count(statement) {
                    if let text = sqlite3_column_text(statement,index) {
                        row[String(cString: sqlite3_column_name(statement,index))] = String(cString: text)
                    }
                }
                rows.append(row)
            }
        }
    }
    func transaction<T>(_ body: () throws -> T) throws -> T {
        try execute("BEGIN IMMEDIATE")
        do { let value = try body(); try execute("COMMIT"); return value }
        catch { try? execute("ROLLBACK"); throw error }
    }
    private func failure() -> DropSpaceError { .storage(String(cString: sqlite3_errmsg(handle))) }
    private func statement<T>(_ sql: String, _ values: [SQLValue], _ body: (OpaquePointer) throws -> T) throws -> T {
        var pointer: OpaquePointer?
        guard sqlite3_prepare_v2(handle,sql,-1,&pointer,nil) == SQLITE_OK, let pointer else { throw failure() }
        defer { sqlite3_finalize(pointer) }
        for (offset,value) in values.enumerated() {
            let index = Int32(offset + 1); let result: Int32
            switch value {
            case .text(let text): result = sqlite3_bind_text(pointer,index,text,-1,unsafeBitCast(-1, to: sqlite3_destructor_type.self))
            case .integer(let number): result = sqlite3_bind_int64(pointer,index,number)
            case .double(let number): result = sqlite3_bind_double(pointer,index,number)
            case .null: result = sqlite3_bind_null(pointer,index)
            }
            guard result == SQLITE_OK else { throw failure() }
        }
        return try body(pointer)
    }
}
