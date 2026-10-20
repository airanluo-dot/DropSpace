import Foundation
import DropSpaceCore

/// One observation belongs to one transfer, never to the shared budget. Retained
/// bytes are excluded from measured throughput, including after range fallback.
actor DownloadObservation {
    private var value = DownloadMetrics()
    private var origin = ContinuousClock.now
    private var retained: Int64 = 0
    private var reportTime = ContinuousClock.now
    private let sink: @Sendable (DownloadMetrics) async -> Void
    init(sink: @escaping @Sendable (DownloadMetrics) async -> Void) { self.sink = sink }
    func begin(bytes: Int64, total: Int64?) async {
        origin = .now; retained = bytes; value.received = bytes; value.total = total
        value.bytesPerSecond = 0; value.lastByteAt = nil; await emit()
    }
    func stage(_ stage: DownloadStage, attempt: Int? = nil) async {
        value.stage = stage; if let attempt { value.attempt = attempt }; await emit()
    }
    func connected(_ delta: Int) async {
        value.activeConnections += delta
        assert(value.activeConnections >= 0)
        await emit()
    }
    func bytes(_ bytes: Int64, total: Int64?) async {
        if bytes > value.received { value.lastByteAt = Date() }
        value.received = bytes; value.total = total; value.stage = .transferring
        let elapsed = origin.duration(to: .now).components
        let seconds = Double(elapsed.seconds) + Double(elapsed.attoseconds) / 1e18
        value.bytesPerSecond = Double(max(0, bytes - retained)) / max(0.001, seconds)
        if reportTime.duration(to: .now) >= .milliseconds(100) { await emit() }
    }
    func finish(completed: Bool) async {
        if completed { value.stage = .completed }
        value.activeConnections = 0; value.bytesPerSecond = 0; await emit()
    }
    private func emit() async {
        reportTime = .now; value.sequence += 1; await sink(value)
    }
}
