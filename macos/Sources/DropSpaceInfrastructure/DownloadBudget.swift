import Foundation
import DropSpaceCore

public actor DownloadPermitPool {
    private var limit: Int
    private var active = Set<UUID>()
    private var waiting: [(UUID,CheckedContinuation<UUID,any Error>)] = []
    public init(limit: Int) { self.limit = max(1,limit) }
    public var activeCount: Int { active.count }
    public var configuredLimit: Int { limit }
    public func setLimit(_ value: Int) throws { guard (1...256).contains(value) else { throw DropSpaceError.invalid("Connection budget") }; limit = value; admit() }
    public func acquire(deadline: ContinuousClock.Instant? = nil) async throws -> UUID {
        try Task.checkCancellation(); let id = UUID()
        if let deadline, ContinuousClock.now >= deadline { throw URLError(.timedOut) }
        if active.count < limit { active.insert(id); return id }
        let expiry = deadline.map { deadline in Task {
            do { try await Task.sleep(until: deadline, clock: .continuous); expire(id) } catch {}
        } }
        defer { expiry?.cancel() }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in waiting.append((id,continuation)) }
        } onCancel: { Task { await self.cancel(id) } }
    }
    public func release(_ id: UUID) { active.remove(id); admit() }
    private func cancel(_ id: UUID) { if let index = waiting.firstIndex(where: { $0.0 == id }) { waiting.remove(at:index).1.resume(throwing:CancellationError()) } }
    private func expire(_ id: UUID) { if let index = waiting.firstIndex(where: { $0.0 == id }) { waiting.remove(at: index).1.resume(throwing: URLError(.timedOut)) } }
    private func admit() { while active.count < limit && !waiting.isEmpty { let (id,continuation) = waiting.removeFirst(); active.insert(id); continuation.resume(returning:id) } }
}
public actor DownloadBandwidth {
    private var rate: Int64 = 0
    private var tokens: Double = 1
    private var updated = ContinuousClock.now
    public init() {}
    public func setLimit(_ value: Int64) throws {
        guard (0...1_073_741_824).contains(value) else { throw DropSpaceError.invalid("Bandwidth limit") }
        rate = value; tokens = max(1,Double(value)/10); updated = .now
    }
    public func consume(_ count: Int) async throws {
        guard count >= 0 else { throw DropSpaceError.invalid("Negative bandwidth request") }
        var remaining = count
        while remaining > 0 {
            try Task.checkCancellation(); if rate == 0 { return }
            let now = ContinuousClock.now, elapsed = updated.duration(to:now)
            let seconds = Double(elapsed.components.seconds) + Double(elapsed.components.attoseconds) / 1e18
            let capacity = max(1,Double(rate)/10)
            tokens = min(capacity,tokens + seconds * Double(rate)); updated = now
            let granted = min(remaining,Int(tokens.rounded(.down))); tokens -= Double(granted); remaining -= granted
            if remaining == 0 { return }
            let delay = min(0.1,max(0.001,(min(Double(remaining),capacity)-tokens)/Double(rate)))
            try await Task.sleep(for:.seconds(delay))
        }
    }
}
public final class DownloadBudget: Sendable {
    public let transfers = DownloadPermitPool(limit:2)
    public let connections = DownloadPermitPool(limit:64)
    public let bandwidth = DownloadBandwidth()
    public init() {}
    public func configure(_ settings: AppSettings) async throws {
        let value = try settings.validated()
        try await transfers.setLimit(value.maxConcurrentDownloads)
        try await connections.setLimit(value.maxDownloadConnections)
        try await bandwidth.setLimit(value.downloadSpeedLimitBytesPerSecond)
    }
}
