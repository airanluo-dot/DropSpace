import Foundation

public protocol ItemRepository: Sendable {
    func items(_ query: ItemQuery) async throws -> [DropItem]
    func insert(_ items: [DropItem]) async throws
    func replace(_ item: DropItem) async throws
    func item(_ id: UUID) async throws -> DropItem?
    func refreshFileStatus(expected: DropItem, availability: FileAvailability) async throws -> FileStatusRefresh
    func markUsed(_ ids: [UUID], now: Date) async throws
    func remove(_ ids: [UUID], now: Date) async throws -> UndoReceipt?
    func setPinned(_ ids: [UUID], pinned: Bool, now: Date) async throws -> UndoReceipt?
    func undo(_ token: UUID, now: Date) async throws
    func finalizeUndo(now: Date) async throws
}
public protocol SettingsPersistence: Sendable {
    func load() async throws -> AppSettings
    func save(_ settings: AppSettings) async throws
}
public struct MediaSnapshot: Equatable, Sendable {
    public var sourceID = ""
    public var sourceName = ""
    public var title = ""
    public var artist = ""
    public var album = ""
    public var albumArtist = ""
    public var sourceTrackID = ""
    public var duration: Double = 0
    public var position: Double = 0
    public var playing = false
    public var canSeek = false
    public var canPlayPause = false
    public var canSkip = false
    public var artwork: Data?
    public var observedAt = Date()
    public var observedUptime = ProcessInfo.processInfo.systemUptime
    /// Decorative text motion, isolated from corrected native seek/lyric timing.
    public var marqueePosition: Double?
    public var timelineRevision: UInt64 = 0
    public init() {}
    public var trackID: String { [sourceID, sourceTrackID, title, artist, album].joined(separator: "\u{1f}") }
    public func interpolatedPosition(uptime: Double = ProcessInfo.processInfo.systemUptime) -> Double {
        guard position.isFinite else { return 0 }
        // A stalled public scripting source cannot establish unlimited progress.
        // Keep the existing two-second interpolation bound and use a monotonic
        // clock so wall-clock changes cannot seek a visible lyric.
        let elapsed = playing && uptime.isFinite && observedUptime.isFinite ? max(0, min(2, uptime - observedUptime)) : 0
        return min(duration.isFinite && duration > 0 ? duration : 86400, max(0, position + elapsed))
    }
    public func interpolatedMarqueePosition(uptime: Double = ProcessInfo.processInfo.systemUptime) -> Double {
        guard let marqueePosition, marqueePosition.isFinite else { return interpolatedPosition(uptime: uptime) }
        let elapsed = playing && uptime.isFinite && observedUptime.isFinite ? max(0, min(2, uptime - observedUptime)) : 0
        return marqueePosition + elapsed
    }
}
public enum MediaCommand: Sendable { case playPause, previous, next, seek(Double) }
@MainActor public protocol MediaSessionService: AnyObject {
    var snapshot: MediaSnapshot { get }
    func command(_ command: MediaCommand) async throws
}
public struct CapabilityState: Codable, Sendable {
    public enum Status: String, Codable, Sendable { case available, permissionRequired, unsupported, failed }
    public let status: Status
    public let reason: String
    public init(_ status: Status, _ reason: String = "") { self.status = status; self.reason = reason }
}
