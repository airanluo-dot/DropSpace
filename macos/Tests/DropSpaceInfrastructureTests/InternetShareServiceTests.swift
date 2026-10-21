import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private let shareTestEndpoint = URL(string: "https://sharing.example.invalid")!
private actor ShareTestKeys: DropLinkSecretStorage {
    var values: [String: Data] = [:]
    var denySave = false, denyRead = false, denyRemove = false
    func save(_ bytes: Data, account: String) throws {
        if denySave { throw DropSpaceError.storage("Injected save denial") }; values[account] = bytes
    }
    func read(_ account: String) throws -> Data? {
        if denyRead { throw DropSpaceError.storage("Injected read denial") }; return values[account]
    }
    func remove(_ account: String) throws {
        if denyRemove { throw DropSpaceError.storage("Injected removal denial") }; values[account] = nil
    }
    func fault(save: Bool = false, read: Bool = false, remove: Bool = false) { denySave = save; denyRead = read; denyRemove = remove }
    func count() -> Int { values.count }
}
private actor ShareTestBackend: ShareBackendClient {
    var objects: [String: Data] = [:], created: [UUID] = [], revoked: [UUID] = []
    var failUpload = false, failRevoke = false, suspendUpload = false
    var beforeCreate: (@Sendable () async throws -> Void)?
    func create(id: UUID, expires: Date, count: Int, bytes: Int64) async throws -> ShareBackendSession {
        try await beforeCreate?(); created.append(id)
        return .init(upload: shareTestEndpoint.appendingPathComponent("objects/\(id.dropLinkN)/"), download: shareTestEndpoint,
                     authorization: "Bearer owned-test-authorization", revoke: shareTestEndpoint.appendingPathComponent("v1/shares/\(id.dropLinkN)"))
    }
    func upload(session: ShareBackendSession, name: String, ciphertext: Data) async throws {
        objects[name] = ciphertext
        if suspendUpload { try await Task.sleep(for: .seconds(30)) }
        if failUpload { throw URLError(.networkConnectionLost) }
    }
    func revoke(session: ShareBackendSession, id: UUID) throws {
        if failRevoke { throw URLError(.notConnectedToInternet) }; revoked.append(id)
    }
    func faults(upload: Bool = false, revoke: Bool = false, suspend: Bool = false) { failUpload = upload; failRevoke = revoke; suspendUpload = suspend }
    func hook(_ operation: @escaping @Sendable () async throws -> Void) { beforeCreate = operation }
    func snapshot() -> ([UUID], [UUID], [String: Data]) { (created, revoked, objects) }
}
private func shareRoot() throws -> URL {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceInternetShareTests-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); return root
}
private func shareService(_ root: URL, _ backend: ShareTestBackend, _ keys: ShareTestKeys, _ budget: DownloadBudget = .init()) throws -> InternetShareService {
    try InternetShareService(directory: root, endpoint: shareTestEndpoint, budget: budget, keys: keys, backendFactory: { _, _ in backend })
}

