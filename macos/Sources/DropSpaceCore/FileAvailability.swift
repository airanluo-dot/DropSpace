import Foundation

public enum FileAvailabilityReason: String, Codable, Error, Sendable, LocalizedError {
    case missing, accessDenied, storageUnavailable, typeChanged, volumeUnavailable
    public var errorDescription: String? {
        switch self {
        case .missing: "File no longer exists"
        case .accessDenied: "Access denied"
        case .storageUnavailable: "Storage unavailable"
        case .typeChanged: "Entry type changed"
        case .volumeUnavailable: "The original volume is unavailable. Locate the file to confirm a new reference."
        }
    }
}

public struct FileAvailability: Sendable {
    public let status: ItemStatus
    public let reason: FileAvailabilityReason?
    public let checkedAt: Date
    public let resolvedURL: URL?
    public init(status: ItemStatus, reason: FileAvailabilityReason? = nil, checkedAt: Date = Date(), resolvedURL: URL? = nil) {
        self.status = status; self.reason = reason; self.checkedAt = checkedAt; self.resolvedURL = resolvedURL
    }
}

public struct FileStatusRefresh: Sendable {
    /// False means the caller must not use its previously resolved URL.
    public let accepted: Bool
    public let current: DropItem?
    public init(accepted: Bool, current: DropItem?) { self.accepted = accepted; self.current = current }
}

extension FileReference {
    public func hasSameIdentity(as other: FileReference) -> Bool {
        path == other.path && bookmark == other.bookmark && volumeID == other.volumeID && size == other.size
    }
}
