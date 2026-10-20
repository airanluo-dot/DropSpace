import Foundation
import Darwin
import DropSpaceCore

/// Native metadata calls may remain blocked after cancellation or a timeout.
/// Their worker retains its permit until the underlying call actually returns.
public actor FileReferenceService {
    struct Timing: Sendable {
        var acquisition: Duration = .seconds(2)
        var operation: Duration = .seconds(5)
        var cache: TimeInterval = 2
    }
    private struct Key: Hashable {
        let path: String
        let bookmark: Data?
        let volume: String?
        let kind: Int
    }
    private struct Pending {
        let id: UUID
        let remote: Bool
        let continuation: CheckedContinuation<Void, any Error>
    }
    private var localCount = 0
    private var remoteCount = 0
    private var pending: [Pending] = []
    private var workers: [UUID: Task<Void, Never>] = [:]
    private var results: [UUID: any PendingMetadataResult] = [:]
    private var cache: [Key: (Date, FileAvailability)] = [:]
    private var stopped = false
    private let timing: Timing
    private let remotePath: @Sendable (String) -> Bool
    private let inspect: @Sendable (FileReference, ItemKind) -> FileAvailability
    public init() {
        timing = Timing(); remotePath = Self.isRemote; inspect = FileIntake.availability
    }
    init(timing: Timing, remotePath: @escaping @Sendable (String) -> Bool,
         inspect: @escaping @Sendable (FileReference, ItemKind) -> FileAvailability) {
        self.timing = timing; self.remotePath = remotePath; self.inspect = inspect
    }
    public func check(_ reference: FileReference, kind: ItemKind) async throws -> FileAvailability {
        try Task.checkCancellation()
        guard !stopped else { throw CancellationError() }
        let remote = remotePath(reference.path)
        // No case folding: APFS can be case sensitive. Bookmark/volume/kind
        // separate references that happen to reuse the same pathname.
        let key = Key(path: reference.path, bookmark: reference.bookmark, volume: reference.volumeID, kind: kind.rawValue)
        if remote, let cached = cache[key], Date().timeIntervalSince(cached.0) <= timing.cache { return cached.1 }
        let operation = inspect
        let value = try await run(remote: remote) { operation(reference, kind) }
        if remote, !stopped {
            let now = Date()
            cache = cache.filter { now.timeIntervalSince($0.value.0) <= timing.cache }
            if cache[key] == nil, cache.count >= 1024, let oldest = cache.min(by: { $0.value.0 < $1.value.0 })?.key {
                cache.removeValue(forKey: oldest)
            }
            cache[key] = (now, value)
        }
        return value
    }
    public func prepare(_ urls: [URL], source: ItemSource, settings: AppSettings) async throws -> IntakeResult {
        var batch = try FileIntake.Batch(count: urls.count, source: source, settings: settings)
        for url in urls {
            try Task.checkCancellation()
            guard !stopped else { throw CancellationError() }
            if batch.stopped { break }
            do {
                // Intake and availability share the same native worker budgets.
                let candidate = try await run(remote: remotePath(url.path)) { try FileIntake.inspect(url, source: source) }
                batch.accept(candidate)
            } catch is CancellationError { throw CancellationError() }
            catch { batch.reject(url, error: error) }
        }
        return batch.finish()
    }
    private func run<Value: Sendable>(remote: Bool, operation: @escaping @Sendable () throws -> Value) async throws -> Value {
        try Task.checkCancellation()
        guard !stopped else { throw CancellationError() }
        try await acquire(remote: remote)
        do { try Task.checkCancellation(); guard !stopped else { throw CancellationError() } }
        catch { release(remote: remote); throw error }
        let result = FileMetadataResult<Value>()
        let id = UUID()
        results[id] = result
        workers[id] = Task.detached(priority: .utility) {
            let value: Result<Value, any Error>
            do { try Task.checkCancellation(); value = .success(try operation()) }
            catch { value = .failure(error) }
            await self.finished(id: id, remote: remote, result: result, value: value)
        }
        let timer: Task<Void, Never>? = remote ? Task {
            do {
                try await Task.sleep(for: timing.operation)
                await result.complete(.failure(DropSpaceError.unavailable("Remote file metadata inspection timed out.")))
            } catch {}
        } : nil
        defer { timer?.cancel() }
        let value = try await withTaskCancellationHandler {
            let value = try await result.value()
            try Task.checkCancellation()
            return value
        } onCancel: {
            Task { await result.complete(.failure(CancellationError())) }
        }
        return value
    }
    private func acquire(remote: Bool) async throws {
        let id = UUID()
        let timer: Task<Void, Never>? = remote ? Task {
            do {
                try await Task.sleep(for: timing.acquisition)
                cancelWait(id, error: DropSpaceError.unavailable("Remote file metadata capacity is temporarily unavailable."))
            } catch {}
        } : nil
        defer { timer?.cancel() }
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try await withCheckedThrowingContinuation { continuation in
                if (remote ? remoteCount < 2 : localCount < 8) {
                    if remote { remoteCount += 1 } else { localCount += 1 }
                    continuation.resume()
                } else { pending.append(Pending(id: id, remote: remote, continuation: continuation)) }
            }
        } onCancel: { Task { await self.cancelWait(id, error: CancellationError()) } }
    }
    private func cancelWait(_ id: UUID, error: any Error) {
        guard let index = pending.firstIndex(where: { $0.id == id }) else { return }
        pending.remove(at: index).continuation.resume(throwing: error)
    }
    private func release(remote: Bool) {
        if let index = pending.firstIndex(where: { $0.remote == remote }) {
            pending.remove(at: index).continuation.resume()
        } else if remote { remoteCount -= 1 } else { localCount -= 1 }
    }
    private func finished<Value: Sendable>(id: UUID, remote: Bool, result: FileMetadataResult<Value>, value: Result<Value, any Error>) async {
        workers.removeValue(forKey: id); results.removeValue(forKey: id); release(remote: remote)
        await result.complete(value)
    }
    public func shutdown(timeout: Duration = .seconds(5)) async -> Bool {
        stopped = true; cache.removeAll()
        for wait in pending { wait.continuation.resume(throwing: CancellationError()) }; pending.removeAll()
        for worker in workers.values { worker.cancel() }
        for result in results.values { await result.cancel() }
        let deadline = ContinuousClock.now.advanced(by: timeout)
        while !workers.isEmpty, ContinuousClock.now < deadline {
            do { try await Task.sleep(for: .milliseconds(25)) } catch { return false }
        }
        return workers.isEmpty
    }
    var diagnostics: (local: Int, remote: Int, waiting: Int, cached: Int) { (localCount, remoteCount, pending.count, cache.count) }

    /// MNT_NOWAIT reads the kernel's mount table without contacting a remote server.
    /// Missing external-volume paths use the conservative remote queue.
    static func isRemote(_ path: String) -> Bool {
        let count = getfsstat(nil, 0, MNT_NOWAIT)
        guard count >= 0 else { return true }
        var mounts: [statfs] = .init(repeating: .init(), count: Int(count) + 16)
        let bytes = Int32(mounts.count * MemoryLayout<statfs>.stride)
        let actual = mounts.withUnsafeMutableBufferPointer { getfsstat($0.baseAddress, bytes, MNT_NOWAIT) }
        guard actual >= 0 else { return true }
        var best: (String, Bool)?
        for var mount in mounts.prefix(Int(actual)) {
            let name = withUnsafePointer(to: &mount.f_mntonname) {
                $0.withMemoryRebound(to: CChar.self, capacity: Int(MNAMELEN)) { String(cString: $0) }
            }
            guard path == name || path.hasPrefix(name == "/" ? "/" : name + "/") else { continue }
            if best == nil || name.count > best!.0.count { best = (name, (mount.f_flags & UInt32(MNT_LOCAL)) == 0) }
        }
        if let best, best.0 != "/" { return best.1 }
        return path.hasPrefix("/Volumes/") || path.hasPrefix("/Network/") || best?.1 != false
    }
}

private protocol PendingMetadataResult: Sendable { func cancel() async }
private actor FileMetadataResult<Value: Sendable>: PendingMetadataResult {
    private var result: Result<Value, any Error>?
    private var waiting: CheckedContinuation<Value, any Error>?
    func value() async throws -> Value {
        if let result { return try result.get() }
        return try await withCheckedThrowingContinuation { waiting = $0 }
    }
    func cancel() { complete(.failure(CancellationError())) }
    func complete(_ value: Result<Value, any Error>) {
        guard result == nil else { return }
        result = value; waiting?.resume(with: value); waiting = nil
    }
}
