import Foundation
import DropSpaceCore

/// A timed-out codec retains its one input/worker slot until the native call exits.
/// A logical timeout must never allow another large decode to accumulate behind it.
public actor ClipboardImageReadLifetime {
    private var owned: Task<Void, Never>?
    public init() {}
    public var outstanding: Bool { owned != nil }
    public func run(timeout: Duration = .seconds(5), operation: @escaping @Sendable () throws -> ClipboardImageInfo) async throws -> ClipboardImageInfo {
        try Task.checkCancellation()
        guard owned == nil else { throw DropSpaceError.unavailable("Remote clipboard image preparation is still in progress") }
        let result = ImageReadResult()
        let worker = Task.detached(priority: .utility) {
            let value: Result<ClipboardImageInfo, any Error>
            do { try Task.checkCancellation(); value = .success(try operation()) }
            catch { value = .failure(error) }
            await self.finished(value, result: result)
        }
        owned = worker
        let timer = Task { do { try await Task.sleep(for: timeout); await result.complete(.failure(DropSpaceError.unavailable("Remote clipboard image preparation timed out"))); worker.cancel() } catch {} }
        defer { timer.cancel() }
        return try await withTaskCancellationHandler {
            let value = try await result.value(); try Task.checkCancellation(); return value
        } onCancel: {
            worker.cancel(); Task { await result.complete(.failure(CancellationError())) }
        }
    }
    public func drain() async { await owned?.value }
    private func finished(_ value: Result<ClipboardImageInfo, any Error>, result: ImageReadResult) async {
        owned = nil; await result.complete(value)
    }
}
private actor ImageReadResult {
    private var result: Result<ClipboardImageInfo, any Error>?
    private var waiting: CheckedContinuation<ClipboardImageInfo, any Error>?
    func value() async throws -> ClipboardImageInfo {
        if let result { return try result.get() }
        return try await withCheckedThrowingContinuation { waiting = $0 }
    }
    func complete(_ value: Result<ClipboardImageInfo, any Error>) {
        guard result == nil else { return }; result = value; waiting?.resume(with: value); waiting = nil
    }
}
public struct ClipboardImageInfo: Sendable {
    public let suffix: String
    public let mime: String
    public let typeIdentifier: String
    public let width: Int64
    public let height: Int64
    public init(suffix: String, mime: String, typeIdentifier: String, width: Int64, height: Int64) {
        self.suffix = suffix; self.mime = mime; self.typeIdentifier = typeIdentifier; self.width = width; self.height = height
    }
}
