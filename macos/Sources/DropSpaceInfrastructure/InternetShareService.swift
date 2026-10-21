import Foundation
import CryptoKit
import DropSpaceCore

public actor InternetShareService {
    typealias BackendFactory = @Sendable (URL, DownloadBudget) throws -> any ShareBackendClient
    public nonisolated let isConfigured: Bool
    private let endpoint: URL?, store: InternetShareStore, stagingRoot: URL, budget: DownloadBudget
    private let backendFactory: BackendFactory
    private let cleanup = InternetShareStagingCleanup()
    private var initialization: Task<Void, any Error>?
    private var uploads: [UUID: Task<ShareDescriptor, any Error>] = [:]
    private var revocations: [UUID: Task<Void, any Error>] = [:]
    private var retainedSessions: [UUID: ShareBackendSession] = [:]
    private var stopped = false
    private var calls = Set<UUID>()
    private var callWaiters: [CheckedContinuation<Void, Never>] = []
    private var retirement: Task<Void, Never>?

    public init(directory: URL, endpoint: URL?, budget: DownloadBudget, keys: any DropLinkSecretStorage = KeychainStore()) throws {
        try self.init(directory: directory, endpoint: endpoint, budget: budget, keys: keys, backendFactory: { try CloudflareShareBackend(endpoint: $0, budget: $1) })
    }
    init(directory: URL, endpoint: URL?, budget: DownloadBudget, keys: any DropLinkSecretStorage, backendFactory: @escaping BackendFactory) throws {
        self.endpoint = endpoint.flatMap { ShareBackendSession.safe($0) ? $0 : nil }; isConfigured = self.endpoint != nil
        self.budget = budget; self.backendFactory = backendFactory
        store = try InternetShareStore(directory: directory, keys: keys); stagingRoot = directory.appendingPathComponent("staging", isDirectory: true)
    }
    public func initialize() async throws {
        if initialization == nil { initialization = Task { try await self.recover() } }
        do { try await initialization!.value } catch { initialization = nil; throw error }
    }
    public func records() async throws -> [InternetShareRecord] { try await store.records() }
    public func create(_ sources: [ShareSource], enabled: Bool, lifetime: TimeInterval = 86400) async throws -> ShareDescriptor {
        guard !stopped else { throw DropSpaceError.unavailable("Internet sharing is shutting down") }
        guard enabled else { throw DropSpaceError.unavailable("Internet Share is disabled in settings") }
        guard let endpoint else { throw DropSpaceError.unavailable("Internet Share requires DROPSPACE_SHARE_BACKEND_URL to name an HTTPS backend") }
        let call = UUID(); calls.insert(call); defer { finishCall(call) }
        guard lifetime.isFinite, lifetime > 0, lifetime <= 7 * 86400, (1...100).contains(sources.count) else { throw DropSpaceError.invalid("Internet share lifetime or item count") }
        var total: Int64 = 0
        for source in sources {
            try source.validate()
            guard source.length <= 2_147_483_648 - total else { throw DropSpaceError.limit("Internet share total bytes") }; total += source.length
        }
        guard total > 0 else { throw DropSpaceError.invalid("Internet share must contain at least one byte") }
        try await initialize(); try Task.checkCancellation()
        guard !stopped else { throw CancellationError() }
        let record = try await store.reserve(endpoint: endpoint, expires: Date().addingTimeInterval(lifetime), count: sources.count, bytes: total, name: sources[0].name)
        // Register the owned task before its first suspension. Shutdown and
        // caller cancellation retire this same task; no detached upload remains.
        let task = Task { try await self.perform(record, sources: sources, lifetime: lifetime) }
        uploads[record.id] = task
        defer { uploads[record.id] = nil }
        if stopped { task.cancel() }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    private func perform(_ reservation: InternetShareRecord, sources: [ShareSource], lifetime: TimeInterval) async throws -> ShareDescriptor {
        var record = reservation
        var remoteAttempted = false
        do {
            try Task.checkCancellation()
            let staging = try InternetShareStaging(root: stagingRoot, id: record.id, count: sources.count)
            try await staging.stage(sources)
            let permit = try await budget.transfers.acquire(deadline: .now.advanced(by: .seconds(1800)))
            let result: ShareDescriptor
            do {
                try Task.checkCancellation()
                let backend = try backendFactory(record.endpoint, budget)
                record = try await store.beginCreation(record.id, lifetime: lifetime); remoteAttempted = true
                let session = try await backend.create(id: record.id, expires: record.expiresAt, count: record.itemCount, bytes: record.totalBytes)
                try session.validate(origin: record.endpoint); retainedSessions[record.id] = session
                // Cancellation must not drop the only authenticated revoke handle.
                // Once remote creation replies, persist custody before any payload.
                let durable = Task { try await self.store.attach(session, id: record.id) }
                try await durable.value; try Task.checkCancellation()
                let key = ShareCrypto.createMasterKey()
                let items = sources.map { source in
                    EncryptedShareManifestItem(fileID: UUID(), name: source.name, mime: source.mime, length: source.length,
                        chunks: Int32(source.length == 0 ? 0 : 1 + (source.length - 1) / Int64(ShareLimits.chunkPlainBytes)), hash: source.sha256,
                        noncePrefix: SymmetricKey(size: .bits128).withUnsafeBytes { Data($0.prefix(8)) })
                }
                let manifest = try ShareCrypto.encryptManifest(key: key, share: record.id, items: items)
                guard manifest.count <= ShareLimits.maxManifestBytes else { throw DropSpaceError.limit("Encrypted share manifest") }
                try await backend.upload(session: session, name: "manifest.bin", ciphertext: manifest)
                for (index, item) in items.enumerated() {
                    var digest = SHA256(), offset: Int64 = 0
                    for chunk in 0..<item.chunkCount {
                        try Task.checkCancellation()
                        var plain = try await staging.read(index: index, offset: offset, count: Int(min(Int64(ShareLimits.chunkPlainBytes), item.plainLength - offset)))
                        defer { plain.resetBytes(in: plain.startIndex..<plain.endIndex) }
                        digest.update(data: plain)
                        let ciphertext = try ShareCrypto.encryptChunk(key: key, share: record.id, file: item.fileId, index: chunk, plaintext: plain, noncePrefix: item.noncePrefix)
                        try await backend.upload(session: session, name: item.fileId.dropLinkN + ".\(chunk).bin", ciphertext: ciphertext)
                        offset += Int64(plain.count)
                    }
                    guard digest.finalize().map({ String(format: "%02x", $0) }).joined() == item.sha256 else { throw DropSpaceError.invalid("Staged share bytes changed during upload") }
                }
                try Task.checkCancellation()
                let base = session.downloadBaseUrl.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                guard let url = URL(string: base + "/s/" + record.id.dropLinkN + "#k=" + (try ShareCrypto.fragment(key))) else { throw DropSpaceError.invalid("Secure share URL") }
                try await store.transition(record.id, to: .completed)
                result = ShareDescriptor(id: record.id, url: url, expiresAt: record.expiresAt, itemCount: record.itemCount, totalBytes: record.totalBytes, isEncrypted: true)
                await budget.transfers.release(permit)
            } catch { await budget.transfers.release(permit); throw error }
            // Cleanup is independent of a caller that cancelled after the commit.
            await independentCleanup(record)
            if Task.isCancelled || stopped {
                throw CancellationError()
            }
            retainedSessions[record.id] = nil
            return result
        } catch {
            await failedUpload(record, remoteAttempted: remoteAttempted, cause: error)
            throw error
        }
    }
    private func failedUpload(_ record: InternetShareRecord, remoteAttempted: Bool, cause: any Error) async {
        let reason = "Upload did not complete (\(String(reflecting: type(of: cause))))"
        let task = Task {
            if remoteAttempted {
                try? await self.store.transition(record.id, to: .revokePending, error: reason)
                do { try await self.revokeWithDeadline(record, seconds: 15) } catch {
                    try? await self.store.transition(record.id, to: .revokePending, error: "Remote cleanup remains pending (\(String(reflecting: type(of: error))))")
                }
            } else { try? await self.store.didRevoke(record.id) }
            await self.cleanStaging(record)
        }
        await task.value
    }
    private func independentCleanup(_ record: InternetShareRecord) async { let task = Task { await self.cleanStaging(record) }; await task.value }
    private func cleanStaging(_ record: InternetShareRecord) async {
        do { try await cleanup.remove(root: stagingRoot, id: record.id, count: record.itemCount); try await store.stagingCleaned(record.id) }
        catch { try? await store.stagingCleaned(record.id, error: "Local staging cleanup remains pending (\(String(reflecting: type(of: error))))") }
    }
    public func revoke(_ id: UUID) async throws {
        guard !stopped else { throw DropSpaceError.unavailable("Internet sharing is shutting down") }
        let call = UUID(); calls.insert(call); defer { finishCall(call) }
        try await initialize()
        try Task.checkCancellation(); guard !stopped else { throw CancellationError() }
        if let upload = uploads[id] { upload.cancel(); _ = await upload.result }
        if let prior = revocations[id] { return try await prior.value }
        guard let record = try await store.record(id) else { return }
        let task = Task {
            if record.state != .revoked { try await self.store.transition(id, to: .revokePending) }
            do { try await self.revokeWithDeadline(record, seconds: 120) }
            catch {
                try? await self.store.transition(id, to: .revokePending, error: "Remote revoke remains pending (\(String(reflecting: type(of: error))))")
                await self.cleanStaging(record); throw error
            }
            await self.cleanStaging(record)
        }
        revocations[id] = task; defer { revocations[id] = nil }
        try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    private func revokeWithDeadline(_ record: InternetShareRecord, seconds: Double) async throws {
        guard let current = try await store.record(record.id) else { return }
        if current.state == .revoked { try await store.didRevoke(record.id); return }
        let session: ShareBackendSession
        if let retained = retainedSessions[record.id] { session = retained }
        else if let persisted = try await store.session(record.id) { session = persisted }
        else { throw DropSpaceError.unavailable("The share's revoke authorization is unavailable. Remote deletion cannot be confirmed; the share remains recorded until its backend expiry") }
        let backend = try backendFactory(record.endpoint, budget)
        try await withThrowingTaskGroup(of: Void.self) { tasks in
            tasks.addTask { try await backend.revoke(session: session, id: record.id) }
            tasks.addTask { try await Task.sleep(for: .seconds(seconds)); throw URLError(.timedOut) }
            defer { tasks.cancelAll() }; try await tasks.next()
        }
        try await store.didRevoke(record.id); retainedSessions[record.id] = nil
    }
    private func recover() async throws {
        try await store.recoverExpired()
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        for record in try await store.records() {
            try Task.checkCancellation()
            if record.stagingPending { await cleanStaging(record) }
            if record.state == .reserved { try await store.didRevoke(record.id); continue }
            if record.state == .completed || record.state == .revoked { continue }
            let remaining = ContinuousClock.now.duration(to: deadline)
            let seconds = Double(remaining.components.seconds) + Double(remaining.components.attoseconds) / 1e18
            guard seconds > 0 else { continue }
            do { try await revokeWithDeadline(record, seconds: seconds) }
            catch { try? await store.transition(record.id, to: .revokePending, error: "Startup remote cleanup remains pending (\(String(reflecting: type(of: error))))") }
        }
    }
    public func shutdown() async -> Bool {
        stopped = true; initialization?.cancel()
        let active = Array(uploads.values), revoking = Array(revocations.values)
        for task in active { task.cancel() }; for task in revoking { task.cancel() }
        if retirement == nil {
            retirement = Task {
                _ = await self.initialization?.result
                await self.waitCalls()
            }
        }
        return await NativeRetirementWait.wait(retirement!, seconds: 10)
    }
    private func waitCalls() async {
        if calls.isEmpty { return }
        await withCheckedContinuation { callWaiters.append($0) }
    }
    private func finishCall(_ id: UUID) {
        calls.remove(id)
        if calls.isEmpty { let waiters = callWaiters; callWaiters.removeAll(); for waiter in waiters { waiter.resume() } }
    }
}
