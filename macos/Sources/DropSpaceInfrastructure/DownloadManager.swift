import Foundation
import Darwin
import DropSpaceCore

public actor DownloadManager {
    private let directory: URL
    private let engine: HTTPDownloader
    private var records: [DownloadRecord] = []
    private var running: [UUID:Task<Void,Never>] = [:]
    private let actions = DownloadPermitPool(limit: 1)
    private var stopping = false
    private var cleanupTimer: Task<Void, Never>?
    private var metricSequence: [UUID: UInt64] = [:]
    private var completion: (@Sendable (URL) async -> Void)?
    public func setCompletionHandler(_ handler: @escaping @Sendable (URL) async -> Void) { completion = handler }
    public init(directory: URL,budget: DownloadBudget) throws {
        self.directory = directory; engine = HTTPDownloader(budget:budget)
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        let file = directory.appendingPathComponent("history.json")
        if FileManager.default.fileExists(atPath:file.path) {
            let reader = try DownloadFiles(staging: file)
            let bytes = try reader.read(file, maximum: 8 * 1024 * 1024 + 1) ?? Data()
            records = try JSONDecoder().decode([DownloadRecord].self,from:bytes)
            for index in records.indices { records[index].bytesPerSecond = 0; records[index].activeConnections = 0 }
            for index in records.indices where [.queued,.connecting,.downloading,.verifying].contains(records[index].phase) { records[index].phase = .paused }
            for index in records.indices {
                let record = records[index]
                guard record.cleanupPending == true, [.completed, .cancelled].contains(record.phase) else { continue }
                do {
                    try DownloadStaging.clean(directory.appendingPathComponent(records[index].id.uuidString + ".part"))
                    records[index].cleanupPending = false; records[index].cleanupError = nil
                } catch { records[index].cleanupError = Self.code(error) }
            }
            records.removeAll { $0.historyRemovalPending == true && $0.cleanupPending == false }
            try JSONEncoder().encode(records).write(to: file, options: .atomic)
        }
    }
    public func activate() { scheduleCleanup() }
    public func history() -> [DownloadRecord] { records.filter { $0.historyRemovalPending != true }.sorted { $0.created > $1.created } }
    public func add(source: URL, directory: URL, name: String? = nil) async throws -> UUID {
        try await action { owner in try owner.enqueue(source: source, directory: directory, name: name) }
    }
    private func enqueue(source: URL, directory: URL, name: String?) throws -> UUID {
        let safe = try DownloadRequestRules.validate(source: source, directory: directory, name: name)
        guard records.count < 10000 else { throw DropSpaceError.limit("Download history is full") }
        let record = DownloadRecord(source: source, destination: directory.appendingPathComponent(safe))
        try commit { $0.append(record) }; start(record.id); return record.id
    }
    public func resume(_ id: UUID) async throws {
        try await action { owner in
            guard let index = owner.records.firstIndex(where: { $0.id == id }), owner.running[id] == nil,
                  [.paused, .failed].contains(owner.records[index].phase) else { return }
            try owner.commit { $0[index].phase = .queued; $0[index].error = nil }; owner.start(id)
        }
    }
    public func pause(_ id: UUID) async throws { try await action { owner in try await owner.settle(id) } }
    private func settle(_ id: UUID) async throws {
        guard let task = running[id] else { return }; task.cancel(); await task.value
        if let index = records.firstIndex(where: { $0.id == id }), records[index].phase != .completed { records[index].phase = .paused }
        try persist()
    }
    public func cancel(_ id: UUID) async throws {
        try await action { owner in
            try await owner.settle(id)
            guard let index = owner.records.firstIndex(where: { $0.id == id }), owner.records[index].phase != .completed else { return }
            try owner.commit { $0[index].phase = .cancelled; $0[index].cleanupPending = true }
            try owner.finishCleanup(id)
        }
    }
    public func removeHistory(_ id: UUID) async throws {
        try await action { owner in
            guard let index = owner.records.firstIndex(where: { $0.id == id }), [.completed, .cancelled].contains(owner.records[index].phase) else { return }
            // A durable hidden row owns failed cleanup even after the UI removes it.
            try owner.commit { $0[index].historyRemovalPending = true; $0[index].cleanupPending = true }
            try owner.finishCleanup(id)
        }
    }
    public func retryCleanup(_ id: UUID) async throws {
        try await action { owner in
            guard owner.running[id] == nil, owner.records.contains(where: { $0.id == id && $0.cleanupPending == true }) else { return }
            try owner.finishCleanup(id)
        }
    }
    public func shutdown() async {
        stopping = true; cleanupTimer?.cancel(); cleanupTimer = nil
        let tasks = Array(running.values); for task in tasks { task.cancel() }; for task in tasks { await task.value }
        // A cancellation should not suppress the final stopped-state checkpoint.
        try? persist()
    }
    private func action<T: Sendable>(_ operation: @Sendable (isolated DownloadManager) async throws -> T) async throws -> T {
        let lease = try await actions.acquire()
        do {
            try Task.checkCancellation(); guard !stopping else { throw CancellationError() }
            let result = try await operation(self); await actions.release(lease); return result
        } catch { await actions.release(lease); throw error }
    }
    private func commit(_ change: (inout [DownloadRecord]) -> Void) throws {
        let previous = records; change(&records)
        do { try persist() } catch { records = previous; throw error }
    }
    private func finishCleanup(_ id: UUID) throws {
        guard let index = records.firstIndex(where: { $0.id == id }), [.completed, .cancelled].contains(records[index].phase) else { return }
        do {
            try cleanStaging(id)
            try commit {
                if $0[index].historyRemovalPending == true { $0.remove(at: index) }
                else { $0[index].cleanupPending = false; $0[index].cleanupError = nil }
            }
        } catch {
            if let index = records.firstIndex(where: { $0.id == id }) { records[index].cleanupPending = true; records[index].cleanupError = Self.code(error) }
            try? persist(); scheduleCleanup(); throw error
        }
    }
    private func scheduleCleanup() {
        guard !stopping, cleanupTimer == nil, records.contains(where: { $0.cleanupPending == true }) else { return }
        cleanupTimer = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(30)) } catch { return }
            await self?.retryPendingCleanup()
        }
    }
    private func retryPendingCleanup() async {
        cleanupTimer = nil
        let ids = records.filter { $0.cleanupPending == true }.map(\.id)
        for id in ids { try? await retryCleanup(id) }; scheduleCleanup()
    }
    private static func code(_ error: any Error) -> String {
        if let value = error as? URLError { return "URL:\(value.code.rawValue)" }
        if let value = error as? POSIXError { return "POSIX:\(value.code.rawValue)" }
        return String(describing: type(of: error))
    }
    private func start(_ id: UUID) {
        guard running[id] == nil else { return }
        running[id] = Task { [weak self] in await self?.run(id) }
    }
    private func run(_ id: UUID) async {
        defer { running[id] = nil; metricSequence[id] = nil; scheduleCleanup() }
        metricSequence[id] = 0
        guard let record = records.first(where: { $0.id == id }) else { return }
        do {
            try await engine.download(source:record.source,staging:staging(id),sha256:record.expectedSHA256,metrics: { [weak self] metrics in await self?.progress(id, metrics: metrics) })
            try Task.checkCancellation()
            if let index = records.firstIndex(where: { $0.id == id }) { records[index].phase = .verifying; records[index].stage = .publishing }
            let output = try publish(staging(id),to:record.destination)
            if let index = records.firstIndex(where: { $0.id == id }) { records[index].phase = .completed; records[index].stage = .completed; records[index].destination = output; records[index].error = nil; records[index].cleanupPending = true }
            try persist()
            try? finishCleanup(id)
            await completion?(output)
        } catch {
            if let index = records.firstIndex(where: { $0.id == id }) {
                // Once published, a journal/cleanup error cannot turn the actual
                // user file into a failed transfer or cause a duplicate retry.
                if records[index].phase != .completed { records[index].phase = Task.isCancelled ? .paused : .failed }
                records[index].error = Task.isCancelled ? nil : Self.code(error)
            }
            try? persist()
        }
    }
    private func progress(_ id: UUID, metrics: DownloadMetrics) {
        guard let index = records.firstIndex(where: { $0.id == id }), let sequence = metricSequence[id], metrics.sequence > sequence else { return }
        metricSequence[id] = metrics.sequence
        records[index].received = metrics.received; records[index].total = metrics.total
        records[index].bytesPerSecond = metrics.bytesPerSecond; records[index].activeConnections = metrics.activeConnections; records[index].stage = metrics.stage
        switch metrics.stage {
        case .checking, .queued: records[index].phase = .queued
        case .waitingConnection, .connecting, .probing: records[index].phase = .connecting
        case .transferring, .retryWaiting: records[index].phase = .downloading
        case .merging, .verifying, .publishing, .completed: records[index].phase = .verifying
        }
    }
    private func staging(_ id: UUID) -> URL { directory.appendingPathComponent(id.uuidString + ".part") }
    private func cleanStaging(_ id: UUID) throws {
        try DownloadStaging.clean(staging(id))
    }
    private func persist() throws { try JSONEncoder().encode(records).write(to:directory.appendingPathComponent("history.json"),options:.atomic) }
    private func publish(_ file: URL,to destination: URL) throws -> URL {
        let folder = destination.deletingLastPathComponent()
        try FileManager.default.createDirectory(at:folder,withIntermediateDirectories:true)
        let temporary = folder.appendingPathComponent(".dropspace-" + UUID().uuidString + ".download")
        try FileManager.default.copyItem(at:file,to:temporary)
        defer { try? FileManager.default.removeItem(at:temporary) }
        let handle = try FileHandle(forWritingTo:temporary); try handle.synchronize(); try handle.close()
        for index in 0..<10000 {
            let name = index == 0 ? destination.lastPathComponent : destination.deletingPathExtension().lastPathComponent + " (\(index))" + (destination.pathExtension.isEmpty ? "" : "." + destination.pathExtension)
            let target = folder.appendingPathComponent(name)
            if renamex_np(temporary.path,target.path,UInt32(RENAME_EXCL)) == 0 { try? FileManager.default.removeItem(at:file); return target }
            if errno != EEXIST { throw POSIXError(POSIXErrorCode(rawValue:errno) ?? .EIO) }
        }
        throw DropSpaceError.storage("No available download filename")
    }
}
