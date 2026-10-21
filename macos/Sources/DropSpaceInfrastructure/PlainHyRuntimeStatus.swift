import Foundation

/// Immutable runtime observations. A selected preference is not execution proof.
public struct PlainHyRuntimeStatus: Sendable, Equatable {
    public struct Current: Sendable, Equatable {
        public enum Phase: Sendable { case starting, executing }
        public var backend: String
        public var modelID: String
        public var phase: Phase
    }
    public struct Completed: Sendable, Equatable {
        public var backend: String
        public var modelID: String
        public var at: Date
        public var processID: Int32
    }
    public struct Failure: Sendable, Equatable {
        public var requestedBackend: String
        public var attemptedBackend: String
        public var modelID: String
        public var at: Date
        public var reason: PlainHyWorkerFailure?
        public var detail: String
        public var exit: PlainHyProcessExit?
        public var cpuFallbackAttempted: Bool
    }
    public var current: Current?
    public var last: Completed?
    public var failure: Failure?
    public init() {}
}

public struct PlainHyProcessExit: Sendable, Equatable {
    public var status: Int32
    public var wasSignal: Bool
    public var terminatedByHost: Bool
}
