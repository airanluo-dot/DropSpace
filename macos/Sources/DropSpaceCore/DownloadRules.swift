import Foundation

public struct HTTPByteRange: Codable,Equatable,Sendable {
    public let from: Int64; public let to: Int64
    public var length: Int64 { to - from + 1 }
    public static func plan(length: Int64,connections: Int,minimumPartSize: Int64 = 2 * 1024 * 1024) throws -> [HTTPByteRange] {
        guard length > 0,minimumPartSize > 0 else { throw DropSpaceError.invalid("Invalid byte range length") }
        let count = min(Int64(min(256,max(1,connections))),max(1,length / minimumPartSize))
        let quotient = length / count,remainder = length % count
        var offset: Int64 = 0
        return (0..<count).map { index in let size = quotient + (index < remainder ? 1 : 0); defer { offset += size }; return HTTPByteRange(from:offset,to:offset+size-1) }
    }
}
public struct HTTPResumeMetadata: Codable,Sendable {
    public var identity: String
    public var etag: String?
    public var length: Int64?
    public init(identity: String,etag: String?,length: Int64?) { self.identity = identity; self.etag = etag; self.length = length }
    public func accepts(offset: Int64,etag responseTag: String?,contentRange: String?,contentLength: Int64?) -> Bool {
        guard let responseTag,!responseTag.hasPrefix("W/"),responseTag == etag,let contentRange,contentRange.hasPrefix("bytes ") else { return false }
        let pieces = contentRange.dropFirst(6).split(separator:"/")
        guard pieces.count == 2,let total = Int64(pieces[1]),total > 0 else { return false }
        let bounds = pieces[0].split(separator:"-")
        guard bounds.count == 2,let start = Int64(bounds[0]),let end = Int64(bounds[1]),start == offset,end == total-1,(length == nil || length == total),(contentLength == nil || contentLength == total-offset) else { return false }
        return true
    }
}
public enum DownloadPhase: String,Codable,Sendable { case queued,connecting,downloading,verifying,paused,completed,failed,cancelled }
public struct DownloadRecord: Identifiable,Codable,Sendable {
    public let id: UUID
    public var source: URL
    public var destination: URL
    public var phase: DownloadPhase = .queued
    public var received: Int64 = 0
    public var total: Int64?
    public var expectedSHA256: String?
    public var error: String?
    public var cleanupPending: Bool?
    public var historyRemovalPending: Bool?
    public var cleanupError: String?
    public var stage: DownloadStage?
    public var bytesPerSecond: Double?
    public var activeConnections: Int?
    public var created = Date()
    public init(id: UUID = UUID(),source: URL,destination: URL,expectedSHA256: String? = nil) { self.id = id; self.source = source; self.destination = destination; self.expectedSHA256 = expectedSHA256 }
}

public enum DownloadStage: String, Codable, Sendable {
    case checking, queued, waitingConnection, connecting, probing, transferring, retryWaiting, merging, verifying, publishing, completed
}
public struct DownloadMetrics: Sendable {
    public var sequence: UInt64 = 0
    public var stage: DownloadStage = .checking
    public var received: Int64 = 0
    public var total: Int64?
    public var bytesPerSecond: Double = 0
    public var activeConnections: Int = 0
    public var attempt: Int = 1
    public var lastByteAt: Date?
    public init() {}
}

public enum DownloadHistory {
    public static func visible(_ records: [DownloadRecord], limit: Int) -> [DownloadRecord] {
        var terminal = 0
        return records.sorted { $0.created > $1.created }.filter {
            if [.completed, .cancelled].contains($0.phase) { terminal += 1; return terminal <= max(0, limit) }
            return true
        }
    }
}
