import Foundation

public enum Baseline {
    public static let windowsVersion = "v0.3.2-beta.7"
    public static let commit = "1cd4620dd58a67b0963b73487743137813b8864a"
    public static let version = "v0.1.0-beta.1"
    public static let display = "v0.1.0 Beta 1 — Native macOS beta · Windows v0.3.2 Beta 7 reference"
    public static let bundleID = "io.github.airanluo-dot.DropSpace.macOS"
}

public enum ItemSource: Int, Codable, CaseIterable, Sendable { case space = 1, clipboard = 2 }
public enum ItemKind: Int, Codable, CaseIterable, Sendable {
    case unknown = 0, file, folder, text, image, url, color, code
}
public enum ItemStatus: Int, Codable, CaseIterable, Sendable {
    case available = 1, missing, unavailable, processing, error
}
public struct FileReference: Codable, Equatable, Sendable {
    public var path: String
    public var bookmark: Data?
    public var volumeID: String?
    public var size: Int64?
    public var availabilityReason: FileAvailabilityReason?
    public var lastCheckedAt: Date?
    public init(path: String, bookmark: Data? = nil, volumeID: String? = nil, size: Int64? = nil) {
        self.path = path; self.bookmark = bookmark; self.volumeID = volumeID; self.size = size
    }
}
public struct DropItem: Identifiable, Codable, Equatable, Sendable {
    public var id: UUID
    public var source: ItemSource
    public var kind: ItemKind
    public var title: String
    public var createdAt: Date
    public var lastUsedAt: Date?
    public var isPinned: Bool
    public var status: ItemStatus
    public var text: String?
    public var file: FileReference?
    public var payloadPath: String?
    public var fingerprint: String?
    public var batchID: UUID?
    public var batchIndex: Int?
    public var batchCount: Int?
    public var batchAcquisition: String?
    public var revision: Int
    public init(id: UUID = UUID(), source: ItemSource, kind: ItemKind, title: String,
                createdAt: Date = Date(), isPinned: Bool = false, status: ItemStatus = .available,
                text: String? = nil, file: FileReference? = nil, payloadPath: String? = nil,
                fingerprint: String? = nil, batchID: UUID? = nil) {
        self.id = id; self.source = source; self.kind = kind; self.title = String(title.prefix(512))
        self.createdAt = createdAt; self.isPinned = isPinned; self.status = status; self.text = text
        self.file = file; self.payloadPath = payloadPath; self.fingerprint = fingerprint
        self.batchID = batchID; revision = 1
    }
    public var searchText: String { ContentRules.searchText(title: title,body: text ?? file?.path) }
}
public struct ItemQuery: Sendable {
    public var source: ItemSource?
    public var kind: ItemKind?
    public var status: ItemStatus?
    public var pinnedOnly = false
    public var text = ""
    public var limit = 250
    public init(source: ItemSource? = nil, pinnedOnly: Bool = false, text: String = "") {
        self.source = source; self.pinnedOnly = pinnedOnly; self.text = text
    }
}
public struct UndoReceipt: Identifiable, Codable, Sendable {
    public let id: UUID
    public let expiresAt: Date
    public let count: Int
    public let kind: Kind
    public let pinValue: Bool?
    public enum Kind: String, Codable, Sendable { case remove, pin }
    public init(id: UUID = UUID(), expiresAt: Date, count: Int, kind: Kind, pinValue: Bool? = nil) {
        self.id = id; self.expiresAt = expiresAt; self.count = count; self.kind = kind; self.pinValue = pinValue
    }
}
public enum DropSpaceError: Error, LocalizedError, Sendable, Equatable {
    case invalid(String), unavailable(String), storage(String), limit(String), cancelled
    public var errorDescription: String? {
        switch self { case .invalid(let s), .unavailable(let s), .storage(let s), .limit(let s): s
        case .cancelled: "Cancelled" }
    }
}
public enum ItemPolicy {
    public static func classify(_ text: String) -> ItemKind { ContentRules.classify(text) }
    public static func expired(_ items: [DropItem], cutoff: Date, limit: Int) -> [UUID] {
        items.filter { $0.source == .clipboard && !$0.isPinned }.sorted { $0.createdAt > $1.createdAt }
            .enumerated().filter { $0.element.createdAt < cutoff || $0.offset >= limit }.map { $0.element.id }
    }
}

public enum ClipboardClearRange: String, CaseIterable, Sendable {
    case lastHour, today, all
    public func from(now: Date, calendar: Calendar = .current) -> Date? {
        switch self { case .lastHour: now.addingTimeInterval(-3600); case .today: calendar.startOfDay(for: now); case .all: nil }
    }
}
