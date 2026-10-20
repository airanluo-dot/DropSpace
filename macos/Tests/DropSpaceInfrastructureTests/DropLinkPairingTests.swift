import Foundation
import CryptoKit
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private actor PairingApprovalProbe {
    var codes: [Int] = []
    var waiting = false
    func approve(_ offer: PairingOffer, accepted: Bool = true, wait: Bool = false) async throws -> Bool {
        codes.append(offer.sas)
        if wait {
            waiting = true; defer { waiting = false }
            try await Task.sleep(for: .seconds(30))
        }
        return accepted
    }
    func received() -> [Int] { codes }
    func isWaiting() -> Bool { waiting }
}
private actor PairingCommitProbe {
    var peers: Set<UUID> = []
    func commit(_ hello: PairingHello) { peers.insert(hello.deviceId) }
    func count() -> Int { peers.count }
}
private func remoteHello() throws -> PairingHello {
    try DropLinkCrypto.makeHello(deviceID: UUID(), name: "Pairing boundary", fingerprint: String(repeating: "b", count: 64), capabilities: [.handoffText]).1
}

@Test func dropLinkPairingWindowsJSONDefaultsCaseDuplicateAndInt32Bounds() throws {
    let decision = try DropLinkPairingJSON.decode(PairingConfirmationRequest.self, from: Data("{\"SAS\":1,\"sas\":\"246810\",\"Confirmed\":true}".utf8))
    #expect(decision.sessionId == .zero && decision.sas == 246810 && decision.confirmed && decision.decision == 1)
    #expect(throws: (any Error).self) { try DropLinkPairingJSON.decode(PairingConfirmationRequest.self, from: Data("{\"sas\":2147483648}".utf8)) }
    #expect(throws: (any Error).self) { try DropLinkPairingJSON.decode(PairingConfirmationRequest.self, from: Data("{\"confirmed\":\"true\"}".utf8)) }
    let hello = try remoteHello()
    var object = try #require(JSONSerialization.jsonObject(with: DropLinkPairingJSON.encode(PairingOffer(sessionId: UUID(), remoteHello: hello, localHello: hello, sas: 123456, expiresAtUtc: Date()))) as? [String: Any])
    object.removeValue(forKey: "state")
    let offer = try DropLinkPairingJSON.decode(PairingOffer.self, from: JSONSerialization.data(withJSONObject: object))
    #expect(offer.state == .awaitingLocalSasConfirmation)
    #expect(DropLinkEncoding.base64("Y Q==\r\n\t") == Data([97]))
    #expect(DropLinkEncoding.base64("Y\u{00a0}Q==") == nil)
}

