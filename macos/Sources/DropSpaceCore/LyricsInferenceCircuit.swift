/// Session-local Beta 7 failure protection. Cache hits, cancellation, resource
/// admission and outputs with no useful translation do not change this state.
public struct LyricsInferenceCircuit: Sendable {
    private var failures = 0
    private var generation: UInt64 = 0
    public init() {}
    public var isPaused: Bool { failures >= 3 }
    public func begin() -> UInt64? { isPaused ? nil : generation }
    public mutating func complete(_ token: UInt64, success: Bool) {
        guard token == generation, !isPaused else { return }
        failures = success ? 0 : failures + 1
    }
    public mutating func resume() { generation &+= 1; failures = 0 }
}
