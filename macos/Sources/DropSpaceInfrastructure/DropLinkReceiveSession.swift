import Foundation
import DropSpaceCore

actor DropLinkReceiveSession {
    let peer: UUID
    private var record: DropLinkTransferRecord
    private let repository: DropLinkTransferRepository
    private let files: TransferReceiveFiles
    private let gate = DownloadPermitPool(limit: 1)
    private var finalization: Task<TransferCompleteResponse, Never>?
    private var retired = false
    private var received: [UUID: Set<Int32>] = [:]
    init(record: DropLinkTransferRecord, repository: DropLinkTransferRepository, files: TransferReceiveFiles) {
        self.record = record; peer = record.peer; self.repository = repository; self.files = files
    }
    private func mutation<T: Sendable>(_ operation: @Sendable (isolated DropLinkReceiveSession) async throws -> T) async throws -> T {
        let permit = try await gate.acquire()
        do { try Task.checkCancellation(); let result = try await operation(self); await gate.release(permit); return result }
        catch { await gate.release(permit); throw error }
    }
    func snapshot(now: Date = Date()) async throws -> TransferStatusResponse {
        try await mutation { owner in
            if !owner.record.state.isTerminal { owner.record.lastActivity = now }
            return .init(session: owner.record.id, state: owner.record.state, bytes: owner.record.transferredBytes,
                         chunks: Dictionary(uniqueKeysWithValues: owner.received.map { ($0.key.dropLinkD, $0.value.sorted()) }), completed: owner.record.completedPaths, error: owner.record.errorCategory)
        }
    }
    func approve(_ accepted: Bool, now: Date = Date()) async throws -> Bool {
        try await mutation { owner in
            guard !owner.retired, owner.record.state == .awaitingApproval else { return false }
            var next = owner.record; next.state = accepted ? .accepted : .rejected; next.completedAt = accepted ? nil : now
            next.errorCategory = accepted ? nil : "rejected"; next.lastActivity = now
            try await owner.repository.save(next); owner.record = next; return true
        }
    }
    func cancel(now: Date = Date()) async throws -> Bool {
        try await mutation { owner in
            guard owner.record.state != .verifying else { return false }
            if !owner.record.state.isTerminal {
                var next = owner.record; next.state = .cancelled; next.completedAt = now; next.lastActivity = now; next.errorCategory = "cancelled"
                try await owner.repository.save(next); owner.record = next
            }
            return true
        }
    }
    func chunk(item id: UUID, index: Int32, body: Data, hash: String, now: Date = Date()) async throws -> DropLinkHTTPResponse {
        try await mutation { owner in
            guard !owner.retired, [.accepted, .transferring].contains(owner.record.state) else { return Self.error(409, "transfer-not-accepted") }
            guard let item = owner.record.manifest.items.first(where: { $0.id == id }), let count = item.chunkCount, index >= 0, index < count else { return Self.error(400, "chunk-invalid") }
            let expected = min(Int64(owner.record.manifest.chunkBytes), item.size - Int64(index) * Int64(owner.record.manifest.chunkBytes))
            guard body.count == expected else { return Self.error(400, "chunk-length-invalid") }
            do {
                let duplicate = owner.received[id]?.contains(index) == true
                // Authentication has already verified the HTTP body. The v1
                // receiver acknowledges recorded intact parts before checking
                // the chunk-specific hash header again.
                let retained = duplicate ? try await owner.files.hasChunk(item: item, index: index) : false
                if !retained { _ = try await owner.files.write(item: item, index: index, body: body, hash: hash) }
                if !duplicate {
                    var next = owner.record
                    next.transferredBytes += Int64(body.count); next.state = .transferring; next.lastActivity = now
                    // A failed ledger commit leaves the fragment for the same
                    // index retry, never increments the reported byte count.
                    try await owner.repository.commitChunk(next, item: id, index: index, length: body.count)
                    owner.record = next; owner.received[id, default: []].insert(index)
                }
                var members: [ModuleJSONMember] = [.init("accepted", .boolean(true)), .init("duplicate", .boolean(duplicate)), .init("length", .number(String(body.count)))]
                if !duplicate { members.append(.init("hash", .string(hash))) }
                let result: ModuleJSON = .object(members)
                return .init(status: 200, body: try result.encoded())
            } catch is CancellationError { throw CancellationError() }
            catch let error as DropSpaceError {
                if case .storage = error { throw error }
                return Self.error(400, String(describing: error))
            }
        }
    }
    func complete() async throws -> TransferCompleteResponse {
        let operation: Task<TransferCompleteResponse, Never>? = try await mutation { owner in
            if let finalization = owner.finalization { return finalization }
            guard !owner.retired, [.accepted, .transferring].contains(owner.record.state) else { return nil }
            var next = owner.record; next.state = .verifying; next.lastActivity = Date(); next.errorCategory = nil
            try await owner.repository.save(next); owner.record = next
            // Independent of any one HTTP request. A disconnected duplicate
            // must not cancel the sole owner while it is publishing files.
            let task = Task { await owner.finalize() }; owner.finalization = task; return task
        }
        if let operation { return await operation.value }
        if record.state.isTerminal { return record.completion }
        return .init(session: record.id, state: record.state, completed: record.completedPaths, error: "transfer-not-accepted")
    }
    private func finalize() async -> TransferCompleteResponse {
        let deadline = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(1800)); await self?.cancelFinalization() } catch {}
        }
        defer { deadline.cancel() }
        do {
            for item in record.manifest.items {
                try Task.checkCancellation()
                guard received[item.id, default: []].count == Int(item.chunkCount ?? -1) else { throw DropSpaceError.invalid("Transfer item missing chunks") }
                do { record.completedPaths.append(try await files.commit(item)) }
                catch let failure as TransferPublicationFailure { record.completedPaths.append(failure.relativePath); throw failure }
                try await repository.save(record)
            }
            var next = record; next.state = .completed; next.completedAt = Date(); next.lastActivity = Date(); next.errorCategory = nil
            try await repository.save(next); record = next
        } catch {
            record.state = .failed; record.completedAt = Date(); record.lastActivity = Date()
            record.errorCategory = error is CancellationError ? "cancelled-during-finalization" : String(describing: type(of: error))
            do { try await repository.save(record) } catch { record.errorCategory = "transfer-persistence-failure" }
        }
        return record.completion
    }
    private func cancelFinalization() { finalization?.cancel() }
    func expire(now: Date) async throws -> Bool {
        try await mutation { owner in
            if !owner.record.state.isTerminal, now.timeIntervalSince(owner.record.lastActivity) > 1800 {
                // The finalization owner observes its own 30-minute deadline;
                // never dispose its descriptors beneath an active native write.
                if owner.record.state == .verifying { owner.finalization?.cancel(); return false }
                var next = owner.record; next.state = .failed; next.completedAt = now; next.errorCategory = "session-timeout"
                try await owner.repository.save(next); owner.record = next
            }
            return owner.record.state.isTerminal && now.timeIntervalSince(owner.record.completedAt ?? owner.record.lastActivity) >= owner.record.state.retention
        }
    }
    func retire() async {
        retired = true; finalization?.cancel(); _ = await finalization?.value
        do {
            try await mutation { owner in
                if !owner.record.state.isTerminal {
                    owner.record.state = .cancelled; owner.record.completedAt = Date(); owner.record.errorCategory = "host-stopped"
                    try await owner.repository.save(owner.record)
                }
                do { try await owner.files.cleanup(); owner.record.pendingCleanup = false; owner.record.cleanupError = nil }
                catch { owner.record.pendingCleanup = true; owner.record.cleanupError = error.localizedDescription }
                try await owner.repository.save(owner.record)
            }
        } catch { /* The persisted lease intent remains pending for recovery. */ }
    }
    func retryCleanup() async {
        do {
            try await mutation { owner in
                guard owner.record.state.isTerminal, owner.record.cleanupError != nil else { return }
                do { try await owner.files.cleanup(); owner.record.pendingCleanup = false; owner.record.cleanupError = nil }
                catch { owner.record.cleanupError = error.localizedDescription }
                try await owner.repository.save(owner.record)
            }
        } catch { /* Existing pending state remains the recovery authority. */ }
    }
    static func error(_ status: Int, _ category: String) -> DropLinkHTTPResponse {
        .init(status: status, body: (try? ModuleJSON.object([.init("error", .string(category))]).encoded()) ?? Data())
    }
}
