import Foundation
import CryptoKit
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private actor TransferTestSecrets: DropLinkSecretStorage {
    var values: [String: Data] = [:]
    func read(_ account: String) -> Data? { values[account] }
    func save(_ data: Data, account: String) { values[account] = data }
    func remove(_ account: String) { values[account] = nil }
}
private actor TransferApprovalGate {
    var allowed = false
    var waiting = 0
    func decide() async throws -> Bool {
        waiting += 1; defer { waiting -= 1 }
        while !allowed { try await Task.sleep(for: .milliseconds(5)) }; return true
    }
    func allow() { allowed = true }
}
private actor TransferChunkInterruption {
    var counts: [String: Int] = [:]
    var interrupted = false
    func shouldInterrupt(_ path: String) -> Bool {
        guard path.contains("/chunks/") else { return false }
        counts[path, default: 0] += 1
        if !interrupted, path.hasSuffix("/1") { interrupted = true; return true }; return false
    }
}
private func transferHeaders(sender: UUID, key: SymmetricKey, method: String, path: String, body: Data) -> [String: String] {
    let nonce = UUID().dropLinkN, hash = Digests.sha256(body)
    return ["x-droplink-device": sender.dropLinkD, "x-droplink-nonce": nonce, "x-droplink-body-sha256": hash, "x-droplink-auth": DropLinkCrypto.authentication(secret: key, method: method, path: path, nonce: nonce, bodyHash: hash)]
}

@Test func transferNativeTLSApprovalResumeCompletionAndHistory() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-Transfer-TLS-" + UUID().dropLinkN)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
    let sources = root.appendingPathComponent("sources"), destination = root.appendingPathComponent("received"), hostDirectory = root.appendingPathComponent("host")
    try FileManager.default.createDirectory(at: sources, withIntermediateDirectories: true)
    let source = sources.appendingPathComponent("bytes.bin"), empty = sources.appendingPathComponent("empty.bin"), bytes = Data(repeating: 137, count: 131071)
    try bytes.write(to: source); try Data().write(to: empty)
    var limits = TransferLimits(); limits.chunkBytes = 65536
    let prepared = try await TransferSourcePreparation().prepare([source, empty], limits: limits)
    let sender = try DropLinkIdentity.create(name: "Transfer Sender"), receiver = try DropLinkIdentity.create(name: "Transfer Receiver"), secret = SymmetricKey(size: .bits256)
    let secrets = TransferTestSecrets(), peers = try DropLinkPeerStore(directory: root.appendingPathComponent("peers"), keychain: secrets)
    let hello = try DropLinkCrypto.makeHello(deviceID: receiver.deviceID, name: receiver.displayName, fingerprint: receiver.fingerprint, capabilities: [.handoffFiles, .handoffFolders]).1
    try await peers.trust(hello, secret: secret); let peer = try #require(await peers.peers().first)
    let gate = TransferApprovalGate(), interruption = TransferChunkInterruption()
    let host = try DropLinkTransferHost(directory: hostDirectory, destination: destination) { _ in try await gate.decide() }; try await host.start()
    let auth = DropLinkAuthenticator { id in id == sender.deviceID ? secret : nil }
    let server = DropLinkHTTPServer(identity: receiver, admit: { try await auth.admit($0, address: $1) }, handler: { request in
        if await interruption.shouldInterrupt(request.head.path) { return .init(status: 503) }; return try await host.handle(request)
    })
    let client = DropLinkTransferClient(identity: sender, peers: peers)
    do {
        let endpoint = try await server.start(address: "127.0.0.1"), raw = try DropLinkHTTPClient(endpoint: endpoint, fingerprint: receiver.fingerprint)
        let send = Task { try await client.send(peer: peer, endpoint: endpoint, prepared: prepared) }
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        while await gate.waiting == 0, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(5)) }
        #expect(await gate.waiting == 1)
        #expect(!FileManager.default.fileExists(atPath: destination.path))
        let acceptPath = try DropLinkRoute.transfer(prepared.manifest.sessionId, action: "accept"), acceptBody = try DropLinkTransferJSON.encode(TransferAcceptRequest(true))
        let forbidden = try await raw.send(method: "POST", path: acceptPath, headers: transferHeaders(sender: sender.deviceID, key: secret, method: "POST", path: acceptPath, body: acceptBody), body: acceptBody)
        #expect(forbidden.status == 403)
        await gate.allow()
        await #expect(throws: (any Error).self) { try await send.value }
        let interrupted = try await client.status(peer: peer, endpoint: endpoint, session: prepared.manifest.sessionId)
        #expect(interrupted.state == .transferring && interrupted.transferredBytes == 65536)
        #expect(!FileManager.default.fileExists(atPath: destination.appendingPathComponent("bytes.bin").path))
        let resumed = try await client.send(peer: peer, endpoint: endpoint, prepared: prepared, resume: true)
        #expect(resumed.state == .completed && Set(resumed.completedRelativePaths) == ["bytes.bin", "empty.bin"])
        #expect(try Data(contentsOf: destination.appendingPathComponent("bytes.bin")) == bytes)
        #expect(try Data(contentsOf: destination.appendingPathComponent("empty.bin")).isEmpty)
        let file = try #require(prepared.manifest.items.first { $0.size > 0 })
        #expect(await interruption.counts[DropLinkRoute.chunk(prepared.manifest.sessionId, item: file.id, index: 0)] == 1)
        #expect(await interruption.counts[DropLinkRoute.chunk(prepared.manifest.sessionId, item: file.id, index: 1)] == 2)
        let completePath = try DropLinkRoute.transfer(prepared.manifest.sessionId, action: "complete"), body = Data("{}".utf8)
        async let one = raw.send(method: "POST", path: completePath, headers: transferHeaders(sender: sender.deviceID, key: secret, method: "POST", path: completePath, body: body), body: body)
        async let two = raw.send(method: "POST", path: completePath, headers: transferHeaders(sender: sender.deviceID, key: secret, method: "POST", path: completePath, body: body), body: body)
        for response in try await [one, two] { #expect(try DropLinkTransferJSON.decode(TransferCompleteResponse.self, from: response.body).state == .completed) }
        await server.stop(); await host.stop()
        let reopened = try DropLinkTransferRepository(directory: hostDirectory), history = try await reopened.history()
        #expect(history.count == 1 && history[0].state == .completed && !history[0].pendingCleanup)
        #expect(try FileManager.default.contentsOfDirectory(atPath: hostDirectory.appendingPathComponent("transfers").path).isEmpty)
        #expect(try Data(contentsOf: source) == bytes)
    } catch { await server.stop(); await host.stop(); throw error }
}

