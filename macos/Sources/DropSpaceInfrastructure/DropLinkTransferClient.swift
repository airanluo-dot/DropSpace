import Foundation
import DropSpaceCore

public enum DropLinkTransferStage: Sendable { case preparing, waitingForApproval, transferring, finalizing }
public struct DropLinkTransferProgress: Sendable {
    public let sessionID: UUID
    public let itemID: UUID
    public let transferredBytes: Int64
    public let totalBytes: Int64
    public let stage: DropLinkTransferStage
}
public struct DropLinkTransferClient: Sendable {
    public typealias Progress = @Sendable (DropLinkTransferProgress) async -> Void
    private let identity: DropLinkIdentity
    private let peers: DropLinkPeerStore
    public init(identity: DropLinkIdentity, peers: DropLinkPeerStore) { self.identity = identity; self.peers = peers }
    public func send(peer: DropLinkPeer, endpoint: URL, sources: [URL], limits: TransferLimits = .init(), progress: @escaping Progress = { _ in }) async throws -> TransferCompleteResponse {
        await progress(.init(sessionID: .zero, itemID: .zero, transferredBytes: 0, totalBytes: 0, stage: .preparing))
        let plan = try await TransferSourcePreparation().prepare(sources, limits: limits, filenames: peer.platform == .windows ? .windows : .macOS)
        return try await send(peer: peer, endpoint: endpoint, prepared: plan, progress: progress)
    }
    /// The caller may retain the prepared plan after a transient network error.
    /// Resume queries the receiver's ledger; every source is revalidated before
    /// any missing chunk is read. This does not imply restart persistence.
    public func send(peer: DropLinkPeer, endpoint: URL, prepared: PreparedTransfer, resume: Bool = false, progress: @escaping Progress = { _ in }) async throws -> TransferCompleteResponse {
        let manifest = prepared.manifest, session = manifest.sessionId
        try TransferManifestPolicy.validate(manifest, filenames: peer.platform == .windows ? .windows : .macOS)
        try Task.checkCancellation()
        do {
            if !resume {
                await progress(.init(sessionID: session, itemID: .zero, transferredBytes: 0, totalBytes: manifest.totalBytes, stage: .waitingForApproval))
                let offer: TransferOfferResponse = try await json(peer: peer, endpoint: endpoint, method: "POST", path: DropLinkRoute.transferOffers, body: DropLinkTransferJSON.encode(TransferOfferRequest(peer: identity.deviceID, manifest: manifest)))
                guard offer.sessionId == session else { await cancelBestEffort(peer: peer, endpoint: endpoint, session: session); throw DropSpaceError.invalid("Transfer offer response session") }
            }
            var status: TransferStatusResponse = try await status(peer: peer, endpoint: endpoint, session: session)
            let deadline = ContinuousClock.now.advanced(by: .seconds(300))
            while status.state == .awaitingApproval || status.state == .offered {
                guard ContinuousClock.now < deadline else { return .init(session: session, state: .failed, completed: [], error: "approval-timeout") }
                try await Task.sleep(for: .milliseconds(500)); status = try await self.status(peer: peer, endpoint: endpoint, session: session)
            }
            if resume, status.state == .verifying {
                let result: TransferCompleteResponse = try await json(peer: peer, endpoint: endpoint, method: "POST", path: DropLinkRoute.transfer(session, action: "complete"), body: Data("{}".utf8))
                guard result.sessionId == session else { throw DropSpaceError.invalid("Transfer completion response session") }; return result
            }
            guard [.accepted, .transferring].contains(status.state) else { return .init(session: session, state: status.state, completed: status.completedRelativePaths, error: status.errorCategory) }
            var transferred: Int64 = 0
            for item in manifest.items {
                guard let source = prepared.sources[item.id] else { throw DropSpaceError.invalid("Prepared transfer source missing") }
                let reader = try TransferSourceReader(source: source)
                do {
                    let received = Set(status.receivedChunks.first { UUID(uuidString: $0.key) == item.id }?.value ?? [])
                    guard received.allSatisfy({ $0 >= 0 && $0 < (item.chunkCount ?? 0) }) else { throw DropSpaceError.invalid("Peer returned an invalid chunk ledger") }
                    for index in 0..<(item.chunkCount ?? 0) {
                        try Task.checkCancellation()
                        let offset = Int64(index) * Int64(manifest.chunkBytes), count = Int(min(Int64(manifest.chunkBytes), item.size - offset))
                        if !received.contains(index) {
                            let data = try await reader.read(offset: offset, count: count)
                            let response = try await request(peer: peer, endpoint: endpoint, method: "PUT", path: DropLinkRoute.chunk(session, item: item.id, index: Int(index)), body: data, additional: ["x-droplink-chunk-sha256": Digests.sha256(data)])
                            guard response.status == 200 else { throw DropSpaceError.unavailable("Transfer chunk HTTP \(response.status)") }
                            guard try ModuleJSON.decode(response.body)["accepted"] == .boolean(true) else { throw DropSpaceError.invalid("Peer did not acknowledge the transfer chunk") }
                        }
                        transferred += Int64(count)
                        await progress(.init(sessionID: session, itemID: item.id, transferredBytes: transferred, totalBytes: manifest.totalBytes, stage: .transferring))
                    }
                    try await reader.validate(); await reader.close()
                } catch { await reader.close(); throw error }
            }
            try Task.checkCancellation()
            await progress(.init(sessionID: session, itemID: .zero, transferredBytes: manifest.totalBytes, totalBytes: manifest.totalBytes, stage: .finalizing))
            try Task.checkCancellation()
            let result: TransferCompleteResponse = try await json(peer: peer, endpoint: endpoint, method: "POST", path: DropLinkRoute.transfer(session, action: "complete"), body: Data("{}".utf8))
            guard result.sessionId == session else { throw DropSpaceError.invalid("Transfer completion response session") }; return result
        } catch {
            if Task.isCancelled || error is CancellationError { await cancelBestEffort(peer: peer, endpoint: endpoint, session: session); throw CancellationError() }
            throw error
        }
    }
    public func status(peer: DropLinkPeer, endpoint: URL, session: UUID) async throws -> TransferStatusResponse {
        let result: TransferStatusResponse = try await json(peer: peer, endpoint: endpoint, method: "GET", path: DropLinkRoute.transfer(session, action: "status"), body: Data("{}".utf8))
        guard result.sessionId == session else { throw DropSpaceError.invalid("Transfer status response session") }; return result
    }
    public func cancel(peer: DropLinkPeer, endpoint: URL, session: UUID) async throws -> TransferStatusResponse {
        let result: TransferStatusResponse = try await json(peer: peer, endpoint: endpoint, method: "POST", path: DropLinkRoute.transfer(session, action: "cancel"), body: Data("{}".utf8))
        guard result.sessionId == session else { throw DropSpaceError.invalid("Transfer cancellation response session") }; return result
    }
    private func cancelBestEffort(peer: DropLinkPeer, endpoint: URL, session: UUID) async {
        // An independent task deliberately does not inherit the cancelled
        // sender's cancellation flag. Its request still has a five-second cap.
        let cleanup = Task {
            try? await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask { _ = try await cancel(peer: peer, endpoint: endpoint, session: session) }
                group.addTask { try await Task.sleep(for: .seconds(5)); throw URLError(.timedOut) }
                defer { group.cancelAll() }; _ = try await group.next()
            }
        }
        await cleanup.value
    }
    private func json<T: Decodable>(peer: DropLinkPeer, endpoint: URL, method: String, path: String, body: Data) async throws -> T {
        let response = try await request(peer: peer, endpoint: endpoint, method: method, path: path, body: body)
        guard response.status == 200 else { throw DropSpaceError.unavailable("File transfer HTTP \(response.status)") }
        return try DropLinkTransferJSON.decode(T.self, from: response.body)
    }
    private func request(peer: DropLinkPeer, endpoint: URL, method: String, path: String, body: Data, additional: [String: String] = [:]) async throws -> DropLinkHTTPResponse {
        guard let secret = try await peers.secretForTrustedPeer(peer.id) else { throw DropSpaceError.unavailable("Device is no longer trusted") }
        let nonce = UUID().dropLinkN, hash = Digests.sha256(body)
        var headers = additional
        headers["x-droplink-device"] = identity.deviceID.dropLinkD; headers["x-droplink-nonce"] = nonce
        headers["x-droplink-body-sha256"] = hash; headers["x-droplink-auth"] = DropLinkCrypto.authentication(secret: secret, method: method, path: path, nonce: nonce, bodyHash: hash)
        return try await DropLinkHTTPClient(endpoint: endpoint, fingerprint: peer.identityFingerprint).send(method: method, path: path, headers: headers, body: body, maximumResponseBytes: 16 * 1024 * 1024)
    }
}

private actor TransferSourceReader {
    private let source: TransferSource
    private var descriptor: TransferDescriptor?
    init(source: TransferSource) throws {
        self.source = source; let descriptor = try TransferFileSystem.file(source.url); try source.validate(descriptor.info()); self.descriptor = descriptor
    }
    func validate() throws {
        guard let descriptor else { throw DropSpaceError.unavailable("Transfer source closed") }; try source.validate(descriptor.info())
        let current = try TransferFileSystem.file(source.url); try source.validate(current.info())
    }
    func read(offset: Int64, count: Int) throws -> Data {
        try validate(); let bytes = try TransferFileSystem.read(descriptor!, offset: offset, count: count); try validate(); return bytes
    }
    func close() { descriptor = nil }
}
