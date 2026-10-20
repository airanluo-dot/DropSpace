import Foundation
import Observation

/// App-owned attempt: leaving Music does not cancel the runtime's recovery.
/// A timed-out operation retains the slot until its native work actually exits.
@MainActor @Observable public final class NativeMusicRestart {
    public enum State { case idle, reconnecting, succeeded, failed }
    public private(set) var state: State = .idle
    public private(set) var failure: String?
    public private(set) var isRetiring = false
    @ObservationIgnored private var operation: Task<Void, Never>?
    @ObservationIgnored private var deadline: Task<Void, Never>?
    @ObservationIgnored private var closed = false
    private let timeout: Duration

    public init(timeout: Duration = .seconds(20)) { self.timeout = timeout }

    @discardableResult
    public func begin(_ restart: @escaping @MainActor () async throws -> Void) -> Bool {
        guard !closed, operation == nil else { return false }
        state = .reconnecting; failure = nil; isRetiring = true
        operation = Task { [weak self] in
            let result: Result<Void, any Error>
            do { try Task.checkCancellation(); try await restart(); try Task.checkCancellation(); result = .success(()) }
            catch { result = .failure(error) }
            guard let self else { return }
            deadline?.cancel(); deadline = nil
            // Timeout/quit owns the final visible status, even if native work
            // returns success after ignoring cancellation.
            if !closed, state == .reconnecting {
                switch result {
                case .success: state = .succeeded
                case .failure(let error): state = .failed; failure = error.localizedDescription
                }
            }
            operation = nil; isRetiring = false
        }
        deadline = Task { [weak self, timeout] in
            do { try await Task.sleep(for: timeout) } catch { return }
            guard let self, state == .reconnecting else { return }
            state = .failed; failure = "Music reconnection timed out; native work is retiring."
            operation?.cancel()
        }
        return true
    }

    public func shutdown() async {
        closed = true; deadline?.cancel(); deadline = nil
        operation?.cancel(); await operation?.value
        operation = nil; isRetiring = false
    }
}
