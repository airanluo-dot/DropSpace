import Foundation
import Observation

/// Application-owned work. Cancellation retires presentation immediately, but
/// the task remains owned until native IO and its cleanup have actually returned.
@MainActor @Observable public final class NativeActionJobs {
    public private(set) var activeCount = 0
    @ObservationIgnored private var tasks: [UUID: Task<Void, Never>] = [:]
    @ObservationIgnored private var cancelled = Set<UUID>()
    private var closed = false
    public var acceptsNewWork: Bool { !closed }

    public init() {}

    @discardableResult
    public func start<Value: Sendable>(
        operation: @escaping @MainActor () async throws -> Value,
        completed: @escaping @MainActor (Result<Value, any Error>) -> Void
    ) -> UUID? {
        guard !closed else { return nil }
        let id = UUID()
        tasks[id] = Task {
            defer {
                tasks[id] = nil; cancelled.remove(id); activeCount = tasks.count
            }
            let result: Result<Value, any Error>
            do {
                try Task.checkCancellation()
                let value = try await operation()
                try Task.checkCancellation()
                result = .success(value)
            } catch { result = .failure(error) }
            guard !closed, !cancelled.contains(id), !Task.isCancelled else { return }
            completed(result)
        }
        activeCount = tasks.count
        return id
    }

    public func cancel(_ id: UUID) {
        guard let task = tasks[id] else { return }
        cancelled.insert(id); task.cancel()
    }

    public func shutdown() async {
        closed = true
        let pending = Array(tasks.values)
        for task in pending { task.cancel() }
        for task in pending { await task.value }
    }
}