@Test func transferNativeCancellationRetiresApprovalAndKeepsSources() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-Transfer-Cancel-" + UUID().dropLinkN)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
    let source = root.appendingPathComponent("source.txt"); try Data("source".utf8).write(to: source)
    let prepared = try await TransferSourcePreparation().prepare([source])
    let sender = try DropLinkIdentity.create(name: "Cancel Sender"), receiver = try DropLinkIdentity.create(name: "Cancel Receiver"), secret = SymmetricKey(size: .bits256)
    let peers = try DropLinkPeerStore(directory: root.appendingPathComponent("peers"), keychain: TransferTestSecrets())
    try await peers.trust(DropLinkCrypto.makeHello(deviceID: receiver.deviceID, name: receiver.displayName, fingerprint: receiver.fingerprint, capabilities: [.handoffFiles]).1, secret: secret)
    let peer = try #require(await peers.peers().first), gate = TransferApprovalGate()
    let host = try DropLinkTransferHost(directory: root.appendingPathComponent("host"), destination: root.appendingPathComponent("received")) { _ in try await gate.decide() }
    try await host.start(); let auth = DropLinkAuthenticator { id in id == sender.deviceID ? secret : nil }
    let server = DropLinkHTTPServer(identity: receiver, admit: { try await auth.admit($0, address: $1) }, handler: { try await host.handle($0) })
    do {
        let endpoint = try await server.start(address: "127.0.0.1"), client = DropLinkTransferClient(identity: sender, peers: peers)
        let send = Task { try await client.send(peer: peer, endpoint: endpoint, prepared: prepared) }
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        while await gate.waiting == 0, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(5)) }
        #expect(await gate.waiting == 1); send.cancel()
        await #expect(throws: CancellationError.self) { try await send.value }
        #expect(try await client.status(peer: peer, endpoint: endpoint, session: prepared.manifest.sessionId).state == .cancelled)
        #expect(await gate.waiting == 0)
        await server.stop()
        async let firstStop: Void = host.stop()
        async let secondStop: Void = host.stop()
        await firstStop; await secondStop
        try await host.start()
        let oldSessionRequest = DropLinkHTTPRequest(head: .init(method: "GET", path: try DropLinkRoute.transfer(prepared.manifest.sessionId, action: "status"), headers: [:]), body: Data(), peerID: sender.deviceID, address: "127.0.0.1")
        #expect(try await host.handle(oldSessionRequest).status == 404)
        await host.stop()
        #expect(try Data(contentsOf: source) == Data("source".utf8))
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("received").path))
        #expect(try await host.history().first?.pendingCleanup == false)
    } catch { await server.stop(); await host.stop(); throw error }
}

