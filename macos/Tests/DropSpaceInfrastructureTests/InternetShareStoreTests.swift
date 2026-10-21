import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private actor ShareStoreGateKeys: DropLinkSecretStorage {
    private var values: [String: Data] = [:]
    private var continuation: CheckedContinuation<Void, Never>?
    var saving = false
    var denyDelete = false
    func save(_ data: Data, account: String) async {
        values[account] = data; saving = true
        await withCheckedContinuation { continuation = $0 }; saving = false
    }
    func release() { continuation?.resume(); continuation = nil }
    func read(_ account: String) -> Data? { values[account] }
    func remove(_ account: String) throws { if denyDelete { throw DropSpaceError.storage("Injected key removal denial") }; values[account] = nil }
    func deny(_ value: Bool) { denyDelete = value }
    var count: Int { values.count }
}
private func shareStoreRoot() throws -> URL {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceShareStore-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); return root
}
private let custodyEndpoint = URL(string: "https://custody.example.invalid")!
private let custodySession = ShareBackendSession(upload: custodyEndpoint.appendingPathComponent("objects/"), download: custodyEndpoint, authorization: "Bearer owned-recovery-credential", revoke: custodyEndpoint.appendingPathComponent("revoke"))

@Test func internetShareStoreSerializesSuspendedCredentialCommitAndRetainsDeniedCleanup() async throws {
    let root = try shareStoreRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let keys = ShareStoreGateKeys(), store = try InternetShareStore(directory: root, keys: keys)
    let reservation = try await store.reserve(endpoint: custodyEndpoint, expires: Date().addingTimeInterval(60), count: 1, bytes: 1, name: "Owned")
    _ = try await store.beginCreation(reservation.id, lifetime: 60)
    let attach = Task { try await store.attach(custodySession, id: reservation.id) }
    let deadline = ContinuousClock.now.advanced(by: .seconds(3))
    while !(await keys.saving), .now < deadline { try await Task.sleep(for: .milliseconds(5)) }
    #expect(await keys.saving)
    let revoke = Task { try await store.transition(reservation.id, to: .revokePending, error: "Requested while saving") }
    await keys.release(); try await attach.value; try await revoke.value
    #expect(try await store.record(reservation.id)?.state == .revokePending)
    try await store.stagingCleaned(reservation.id)
    #expect(try await store.record(reservation.id)?.error == "Requested while saving")
    await keys.deny(true)
    await #expect(throws: (any Error).self) { try await store.didRevoke(reservation.id) }
    #expect(try await store.record(reservation.id)?.state == .revoked)
    #expect(try await store.session(reservation.id) == nil); #expect(await keys.count == 1)
    let reopen = try InternetShareStore(directory: root, keys: keys)
    await keys.deny(false); try await reopen.recoverExpired()
    #expect(try await reopen.records().isEmpty); #expect(await keys.count == 0)
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_KEYCHAIN"] == "1"))
func internetShareRealKeychainRecoversSQLiteCommitFailureWithoutStoringCapabilityInDatabase() async throws {
    let root = try shareStoreRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let keys = KeychainStore(service: "io.github.airanluo-dot.DropSpace.share-validation." + UUID().uuidString)
    let store = try InternetShareStore(directory: root, keys: keys), db = try SQLiteConnection(url: root.appendingPathComponent("internet-shares.sqlite"))
    let reservation = try await store.reserve(endpoint: custodyEndpoint, expires: Date().addingTimeInterval(60), count: 1, bytes: 1, name: "Real Keychain validation")
    do {
        _ = try await store.beginCreation(reservation.id, lifetime: 60)
        try db.execute("CREATE TRIGGER reject_attach BEFORE UPDATE ON shares WHEN json_extract(NEW.record,'$.state')='uploading' BEGIN SELECT RAISE(ABORT,'owned attach validation'); END")
        await #expect(throws: (any Error).self) { try await store.attach(custodySession, id: reservation.id) }
        #expect(try await store.record(reservation.id)?.state == .creating)
        #expect(try await keys.read(reservation.account) != nil)
        let storedJSON = try #require(db.rows("SELECT record FROM shares").first?["record"])
        #expect(!storedJSON.contains("owned-recovery-credential")); #expect(!storedJSON.contains("uploadAuthorization")); #expect(!storedJSON.contains("#k="))
        try db.execute("DROP TRIGGER reject_attach")
        let reopened = try InternetShareStore(directory: root, keys: keys)
        #expect(try await reopened.session(reservation.id)?.uploadAuthorization == custodySession.uploadAuthorization)
        try await reopened.stagingCleaned(reservation.id); try await reopened.didRevoke(reservation.id)
        #expect(try await reopened.records().isEmpty); #expect(try await keys.read(reservation.account) == nil)
    } catch { try? await keys.remove(reservation.account); throw error }
}
