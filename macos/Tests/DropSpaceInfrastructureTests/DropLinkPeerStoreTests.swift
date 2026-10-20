import Foundation
import CryptoKit
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private actor FaultablePeerSecrets: DropLinkSecretStorage {
    var values: [String: Data] = [:]
    var suspendAfterSave = false
    var denyRemoval = false
    var denyRead = false
    func read(_ account: String) throws -> Data? {
        if denyRead { throw DropSpaceError.storage("Injected Keychain read denial") }; return values[account]
    }
    func save(_ data: Data, account: String) async throws {
        values[account] = data
        if suspendAfterSave { try await Task.sleep(for: .seconds(30)) }
    }
    func remove(_ account: String) throws {
        if denyRemoval { throw DropSpaceError.storage("Injected Keychain delete denial") }; values[account] = nil
    }
    func faults(save: Bool = false, remove: Bool = false, read: Bool = false) { suspendAfterSave = save; denyRemoval = remove; denyRead = read }
    func count() -> Int { values.count }
}
private func peerHello() throws -> PairingHello {
    try DropLinkCrypto.makeHello(deviceID: UUID(), name: "Owned peer validation", fingerprint: String(repeating: "a", count: 64), capabilities: [.handoffText]).1
}
private func peerDirectory() throws -> URL {
    let result = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpacePeerTest-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: result, withIntermediateDirectories: true); return result
}
private func secretBytes(_ key: SymmetricKey?) -> Data? { key?.withUnsafeBytes { Data($0) } }

@Test func dropLinkPeerCancellationRevokesIntentAndUnpairDenialRemainsNonAuthorizing() async throws {
    let root = try peerDirectory(); defer { try? FileManager.default.removeItem(at: root) }
    let keys = FaultablePeerSecrets(), store = try DropLinkPeerStore(directory: root, keychain: keys), hello = try peerHello()
    let old = SymmetricKey(size: .bits256); try await store.trust(hello, secret: old)
    await keys.faults(save: true)
    let pairing = Task { try await store.trust(hello, secret: SymmetricKey(size: .bits256)) }
    let deadline = ContinuousClock.now.advanced(by: .seconds(3))
    while await keys.count() < 2, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(5)) }
    #expect(await keys.count() == 2); pairing.cancel()
    await #expect(throws: CancellationError.self) { try await pairing.value }
    #expect(secretBytes(try await store.secretForTrustedPeer(hello.deviceId)) == secretBytes(old))
    #expect(await keys.count() == 1)
    let reopened = try DropLinkPeerStore(directory: root, keychain: keys)
    try await reopened.recover()
    #expect(secretBytes(try await reopened.secretForTrustedPeer(hello.deviceId)) == secretBytes(old))
    await keys.faults(remove: true)
    try await reopened.remove(hello.deviceId)
    #expect(try await reopened.peers().first?.trustState == .unpairPending)
    #expect(try await reopened.secretForTrustedPeer(hello.deviceId) == nil)
    #expect(try await reopened.pendingCleanupCount() == 1)
    await keys.faults(); try await reopened.recover()
    #expect(try await reopened.peers().isEmpty)
    #expect(await keys.count() == 0)
}

@Test func dropLinkPeerBlockedAndUnreadableSecretsNeverAuthorizeOrSilentlyDiscardTrust() async throws {
    let root = try peerDirectory(); defer { try? FileManager.default.removeItem(at: root) }
    let keys = FaultablePeerSecrets(), store = try DropLinkPeerStore(directory: root, keychain: keys), hello = try peerHello()
    try await store.trust(hello, secret: SymmetricKey(size: .bits256))
    await keys.faults(read: true); try await store.recover()
    #expect(try await store.peers().first?.trustState == .trusted)
    #expect(await store.recoveryError != nil)
    await #expect(throws: (any Error).self) { try await store.secretForTrustedPeer(hello.deviceId) }
    await keys.faults(); try await store.remove(hello.deviceId, blocked: true)
    #expect(try await store.peers().first?.trustState == .blocked)
    #expect(try await store.secretForTrustedPeer(hello.deviceId) == nil)
    await #expect(throws: (any Error).self) { try await store.trust(hello, secret: SymmetricKey(size: .bits256)) }
    #expect(await keys.count() == 0)
    try await store.remove(hello.deviceId)
    #expect(try await store.peers().isEmpty)
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_KEYCHAIN"] == "1"))
func dropLinkPeerRealKeychainAndSQLiteCommitFailureRecoverWithoutReplacingWorkingPair() async throws {
    let root = try peerDirectory(); defer { try? FileManager.default.removeItem(at: root) }
    let keys = KeychainStore(service: "io.github.airanluo-dot.DropSpace.validation." + UUID().uuidString)
    let store = try DropLinkPeerStore(directory: root, keychain: keys), db = try SQLiteConnection(url: root.appendingPathComponent("peers.sqlite"))
    let hello = try peerHello(), old = SymmetricKey(size: .bits256), new = SymmetricKey(size: .bits256)
    func accounts() throws -> Set<String> {
        Set(try db.rows("SELECT secret AS account FROM peers UNION SELECT secret AS account FROM pairing_intents UNION SELECT previous_secret AS account FROM pairing_intents UNION SELECT account FROM peer_key_cleanup").compactMap { $0["account"] })
    }
    var owned = Set<String>()
    do {
        try await store.trust(hello, secret: old); owned.formUnion(try accounts())
        let created = try #require(try await store.peers().first?.createdAtUtc)
        // A real SQLite commit interruption after the new bytes entered real Keychain.
        try db.execute("CREATE TRIGGER reject_trust BEFORE UPDATE OF record ON peers WHEN json_extract(NEW.record,'$.trustState')=2 BEGIN SELECT RAISE(ABORT,'owned validation failure'); END")
        await #expect(throws: (any Error).self) { try await store.trust(hello, secret: new) }
        owned.formUnion(try accounts())
        #expect(try db.rows("SELECT id FROM pairing_intents").count == 1)
        #expect(secretBytes(try await store.secretForTrustedPeer(hello.deviceId)) == secretBytes(old))
        try db.execute("DROP TRIGGER reject_trust")
        let reopened = try DropLinkPeerStore(directory: root, keychain: keys)
        try await reopened.recover()
        #expect(secretBytes(try await reopened.secretForTrustedPeer(hello.deviceId)) == secretBytes(new))
        #expect(try await reopened.peers().first?.createdAtUtc == created)
        #expect(try db.rows("SELECT id FROM pairing_intents").isEmpty)
        #expect(try await reopened.pendingCleanupCount() == 0)
        let active = try #require(try db.rows("SELECT secret FROM peers").first?["secret"])
        for account in owned where account != active { #expect(try await keys.read(account) == nil) }
        // Damaged protected bytes revoke authorization; the next reconciliation cleans
        // both the unusable account and pending metadata, rather than authorizing it.
        try await keys.save(Data([1, 2, 3]), account: active)
        try await reopened.recover()
        #expect(try await reopened.peers().first?.trustState == .pairingPending)
        #expect(try await reopened.secretForTrustedPeer(hello.deviceId) == nil)
        try await reopened.recover()
        #expect(try await reopened.peers().isEmpty)
        for account in owned { #expect(try await keys.read(account) == nil) }
    } catch {
        owned.formUnion((try? accounts()) ?? [])
        for account in owned { try? await keys.remove(account) }
        throw error
    }
}
