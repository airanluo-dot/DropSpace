import AppKit
import CryptoKit
import ImageIO
import Testing
import UniformTypeIdentifiers
import DropSpaceCore
@testable import DropSpaceInfrastructure
@testable import DropSpacePlatform

private actor ClipboardTestSecrets: DropLinkSecretStorage {
    private var values: [String: Data] = [:]
    func read(_ account: String) -> Data? { values[account] }
    func save(_ data: Data, account: String) { values[account] = data }
    func remove(_ account: String) { values[account] = nil }
}
private func clipboardPNG() throws -> Data {
    let context = try #require(CGContext(data: nil, width: 2, height: 3, bitsPerComponent: 8, bytesPerRow: 8, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    let data = NSMutableData(), image = try #require(context.makeImage())
    let destination = try #require(CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil))
    CGImageDestinationAddImage(destination, image, nil); #expect(CGImageDestinationFinalize(destination)); return data as Data
}
private func waitForItems(_ store: ItemStore, count: Int) async throws {
    let limit = ContinuousClock.now.advanced(by: .seconds(4))
    while try await store.items().count != count, ContinuousClock.now < limit { try await Task.sleep(for: .milliseconds(20)) }
    #expect(try await store.items().count == count)
}

@Test @MainActor func clipboardTLSRemoteCommitPauseRetryGenerationAndImageValidation() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("ClipboardTLS-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let originalCount = NSPasteboard.general.changeCount
    let board = NSPasteboard(name: .init("DropSpace-test-" + UUID().uuidString)); defer { board.releaseGlobally() }
    let store = try ItemStore(directory: root), payloads = PayloadStore(directory: root.appendingPathComponent("Payloads"))
    let capture = ClipboardCapture(store: store, payloads: payloads, pasteboard: board)
    var settings = AppSettings(); settings.privacyChoicesCompleted = true; settings.enableDeviceHandoff = true; settings.enableCrossDeviceClipboard = true; settings.maxTextCharacters = 1024
    try await capture.configure(settings)
    var captured = 0; capture.onCaptured = { _ in captured += 1 }
    let sender = try DropLinkIdentity.create(name: "Clipboard sender"), receiver = try DropLinkIdentity.create(name: "Clipboard receiver")
    let peers = try DropLinkPeerStore(directory: root.appendingPathComponent("Peers"), keychain: ClipboardTestSecrets())
    let secret = SymmetricKey(size: .bits256)
    try await peers.trust(DropLinkCrypto.makeHello(deviceID: receiver.deviceID, name: receiver.displayName, fingerprint: receiver.fingerprint, capabilities: [.clipboardText, .clipboardURL, .clipboardImage]).1, secret: secret)
    let peer = try #require(try await peers.peers().first)
    let bridge = try CrossDeviceClipboardService(capture: capture, payloads: payloads, peers: peers, discover: { _ in [] })
    try await bridge.configure(settings, identity: receiver)
    let oldReceiver = try #require(await bridge.receiver())
    let auth = DropLinkAuthenticator { $0 == sender.deviceID ? secret : nil }
    let server = DropLinkHTTPServer(identity: receiver, admit: { try await auth.admit($0, address: $1) }, handler: { request in try await DropLinkClipboardHost(receive: bridge.receiver()).handle(request) })
    do {
        let endpoint = try await server.start(address: "127.0.0.1"), client = DropLinkClipboardClient(identity: sender, peers: peers)
        let envelope = try ClipboardEnvelopePolicy.text(origin: sender.deviceID, sequence: 1, text: String(repeating: "x", count: 1025) + "\r\ntail")
        #expect(try await client.send(peer: peer, endpoint: endpoint, envelope: envelope).accepted)
        #expect(try await store.items().count == 1)
        #expect(board.string(forType: .string) == envelope.text?.replacingOccurrences(of: "\r\n", with: "\n"))
        let change = board.changeCount
        #expect(try await client.send(peer: peer, endpoint: endpoint, envelope: envelope).accepted)
        #expect(board.changeCount == change && captured == 0)
        settings.clipboardPaused = true; try await capture.configure(settings)
        let paused = try await client.send(peer: peer, endpoint: endpoint, envelope: envelope)
        #expect(!paused.accepted && paused.errorCategory == "clipboard-paused")
        settings.clipboardPaused = false; try await capture.configure(settings)
        settings.enableCrossDeviceClipboard = false; try await bridge.configure(settings, identity: receiver)
        #expect(!(try await client.send(peer: peer, endpoint: endpoint, envelope: envelope).accepted))
        settings.enableCrossDeviceClipboard = true; try await bridge.configure(settings, identity: receiver)
        await #expect(throws: ClipboardReceiveError.unavailable) { try await oldReceiver(envelope) }
        #expect(try await client.send(peer: peer, endpoint: endpoint, envelope: envelope).accepted)
        #expect(try await store.items().count == 1)
        let fault = try SQLiteConnection(url: root.appendingPathComponent("workspace.sqlite"))
        try fault.execute("CREATE TRIGGER fail_clipboard BEFORE INSERT ON items BEGIN SELECT RAISE(ABORT,'Injected full disk'); END")
        let bytes = try clipboardPNG(), image = try ClipboardEnvelopePolicy.image(origin: sender.deviceID, sequence: 2, bytes: bytes, mime: "image/jpeg")
        await #expect(throws: (any Error).self) { try await client.send(peer: peer, endpoint: endpoint, envelope: image) }
        #expect((try? FileManager.default.contentsOfDirectory(atPath: root.appendingPathComponent("Payloads").path))?.isEmpty == true)
        try fault.execute("DROP TRIGGER fail_clipboard")
        #expect(try await client.send(peer: peer, endpoint: endpoint, envelope: image).accepted)
        let imageItem = try #require(try await store.items().first { $0.kind == .image })
        #expect(imageItem.payloadPath?.hasSuffix(".png") == true)
        #expect(board.data(forType: .png) == bytes)
        let invalid = try ClipboardEnvelopePolicy.image(origin: sender.deviceID, sequence: 3, bytes: Data("not an image".utf8), mime: "image/png")
        await #expect(throws: (any Error).self) { try await client.send(peer: peer, endpoint: endpoint, envelope: invalid) }
        #expect(try await store.items().count == 2)
        let reopened = try ItemStore(directory: root); #expect(try await reopened.items().count == 2)
        #expect(NSPasteboard.general.changeCount == originalCount)
        await server.stop(); await bridge.shutdown(); await capture.stopAndDrain()
    } catch { await server.stop(); await bridge.shutdown(); await capture.stopAndDrain(); throw error }
}

