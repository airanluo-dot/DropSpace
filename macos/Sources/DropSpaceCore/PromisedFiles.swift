import Foundation

public enum PromisedFileRules {
    // Frozen VirtualFileMaterializer limits. Provider writes are controlled by
    // AppKit on macOS; these limits are enforced before adopting the result.
    public static let maximumItems = 1_000
    public static let maximumFileBytes: Int64 = 2 * 1_024 * 1_024 * 1_024
    public static let maximumBatchBytes: Int64 = 8 * 1_024 * 1_024 * 1_024
}

public enum PromisedFileError: String, Error, LocalizedError, Sendable {
    case countLimit, fileLimit, batchLimit, invalidResult, providerUnknownCount, extraFiles, busy
    public var resourceKey: String { "Mac.Promise." + rawValue }
    public var errorDescription: String? {
        switch self {
        case .countLimit: "Promised file count exceeds 1000"
        case .fileLimit: "Promised file exceeds 2 GiB"
        case .batchLimit: "Promised batch exceeds 8 GiB"
        case .invalidResult: "The file provider returned an invalid, changed or inaccessible result"
        case .providerUnknownCount: "The file provider did not report a complete batch. No files will be imported."
        case .extraFiles: "The file provider returned more files than it announced"
        case .busy: "A promised-file batch is still receiving or retiring"
        }
    }
}

public struct OwnedImportLease: Sendable, Equatable {
    public let id: UUID
    public let stagingName: String
    public init(id: UUID = UUID()) {
        self.id = id
        stagingName = id.uuidString + ".promise-stage"
    }
}

public struct ItemImportResult: Sendable {
    public let items: [DropItem]
    public let rejected: Int
    public init(items: [DropItem], rejected: Int) { self.items = items; self.rejected = rejected }
}