@Test func transferFailedLedgerRetryPartialPublicationAndCleanupRecovery() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-Transfer-Recovery-" + UUID().dropLinkN)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
    let hostDirectory = root.appendingPathComponent("host"), staging = hostDirectory.appendingPathComponent("transfers"), destination = root.appendingPathComponent("received")
    try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
    try Data("user-owned".utf8).write(to: destination.appendingPathComponent("collision"))
    let bytes = Data("first-payload".utf8), first = TransferItemManifest(name: "first", path: "first", size: Int64(bytes.count), hash: Digests.sha256(bytes), mime: "text/plain", chunks: 1)
    let second = TransferItemManifest(name: "collision", path: "collision", size: 0, hash: Digests.sha256(Data()), mime: "text/plain", chunks: 0)
    let manifest = try TransferManifestPolicy.create(items: [first, second]), repository = try DropLinkTransferRepository(directory: hostDirectory)
    let record = DropLinkTransferRecord(peer: UUID(), manifest: manifest, destination: destination, now: Date())
    try await repository.create(record)
    let files = try TransferReceiveFiles(parentURL: staging, manifest: manifest, destination: destination)
    let session = DropLinkReceiveSession(record: record, repository: repository, files: files)
    #expect(try await session.approve(true))
    let db = try SQLiteConnection(url: hostDirectory.appendingPathComponent("transfers.sqlite"))
    try db.execute("CREATE TRIGGER reject_transfer_chunk BEFORE INSERT ON transfer_chunks BEGIN SELECT RAISE(FAIL,'injected chunk ledger failure'); END")
    await #expect(throws: (any Error).self) { try await session.chunk(item: first.id, index: 0, body: bytes, hash: Digests.sha256(bytes)) }
    #expect(try await session.snapshot().transferredBytes == 0)
    #expect(try await repository.history().first?.chunks.isEmpty == true)
    try db.execute("DROP TRIGGER reject_transfer_chunk")
    #expect(try await session.chunk(item: first.id, index: 0, body: bytes, hash: Digests.sha256(bytes)).status == 200)
    #expect(try await session.snapshot().transferredBytes == bytes.count)
    // v1 acknowledges an existing ledger entry/part even without a chunk hash;
    // HMAC/body authentication is independently enforced by the HTTP host.
    let duplicate = try await session.chunk(item: first.id, index: 0, body: Data(repeating: 0, count: bytes.count), hash: "")
    #expect(duplicate.status == 200)
    #expect(try ModuleJSON.decode(duplicate.body)["duplicate"] == .boolean(true))
    #expect(try await session.snapshot().transferredBytes == bytes.count)
    let missing = try await session.chunk(item: UUID(), index: 0, body: bytes, hash: "")
    #expect(missing.status == 400)
    #expect(try ModuleJSON.decode(missing.body)["error"] == .string("chunk-invalid"))
    let short = try await session.chunk(item: first.id, index: 0, body: Data(), hash: "")
    #expect(short.status == 400)
    // A lost recorded part is rewritten, without double-counting its ledger.
    let part = staging.appendingPathComponent(manifest.sessionId.dropLinkN).appendingPathComponent(first.id.dropLinkN + "-0.part")
    try FileManager.default.removeItem(at: part)
    #expect(try await session.chunk(item: first.id, index: 0, body: bytes, hash: Digests.sha256(bytes)).status == 200)
    #expect(try await session.snapshot().transferredBytes == bytes.count)
    let failed = try await session.complete()
    #expect(failed.state == .failed && failed.completedRelativePaths == ["first"])
    #expect(try Data(contentsOf: destination.appendingPathComponent("first")) == bytes)
    #expect(try Data(contentsOf: destination.appendingPathComponent("collision")) == Data("user-owned".utf8))
    let unknown = staging.appendingPathComponent(manifest.sessionId.dropLinkN).appendingPathComponent("foreign-file")
    try Data("retain".utf8).write(to: unknown); await session.retire()
    #expect(try await repository.history().first?.pendingCleanup == true)
    let host = try DropLinkTransferHost(directory: hostDirectory, destination: destination) { _ in false }
    try await host.start()
    #expect(try await host.history().first?.pendingCleanup == true)
    #expect(try Data(contentsOf: unknown) == Data("retain".utf8))
    try FileManager.default.removeItem(at: unknown); try await host.retryCleanup()
    #expect(try await host.history().first?.pendingCleanup == false)
    #expect(try await host.history().first?.completedPaths == ["first"])
    #expect(try Data(contentsOf: destination.appendingPathComponent("first")) == bytes)
    #expect(try Data(contentsOf: destination.appendingPathComponent("collision")) == Data("user-owned".utf8))
    await host.stop()
}
