import Foundation
import Testing
@testable import DropSpacePlatform

@MainActor @Test func actionShutdownWaitsForNativeRetirementAndSuppressesLateResults() async throws {
    let jobs = NativeActionJobs(), gate = ActionRetirementGate()
    var results: [Int] = []
    let started = jobs.start(operation: { await gate.wait() }, completed: {
        if case .success(let value) = $0 { results.append(value) }
    })
    let id = try #require(started)
    await gate.awaitEntry()
    jobs.cancel(id)
    #expect(jobs.activeCount == 1)
    var drained = false
    let shutdown = Task { await jobs.shutdown(); drained = true }
    let deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while jobs.acceptsNewWork, ContinuousClock.now < deadline { await Task.yield() }
    #expect(!drained)
    let rejected = jobs.start(operation: { 2 }, completed: { _ in })
    #expect(rejected == nil)
    await gate.release()
    await shutdown.value
    #expect(drained && jobs.activeCount == 0 && results.isEmpty)
}

private actor ActionRetirementGate {
    private var entered = false
    private var entryWaiters: [CheckedContinuation<Void, Never>] = []
    private var continuation: CheckedContinuation<Int, Never>?
    func wait() async -> Int {
        entered = true
        for waiter in entryWaiters { waiter.resume() }
        entryWaiters.removeAll()
        return await withCheckedContinuation { continuation = $0 }
    }
    func awaitEntry() async {
        if entered { return }
        await withCheckedContinuation { entryWaiters.append($0) }
    }
    func release() {
        continuation?.resume(returning: 1); continuation = nil
    }
}
