import Foundation
import Darwin
import DropSpaceCore

public struct IncomingTransferOffer: Sendable {
    public let sessionID: UUID
    public let peerID: UUID
    public let manifest: TransferManifest
}
public actor DropLinkTransferHost {
    public typealias Approval = @Sendable (IncomingTransferOffer) async throws -> Bool
    private let repository: DropLinkTransferRepository
    private let staging: URL
    private let destination: URL
    private let approval: Approval
    private var sessions: [UUID: DropLinkReceiveSession] = [:]
    private var notifications: [UUID: Task<Void, Never>] = [:]
    private var lifetime: Task<Void, Never>?
    private var running = false
    private var stopping = false
    private var shutdown: Task<Void, Never>?
    private let admission = DownloadPermitPool(limit: 1)
    public init(directory: URL, destination: URL, approval: @escaping Approval) throws {
        repository = try .init(directory: directory); staging = directory.appendingPathComponent("transfers"); self.destination = destination; self.approval = approval
    }
    public func start() async throws {
        await shutdown?.value
        let permit = try await admission.acquire()
        do {
            guard !stopping else { throw DropSpaceError.unavailable("Transfer host stopping") }
            if !running {
                try await recover()
                guard !stopping else { throw CancellationError() }
                running = true
                lifetime = Task { [weak self] in
                    do { while !Task.isCancelled { try await Task.sleep(for: .seconds(30)); try await self?.sweep() } } catch {}
                }
            }
            await admission.release(permit)
        } catch { await admission.release(permit); throw error }
    }
    public func history() async throws -> [DropLinkTransferRecord] { try await repository.history() }
    public func retryCleanup() async throws {
        let permit = try await admission.acquire()
        do {
            for session in sessions.values { await session.retryCleanup() }
            try await recover(); await admission.release(permit)
        } catch { await admission.release(permit); throw error }
    }
    private func recover() async throws {
        for var record in try await repository.history() where sessions[record.id] == nil {
            if !record.state.isTerminal {
                record.state = .failed; record.completedAt = Date(); record.errorCategory = "process-interrupted"
                try await repository.save(record)
            }
            guard record.pendingCleanup else { continue }
            do {
                // Test the lease itself with a no-follow parent. A missing owner
                // marker in an existing directory is not evidence of cleanup.
                let parent = try TransferFileSystem.directory(staging, create: true); var info = stat()
                if fstatat(parent.raw, record.id.dropLinkN, &info, AT_SYMLINK_NOFOLLOW) != 0 {
                    guard errno == ENOENT else { throw TransferFileSystem.posix() }
                } else {
                    let files = try TransferReceiveFiles(parentURL: staging, manifest: record.manifest, destination: record.destination, existing: true)
                    try await files.cleanup()
                }
                record.pendingCleanup = false; record.cleanupError = nil
            } catch { record.cleanupError = error.localizedDescription }
            try await repository.save(record)
        }
    }
    public func handle(_ request: DropLinkHTTPRequest) async throws -> DropLinkHTTPResponse {
        do { return try await dispatch(request) }
        catch is CancellationError { throw CancellationError() }
        catch let error as DropSpaceError {
            if case .storage = error { return DropLinkReceiveSession.error(500, "transfer-storage-failure") }
            return DropLinkReceiveSession.error(400, "transfer-invalid")
        } catch { return DropLinkReceiveSession.error(500, "transfer-io-failure") }
    }
    private func dispatch(_ request: DropLinkHTTPRequest) async throws -> DropLinkHTTPResponse {
        guard running, !stopping else { return .init(status: 503) }
        guard let peer = request.peerID else { return .init(status: 401) }
        let segments = request.head.path.lowercased().split(separator: "/").map(String.init)
        if segments == ["v1", "transfers", "offers"], request.head.method == "POST" {
            let offer: TransferOfferRequest
            do { offer = try DropLinkTransferJSON.decode(TransferOfferRequest.self, from: request.body); try TransferManifestPolicy.validate(offer.manifest, filenames: .macOS) }
            catch { return DropLinkReceiveSession.error(400, "invalid-manifest") }
            guard offer.peerId == peer else { return .init(status: 401) }
            return try await offerTransfer(offer)
        }
        guard segments.count >= 4, Array(segments.prefix(2)) == ["v1", "transfers"], let id = UUID(uuidString: segments[2]), let session = sessions[id] else { return .init(status: 404) }
        guard session.peer == peer else { return .init(status: 401) }
        if segments.count == 7, segments[3] == "items", segments[5] == "chunks", request.head.method == "PUT",
           let item = UUID(uuidString: segments[4]), let index = Int32(segments[6]) {
            guard let length = request.head.headers["content-length"].flatMap(Int64.init), length == Int64(request.body.count) else { return DropLinkReceiveSession.error(400, "chunk-length-invalid") }
            return try await session.chunk(item: item, index: index, body: request.body, hash: request.head.headers["x-droplink-chunk-sha256"] ?? "")
        }
        guard segments.count == 4 else { return .init(status: 404) }
        switch (request.head.method, segments[3]) {
        case ("GET", "status"): return try await status(session)
        case ("POST", "accept"):
            let choice: TransferAcceptRequest
            do { choice = try DropLinkTransferJSON.decode(TransferAcceptRequest.self, from: request.body) } catch { return .init(status: 400) }
            guard !choice.accepted else { return DropLinkReceiveSession.error(403, "receiver-local-approval-required") }
            _ = try await session.approve(false); notifications[id]?.cancel(); return try await status(session)
        case ("POST", "cancel"):
            guard try await session.cancel() else { return DropLinkReceiveSession.error(409, "transfer-finalizing") }
            notifications[id]?.cancel(); return try await status(session)
        case ("POST", "complete"):
            return .init(status: 200, body: try DropLinkTransferJSON.encode(await session.complete()))
        default: return .init(status: 404)
        }
    }
    private func status(_ session: DropLinkReceiveSession) async throws -> DropLinkHTTPResponse {
        .init(status: 200, body: try DropLinkTransferJSON.encode(await session.snapshot()))
    }
    private func offerTransfer(_ offer: TransferOfferRequest) async throws -> DropLinkHTTPResponse {
        let permit = try await admission.acquire()
        do {
            let response = try await admitOffer(offer); await admission.release(permit); return response
        } catch { await admission.release(permit); throw error }
    }
    private func admitOffer(_ offer: TransferOfferRequest) async throws -> DropLinkHTTPResponse {
        try Task.checkCancellation(); try await sweep()
        guard running, !stopping else { return .init(status: 503) }
        guard sessions.count < 64 else { return .init(status: 429) }
        let id = offer.manifest.sessionId
        guard sessions[id] == nil, try await !repository.contains(id) else { return .init(status: 409) }
        var record = DropLinkTransferRecord(peer: offer.peerId, manifest: offer.manifest, destination: destination, now: Date())
        // Durable custody precedes any staging creation. If creation fails or
        // the process exits, startup has an explicit cleanup obligation.
        try await repository.create(record)
        do {
            let files = try TransferReceiveFiles(parentURL: staging, manifest: offer.manifest, destination: destination)
            let session = DropLinkReceiveSession(record: record, repository: repository, files: files)
            sessions[id] = session
            guard running, !stopping, !Task.isCancelled else { sessions[id] = nil; await session.retire(); throw CancellationError() }
            notifications[id] = Task { [weak self, approval] in
                do {
                    let accepted = try await approval(.init(sessionID: id, peerID: offer.peerId, manifest: offer.manifest))
                    try Task.checkCancellation(); _ = try await session.approve(accepted)
                } catch is CancellationError {} catch { _ = try? await session.approve(false) }
                await self?.notificationFinished(id)
            }
            return .init(status: 200, body: try DropLinkTransferJSON.encode(TransferOfferResponse(session: id, state: .awaitingApproval)))
        } catch {
            record.state = .failed; record.completedAt = Date(); record.errorCategory = "transfer-staging-failure"
            try await repository.save(record); throw error
        }
    }
    private func notificationFinished(_ id: UUID) { notifications[id] = nil }
    private func sweep(now: Date = Date()) async throws {
        for (id, session) in sessions {
            if try await session.expire(now: now) {
                sessions[id] = nil; notifications[id]?.cancel(); await session.retire()
            }
        }
    }
    public func stop() async {
        if let shutdown { await shutdown.value; return }
        running = false; stopping = true
        let operation = Task { await finishStop() }; shutdown = operation
        await operation.value
    }
    private func finishStop() async {
        defer { stopping = false; shutdown = nil }
        lifetime?.cancel(); await lifetime?.value; lifetime = nil
        let permit: UUID
        do { permit = try await admission.acquire() } catch { return }
        let owned = Array(sessions.values); sessions.removeAll()
        let prompts = Array(notifications.values); for task in prompts { task.cancel() }
        for task in prompts { await task.value }; notifications.removeAll()
        for session in owned { await session.retire() }
        await admission.release(permit)
    }
}
