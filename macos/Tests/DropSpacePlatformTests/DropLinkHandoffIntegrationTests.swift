import AppKit
import CryptoKit
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure
import DropSpacePlatform

private actor HandoffTestSecrets: DropLinkSecretStorage {
    private var values: [String: Data] = [:]
    func read(_ account: String) -> Data? { values[account] }
    func save(_ data: Data, account: String) { values[account] = data }
    func remove(_ account: String) { values[account] = nil }
}
private actor HandoffCommitGate {
    private var continuation: CheckedContinuation<Void, Never>?
    func wait() async { await withCheckedContinuation { continuation = $0 } }
    var waiting: Bool { continuation != nil }
    func release() { continuation?.resume(); continuation = nil }
}

@Test @MainActor func dropLinkHandoffTLSCommitsSpaceOnlyAndReportsDatabaseFailure() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceHandoff-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root), keys = HandoffTestSecrets()
    let peers = try DropLinkPeerStore(directory: root.appendingPathComponent("Peers"), keychain: keys)
    let sender = try DropLinkIdentity.create(name: "Sender"), receiver = try DropLinkIdentity.create(name: "Receiver")
    let shared = SymmetricKey(size: .bits256)
    let hello = try DropLinkCrypto.makeHello(deviceID: receiver.deviceID, name: receiver.displayName, fingerprint: receiver.fingerprint, capabilities: [.handoffText, .handoffURL]).1
    try await peers.trust(hello, secret: shared)
    let auth = DropLinkAuthenticator { $0 == sender.deviceID ? shared : nil }
    let host = DropLinkHandoffHost { message in
        let item = try FileIntake.handoff(message, settings: AppSettings())
        try await store.insert([item]); return true
    }
    let server = DropLinkHTTPServer(identity: receiver, admit: { try await auth.admit($0, address: $1) }, handler: { try await host.handle($0) })
    let clipboardChanges = NSPasteboard.general.changeCount
    do {
        let endpoint = try await server.start(address: "127.0.0.1")
        let peer = try #require(try await peers.peers().first)
        let client = DropLinkHandoffClient(identity: sender, peers: peers)
        let accepted = try await client.send(peer: peer, endpoint: endpoint, kind: .url, payload: "https://example.test/a#encryption-key", label: "URL fixture")
        #expect(accepted.accepted)
        let items = try await store.items()
        let item = try #require(items.first)
        #expect(items.count == 1 && item.source == .space && item.kind == .url)
        #expect(item.text == "https://example.test/a#encryption-key")
        #expect(item.batchAcquisition == "device-handoff" && item.batchCount == 1 && item.batchIndex == 0 && item.batchID != nil)
        #expect(NSPasteboard.general.changeCount == clipboardChanges)
        let reopened = try ItemStore(directory: root)
        #expect(try await reopened.items().first?.id == item.id)
        let fault = try SQLiteConnection(url: root.appendingPathComponent("workspace.sqlite"))
        try fault.execute("CREATE TRIGGER fail_handoff BEFORE INSERT ON items BEGIN SELECT RAISE(ABORT,'Injected full disk'); END")
        let failed = try await client.send(peer: peer, endpoint: endpoint, kind: .text, payload: "must not report committed")
        #expect(!failed.accepted && failed.errorCategory == "handoff-invalid")
        #expect(try await store.items().count == 1)
        try fault.execute("DROP TRIGGER fail_handoff")
        let recovered = try await client.send(peer: peer, endpoint: endpoint, kind: .text, payload: "new explicit retry\r\naccepted")
        #expect(recovered.accepted)
        #expect(try await store.items().count == 2)
        #expect(NSPasteboard.general.changeCount == clipboardChanges)
        await server.stop()
    } catch { await server.stop(); throw error }
}

@Test func dropLinkHandoffRespectsLocalTextLimitAndCancelledQueuedCommit() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceHandoffCancel-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let message = try HandoffMessagePolicy.create(sender: UUID(), name: "Sender", kind: .text, payload: String(repeating: "x", count: 1025))
    var settings = AppSettings(); settings.maxTextCharacters = 1024
    #expect(throws: (any Error).self) { try FileIntake.handoff(message, settings: settings) }
    let store = try ItemStore(directory: root), item = try FileIntake.handoff(message, settings: AppSettings())
    let gate = HandoffCommitGate()
    let cancelled = Task { await gate.wait(); try await store.insert([item]) }
    while !(await gate.waiting) { await Task.yield() }
    cancelled.cancel(); await gate.release()
    await #expect(throws: CancellationError.self) { try await cancelled.value }
    #expect(try await store.items().isEmpty)
}