@Test func internetShareStagedMultiChunkSourcePreservationAndRestartRevoke() async throws {
    let root = try shareRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let original = root.appendingPathComponent("original.bin"), empty = root.appendingPathComponent("empty.bin")
    let bytes = Data((0..<(ShareLimits.chunkPlainBytes + 17)).map { UInt8(truncatingIfNeeded: $0) })
    try bytes.write(to: original); try Data().write(to: empty)
    let sources = try await ShareSourcePreparation().prepare([.file(original), .file(empty)])
    let backend = ShareTestBackend(), keys = ShareTestKeys(), budget = DownloadBudget(), storeRoot = root.appendingPathComponent("service")
    // Mutate the original only after staging. Upload must use the saved, hashed
    // bytes; subsequent source content remains untouched by cleanup and revoke.
    await backend.hook { try Data("source changed after stage".utf8).write(to: original) }
    let service = try shareService(storeRoot, backend, keys, budget)
    let result = try await service.create(sources, enabled: true)
    let snapshot = await backend.snapshot(), key = try ShareCrypto.key(fragment: "#" + result.url.fragment!)
    let items = try ShareCrypto.decryptManifest(key: key, share: result.id, wire: #require(snapshot.2["manifest.bin"]))
    #expect(items.count == 2); #expect(items[0].chunkCount == 2); #expect(items[1].chunkCount == 0)
    var decoded = Data()
    for index in 0..<items[0].chunkCount {
        let count = min(ShareLimits.chunkPlainBytes, bytes.count - decoded.count)
        decoded += try ShareCrypto.decryptChunk(key: key, share: result.id, file: items[0].fileId, index: index, plainLength: count,
            wire: #require(snapshot.2[items[0].fileId.dropLinkN + ".\(index).bin"]), noncePrefix: items[0].noncePrefix)
    }
    #expect(decoded == bytes); #expect(snapshot.2.count == 3); #expect(snapshot.1.isEmpty)
    #expect(try Data(contentsOf: original) == Data("source changed after stage".utf8))
    #expect(try await service.records().first?.state == .completed)
    #expect(try await service.records().first?.stagingPending == false)
    #expect(try FileManager.default.contentsOfDirectory(atPath: storeRoot.appendingPathComponent("staging").path).isEmpty)
    #expect(await budget.transfers.activeCount == 0)
    await service.shutdown()
    let restart = try shareService(storeRoot, backend, keys); try await restart.initialize()
    #expect(await backend.snapshot().1.isEmpty); #expect(try await restart.records().first?.state == .completed)
    try await restart.revoke(result.id)
    #expect(try await restart.records().isEmpty); #expect(await keys.count() == 0)
    #expect(await backend.snapshot().1 == [result.id]); await restart.shutdown()
}

@Test func internetShareCredentialFailureNeverUploadsAndIncompleteRestartRetries() async throws {
    let root = try shareRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let sources = try await ShareSourcePreparation().prepare([.text("owned recovery bytes", name: "Recovery", isURL: false)])
    let keys = ShareTestKeys(), deniedBackend = ShareTestBackend(), denied = try shareService(root.appendingPathComponent("denied"), deniedBackend, keys)
    await keys.fault(save: true)
    await #expect(throws: (any Error).self) { try await denied.create(sources, enabled: true) }
    let deniedSnapshot = await deniedBackend.snapshot()
    #expect(deniedSnapshot.0.count == 1); #expect(deniedSnapshot.1 == deniedSnapshot.0); #expect(deniedSnapshot.2.isEmpty)
    #expect(try await denied.records().isEmpty); await denied.shutdown(); await keys.fault()
    let backend = ShareTestBackend(), directory = root.appendingPathComponent("recovery"), service = try shareService(directory, backend, keys)
    await backend.faults(upload: true, revoke: true)
    await #expect(throws: (any Error).self) { try await service.create(sources, enabled: true) }
    let pending = try #require(try await service.records().first)
    #expect(pending.state == .revokePending); #expect(!pending.stagingPending); #expect(pending.error != nil); #expect(pending.cleanupError == nil)
    #expect(await keys.count() == 1); await service.shutdown()
    await keys.fault(read: true)
    let restart = try shareService(directory, backend, keys); try await restart.initialize()
    #expect(try await restart.records().first?.state == .revokePending); #expect(await keys.count() == 1)
    await keys.fault(); await backend.faults(); try await restart.revoke(pending.id)
    #expect(try await restart.records().isEmpty); #expect(await keys.count() == 0); await restart.shutdown()
}

@Test func internetShareCancellationAndCommitFailureRevokeBeforeReportingSuccess() async throws {
    let root = try shareRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let sources = try await ShareSourcePreparation().prepare([.text("cancel bytes", name: "Cancel", isURL: false)])
    let keys = ShareTestKeys(), backend = ShareTestBackend(), budget = DownloadBudget(), service = try shareService(root.appendingPathComponent("cancel"), backend, keys, budget)
    await backend.faults(suspend: true)
    let operation = Task { try await service.create(sources, enabled: true) }
    let deadline = ContinuousClock.now.advanced(by: .seconds(3))
    while await backend.snapshot().2.isEmpty, .now < deadline { try await Task.sleep(for: .milliseconds(5)) }
    #expect(await backend.snapshot().2.count == 1); operation.cancel()
    await #expect(throws: CancellationError.self) { try await operation.value }
    #expect(try await service.records().isEmpty); #expect(await keys.count() == 0); #expect(await budget.transfers.activeCount == 0)
    #expect(await backend.snapshot().1.count == 1); await service.shutdown()
    let commitRoot = root.appendingPathComponent("commit"), commitBackend = ShareTestBackend(), commit = try shareService(commitRoot, commitBackend, keys)
    let db = try SQLiteConnection(url: commitRoot.appendingPathComponent("internet-shares.sqlite"))
    try db.execute("CREATE TRIGGER fail_complete BEFORE UPDATE ON shares WHEN json_extract(NEW.record,'$.state')='completed' BEGIN SELECT RAISE(ABORT,'owned commit validation'); END")
    await #expect(throws: (any Error).self) { try await commit.create(sources, enabled: true) }
    #expect(await commitBackend.snapshot().1.count == 1); #expect(try await commit.records().isEmpty); #expect(await keys.count() == 0)
    await commit.shutdown()
}

@Test func internetShareCustodyCleanupRefusesUnknownFilesAndRemovesPartialOwnedInit() async throws {
    let root = try shareRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let stagingRoot = root.appendingPathComponent("staging"), id = UUID()
    let staging = try InternetShareStaging(root: stagingRoot, id: id, count: 1)
    let sources = try await ShareSourcePreparation().prepare([.text("staged bytes", name: "Owned", isURL: false)])
    try await staging.stage(sources)
    let lease = stagingRoot.appendingPathComponent(id.dropLinkN), foreign = lease.appendingPathComponent("not-owned.txt")
    try Data("preserve".utf8).write(to: foreign)
    #expect(throws: (any Error).self) { try InternetShareStaging.cleanup(root: stagingRoot, id: id, count: 1) }
    #expect(try Data(contentsOf: foreign) == Data("preserve".utf8)); #expect(FileManager.default.fileExists(atPath: lease.appendingPathComponent("0000.payload").path))
    try FileManager.default.removeItem(at: foreign) // Exact validation-created file.
    try InternetShareStaging.cleanup(root: stagingRoot, id: id, count: 1)
    let partial = UUID(), partialRoot = stagingRoot.appendingPathComponent(partial.dropLinkN)
    try FileManager.default.createDirectory(at: partialRoot, withIntermediateDirectories: false)
    try Data("DropSpace Internet".utf8).write(to: partialRoot.appendingPathComponent("owner"))
    try InternetShareStaging.cleanup(root: stagingRoot, id: partial, count: 1)
    #expect(!FileManager.default.fileExists(atPath: partialRoot.path))
}