@Test @MainActor func clipboardAutomaticNativeCaptureExplicitModesAndNoWritebackLoop() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("ClipboardAutomatic-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let aBoard = NSPasteboard(name: .init("DropSpace-A-" + UUID().uuidString)), bBoard = NSPasteboard(name: .init("DropSpace-B-" + UUID().uuidString))
    defer { aBoard.releaseGlobally(); bBoard.releaseGlobally() }
    let aStore = try ItemStore(directory: root.appendingPathComponent("A")), bStore = try ItemStore(directory: root.appendingPathComponent("B"))
    let aPayloads = PayloadStore(directory: root.appendingPathComponent("A/Payloads")), bPayloads = PayloadStore(directory: root.appendingPathComponent("B/Payloads"))
    let aCapture = ClipboardCapture(store: aStore, payloads: aPayloads, pasteboard: aBoard), bCapture = ClipboardCapture(store: bStore, payloads: bPayloads, pasteboard: bBoard)
    let a = try DropLinkIdentity.create(name: "A"), b = try DropLinkIdentity.create(name: "B"), secret = SymmetricKey(size: .bits256)
    let aPeers = try DropLinkPeerStore(directory: root.appendingPathComponent("A/Peers"), keychain: ClipboardTestSecrets()), bPeers = try DropLinkPeerStore(directory: root.appendingPathComponent("B/Peers"), keychain: ClipboardTestSecrets())
    let caps: PeerCapabilities = [.clipboardText, .clipboardURL, .clipboardImage]
    try await aPeers.trust(DropLinkCrypto.makeHello(deviceID: b.deviceID, name: "B", fingerprint: b.fingerprint, capabilities: caps).1, secret: secret)
    try await bPeers.trust(DropLinkCrypto.makeHello(deviceID: a.deviceID, name: "A", fingerprint: a.fingerprint, capabilities: caps).1, secret: secret)
    let bBridge = try CrossDeviceClipboardService(capture: bCapture, payloads: bPayloads, peers: bPeers, discover: { _ in [] })
    let auth = DropLinkAuthenticator { $0 == a.deviceID ? secret : nil }
    let server = DropLinkHTTPServer(identity: b, admit: { try await auth.admit($0, address: $1) }, handler: { request in try await DropLinkClipboardHost(receive: bBridge.receiver()).handle(request) })
    let endpoint = try await server.start(address: "127.0.0.1")
    let device = DropLinkDevice(deviceID: b.deviceID, name: "B", platform: .macOS, capabilities: caps, fingerprint: b.fingerprint, endpoint: endpoint)
    let aBridge = try CrossDeviceClipboardService(capture: aCapture, payloads: aPayloads, peers: aPeers, discover: { _ in [device] })
    do {
        var config = AppSettings(); config.privacyChoicesCompleted = true; config.enableDeviceHandoff = true; config.enableCrossDeviceClipboard = true; config.defaultClipboardSyncMode = .textURLAndImage
        try await aCapture.configure(config); try await bCapture.configure(config)
        try await aBridge.configure(config, identity: a); try await bBridge.configure(config, identity: b)
        // Merely choosing a default, without selecting a peer, must not transmit.
        aBoard.clearContents(); aBoard.setString("local only", forType: .string)
        try await waitForItems(aStore, count: 1); try await Task.sleep(for: .milliseconds(350))
        #expect(try await bStore.items().isEmpty)
        config.clipboardPeerModes[b.deviceID.uuidString] = .textAndURL; try await aBridge.configure(config, identity: a)
        aBoard.clearContents(); aBoard.setString("  HTTPS://EXAMPLE.TEST:443/cross-clipboard#keep  ", forType: .string)
        try await waitForItems(bStore, count: 1)
        #expect(bBoard.string(forType: .string) == "https://example.test/cross-clipboard#keep")
        try await Task.sleep(for: .milliseconds(350)); #expect(try await bStore.items().count == 1)
        aBoard.clearContents(); aBoard.setData(try clipboardPNG(), forType: .png)
        try await waitForItems(aStore, count: 3); try await Task.sleep(for: .milliseconds(350)); #expect(try await bStore.items().count == 1)
        config.clipboardPeerModes[b.deviceID.uuidString] = .textURLAndImage; try await aBridge.configure(config, identity: a)
        // A manual image send uses the real stored payload and authenticated route.
        let item = try #require(try await aStore.items().first { $0.kind == .image }), peer = try #require(try await aPeers.peers().first)
        #expect(try await aBridge.sendManual(peer: peer, endpoint: endpoint, item: item).accepted)
        try await waitForItems(bStore, count: 2)
        config.clipboardPeerModes[b.deviceID.uuidString] = .manual; try await aBridge.configure(config, identity: a)
        aBoard.clearContents(); aBoard.setString("manual mode local", forType: .string)
        try await waitForItems(aStore, count: 4); try await Task.sleep(for: .milliseconds(350)); #expect(try await bStore.items().count == 2)
        await server.stop(); await aBridge.shutdown(); await bBridge.shutdown(); await aCapture.stopAndDrain(); await bCapture.stopAndDrain()
    } catch { await server.stop(); await aBridge.shutdown(); await bBridge.shutdown(); await aCapture.stopAndDrain(); await bCapture.stopAndDrain(); throw error }
}

@Test func clipboardImageTimeoutRetainsNativeOwnerUntilActualExit() async throws {
    let lifetime = ClipboardImageReadLifetime(), started = DispatchSemaphore(value: 0), exit = DispatchSemaphore(value: 0)
    let info = ClipboardImageInfo(suffix: "png", mime: "image/png", typeIdentifier: UTType.png.identifier, width: 1, height: 1)
    let task = Task { try await lifetime.run(timeout: .milliseconds(30)) { started.signal(); exit.wait(); return info } }
    await withCheckedContinuation { continuation in DispatchQueue.global().async { started.wait(); continuation.resume() } }
    await #expect(throws: (any Error).self) { try await task.value }
    #expect(await lifetime.outstanding)
    await #expect(throws: (any Error).self) { try await lifetime.run { info } }
    exit.signal(); await lifetime.drain(); #expect(!(await lifetime.outstanding))
    #expect(try await lifetime.run { info }.width == 1)
}
