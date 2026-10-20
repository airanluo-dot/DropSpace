import Foundation
import Testing
import DropSpacePlatform

@MainActor private final class RestartGate {
    var continuation: CheckedContinuation<Void, Never>?
    var starts = 0
    func wait() async {
        starts += 1
        await withCheckedContinuation { continuation = $0 }
    }
    func release() { continuation?.resume(); continuation = nil }
}

@MainActor private func until(_ condition: () -> Bool) async throws {
    let deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while !condition() {
        guard ContinuousClock.now < deadline else { throw CancellationError() }
        try await Task.sleep(for: .milliseconds(2))
    }
}

@Test @MainActor func musicRestartTimeoutRetainsNativeOwnershipAndRejectsLateSuccess() async throws {
    let owner = NativeMusicRestart(timeout: .milliseconds(30)), gate = RestartGate()
    #expect(owner.begin { await gate.wait() })
    try await until { gate.starts == 1 }
    #expect(!owner.begin { await gate.wait() })
    try await until { owner.state == .failed }
    #expect(owner.isRetiring)
    #expect(!owner.begin { await gate.wait() })
    // Simulate a native call that returns successfully after the deadline,
    // without observing Swift task cancellation.
    gate.release()
    try await until { !owner.isRetiring }
    #expect(owner.state == .failed && gate.starts == 1)
    #expect(owner.begin {})
    try await until { !owner.isRetiring }
    #expect(owner.state == .succeeded && owner.failure == nil)
    await owner.shutdown()
}

@Test @MainActor func musicRestartFailureAndQuitDrainWithoutStartingAnotherRuntime() async throws {
    enum ExpectedFailure: Error { case unavailable }
    let owner = NativeMusicRestart(), gate = RestartGate()
    #expect(owner.begin { throw ExpectedFailure.unavailable })
    try await until { !owner.isRetiring }
    #expect(owner.state == .failed && owner.failure != nil)
    #expect(owner.begin { await gate.wait() })
    try await until { gate.starts == 1 }
    var stopped = false
    let shutdown = Task { await owner.shutdown(); stopped = true }
    await Task.yield()
    #expect(!stopped && owner.isRetiring)
    #expect(!owner.begin {})
    gate.release(); await shutdown.value
    #expect(stopped && !owner.isRetiring && owner.state != .succeeded)
    #expect(!owner.begin {})
}