@Test func dropLinkPairingAdmissionExpiryAndSingleDecisionMatchBaseline() async throws {
    let identity = try DropLinkIdentity.create(name: "Admission test"), commits = PairingCommitProbe()
    let service = DropLinkPairingService(identity: identity) { hello, _ in await commits.commit(hello) }
    let now = Date(), first = try remoteHello()
    let offer = try await service.accept(first, capabilities: [], address: "one", now: now)
    do { _ = try await service.accept(first, capabilities: [], address: "two", now: now); Issue.record("Duplicate admitted") }
    catch let failure as PairingAdmissionFailure { #expect(failure.category == "pairing-duplicate") }
    for _ in 0..<3 { _ = try await service.accept(remoteHello(), capabilities: [], address: "one", now: now) }
    do { _ = try await service.accept(remoteHello(), capabilities: [], address: "one", now: now); Issue.record("Address capacity ignored") }
    catch let failure as PairingAdmissionFailure { #expect(failure.category == "pairing-address-capacity") }
    for address in ["two", "three", "four"] { for _ in 0..<4 { _ = try await service.accept(remoteHello(), capabilities: [], address: address, now: now) } }
    do { _ = try await service.accept(remoteHello(), capabilities: [], address: "five", now: now); Issue.record("Global capacity ignored") }
    catch let failure as PairingAdmissionFailure { #expect(failure.category == "pairing-capacity") }
    let expired = try await service.confirm(.init(sessionId: offer.sessionId, sas: offer.sas, confirmed: true), now: now.addingTimeInterval(300))
    #expect(expired.state == .expired && !expired.trusted)
    await #expect(throws: (any Error).self) { try await service.confirm(.init(sessionId: offer.sessionId, sas: offer.sas, confirmed: true)) }
    let pending = try await service.accept(remoteHello(), capabilities: [], address: "fresh", now: now.addingTimeInterval(301))
    let rejected = try await service.confirm(.init(sessionId: pending.sessionId, sas: (pending.sas + 1) % 1_000_000, confirmed: true), now: now.addingTimeInterval(302))
    #expect(rejected.state == .rejected && !rejected.trusted)
    #expect(await commits.count() == 0)
    var invalid = try remoteHello(); invalid.deviceId = .zero
    for _ in 0..<20 { await #expect(throws: (any Error).self) { try await service.accept(invalid, capabilities: [], address: "limited", now: now) } }
    do { _ = try await service.accept(remoteHello(), capabilities: [], address: "limited", now: now); Issue.record("Rate ignored") }
    catch let failure as PairingAdmissionFailure { #expect(failure.category == "pairing-rate-limited") }
    await service.shutdown()
    #expect(await service.pendingCount() == 0)
    await #expect(throws: (any Error).self) { try await service.accept(first, capabilities: [], address: "one") }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_KEYCHAIN"] == "1"))
func dropLinkBilateralPairingOverTLSCommitsSameRealKeychainSecretAndReopens() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceBilateral-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let a = try DropLinkIdentity.create(name: "Pairing Mac A"), b = try DropLinkIdentity.create(name: "Pairing Mac B")
    let keysA = KeychainStore(service: "io.github.airanluo-dot.DropSpace.validation." + UUID().uuidString)
    let keysB = KeychainStore(service: "io.github.airanluo-dot.DropSpace.validation." + UUID().uuidString)
    let peersA = try DropLinkPeerStore(directory: root.appendingPathComponent("a"), keychain: keysA)
    let peersB = try DropLinkPeerStore(directory: root.appendingPathComponent("b"), keychain: keysB)
    let local = PairingApprovalProbe(), remote = PairingApprovalProbe()
    let host = DropLinkPairingHost(identity: b, capabilities: [], peers: peersB) { try await remote.approve($0) }
    let auth = DropLinkAuthenticator { try await peersB.secretForTrustedPeer($0) }
    let server = DropLinkHTTPServer(identity: b, admit: { try await auth.admit($0, address: $1) }, handler: { try await host.handle($0) })
    do {
        let endpoint = try await server.start(address: "127.0.0.1"); await host.setEndpoint(endpoint)
        let client = DropLinkPairingClient(identity: a, peers: peersA)
        let device = try await client.device(endpoint: endpoint, fingerprint: b.fingerprint)
        #expect(device.deviceId == b.deviceID && device.platform == .macOS)
        let peer = try await client.pair(endpoint: endpoint, fingerprint: b.fingerprint, expectedDevice: b.deviceID, capabilities: []) { try await local.approve($0) }
        #expect(peer.id == b.deviceID && peer.trustState == .trusted)
        let localCodes = await local.received(), remoteCodes = await remote.received()
        #expect(localCodes.count == 1 && localCodes == remoteCodes)
        let secretA = try #require(try await peersA.secretForTrustedPeer(b.deviceID)), secretB = try #require(try await peersB.secretForTrustedPeer(a.deviceID))
        #expect(secretA.withUnsafeBytes { Data($0) } == secretB.withUnsafeBytes { Data($0) })
        let reopened = try DropLinkPeerStore(directory: root.appendingPathComponent("a"), keychain: keysA)
        try await reopened.recover()
        #expect(try await reopened.peers().first?.trustState == .trusted)
        try await peersA.remove(b.deviceID); try await peersB.remove(a.deviceID)
        #expect(try await peersA.peers().isEmpty)
        #expect(try await peersB.peers().isEmpty)
        await server.stop(); await host.shutdown()
    } catch {
        await server.stop(); await host.shutdown()
        try? await peersA.remove(b.deviceID); try? await peersB.remove(a.deviceID)
        throw error
    }
}

@Test func dropLinkBilateralRejectionAndRemotePromptCancellationNeverCommitTrust() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpacePairingCancel-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let a = try DropLinkIdentity.create(name: "Cancel A"), b = try DropLinkIdentity.create(name: "Cancel B")
    // Neither side should reach a Keychain write in these explicit refusal cases.
    let peersA = try DropLinkPeerStore(directory: root.appendingPathComponent("a"), keychain: KeychainStore(service: "io.github.airanluo-dot.DropSpace.validation." + UUID().uuidString))
    let peersB = try DropLinkPeerStore(directory: root.appendingPathComponent("b"), keychain: KeychainStore(service: "io.github.airanluo-dot.DropSpace.validation." + UUID().uuidString))
    let remote = PairingApprovalProbe()
    let host = DropLinkPairingHost(identity: b, capabilities: [], peers: peersB) { try await remote.approve($0, wait: true) }
    let auth = DropLinkAuthenticator { try await peersB.secretForTrustedPeer($0) }
    let server = DropLinkHTTPServer(identity: b, admit: { try await auth.admit($0, address: $1) }, handler: { try await host.handle($0) })
    do {
        let endpoint = try await server.start(address: "127.0.0.1"); await host.setEndpoint(endpoint)
        let client = DropLinkPairingClient(identity: a, peers: peersA)
        await #expect(throws: (any Error).self) { try await client.pair(endpoint: endpoint, fingerprint: b.fingerprint, capabilities: []) { _ in false } }
        #expect(await remote.received().isEmpty)
        let operation = Task { try await client.pair(endpoint: endpoint, fingerprint: b.fingerprint, capabilities: []) { _ in true } }
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        while !(await remote.isWaiting()), ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(5)) }
        #expect(await remote.isWaiting()); operation.cancel()
        await #expect(throws: (any Error).self) { try await operation.value }
        #expect(!(await remote.isWaiting()))
        #expect(try await peersA.peers().isEmpty)
        #expect(try await peersB.peers().isEmpty)
        await server.stop(); await host.shutdown()
    } catch { await server.stop(); await host.shutdown(); throw error }
}
