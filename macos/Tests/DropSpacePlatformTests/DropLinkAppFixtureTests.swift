import Foundation
import AppKit
import ImageIO
import UniformTypeIdentifiers
import CryptoKit
import Security
import Network
import Testing
import DropSpaceCore
import DropSpaceInfrastructure
@testable import DropSpacePlatform

@MainActor private final class PairingFixtureEvents {
    let root: URL
    init(root: URL) { self.root = root }
    func write(_ object: [String: Any]) throws {
        let path = root.appendingPathComponent("events.jsonl")
        if !FileManager.default.fileExists(atPath: path.path) { FileManager.default.createFile(atPath: path.path, contents: Data()) }
        let handle = try FileHandle(forWritingTo: path); defer { try? handle.close() }
        try handle.seekToEnd(); try handle.write(contentsOf: JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]) + Data([10]))
    }
}

/// Opt-in live peer for the real app UI. The peer is native production transport,
/// discovery and pairing code. Its approvals are deliberately automated fixture
/// decisions, never described as a second machine or second human UI acceptance.
@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_NATIVE_DROPLINK_FIXTURE"] == "1"))
@MainActor func nativeDropLinkAppPairingFixture() async throws {
    let env = ProcessInfo.processInfo.environment
    let root = URL(fileURLWithPath: try #require(env["DROPSPACE_FIXTURE_DIRECTORY"]))
    let endpointText = try #require(env["DROPSPACE_APP_ENDPOINT"])
    let appEndpoint = try #require(URL(string: endpointText))
    let appFingerprint = try #require(env["DROPSPACE_APP_FINGERPRINT"])
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    let identity = try DropLinkIdentity.create(name: "DropSpace Native Validation Peer")
    let keys = KeychainStore(service: "io.github.airanluo-dot.DropSpace.validation." + UUID().uuidString)
    let peers = try DropLinkPeerStore(directory: root.appendingPathComponent("Peers"), keychain: keys)
    let registration = DropLinkBonjourRegistration()
    let events = PairingFixtureEvents(root: root)
    let capabilities: PeerCapabilities = [.handoffFiles, .handoffFolders, .handoffText, .handoffURL, .clipboardText, .clipboardURL, .clipboardImage]
    let host = DropLinkPairingHost(identity: identity, capabilities: capabilities, peers: peers) { offer in
        try await MainActor.run { try events.write(["event": "fixture-approved-incoming", "sas": offer.sas]) }; return true
    }
    let items = try ItemStore(directory: root.appendingPathComponent("Workspace"))
    let board = NSPasteboard(name: .init("DropSpace.Validation.Fixture." + UUID().uuidString))
    let payloads = PayloadStore(directory: root.appendingPathComponent("Payloads"))
    let capture = ClipboardCapture(store: items, payloads: payloads, pasteboard: board)
    var clipboardSettings = AppSettings(); clipboardSettings.privacyChoicesCompleted = true; clipboardSettings.enableDeviceHandoff = true; clipboardSettings.enableCrossDeviceClipboard = true
    try await capture.configure(clipboardSettings)
    let bridge = try CrossDeviceClipboardService(capture: capture, payloads: payloads, peers: peers, discover: { _ in [] })
    try await bridge.configure(clipboardSettings, identity: identity)
    capture.onChange = { Task { try? events.write(["event": "fixture-clipboard-import", "count": try await items.items().count]) } }
    let handoff = DropLinkHandoffHost { message in
        try await items.insert([FileIntake.handoff(message, settings: AppSettings())])
        try await MainActor.run { try events.write(["event": "fixture-received-handoff", "payload": message.utf8Payload, "session": message.sessionId.dropLinkD]) }
        return true
    }
    let auth = DropLinkAuthenticator { try await peers.secretForTrustedPeer($0) }
    let manualFileApproval = env["DROPSPACE_FIXTURE_FILE_APPROVAL"] == "manual"
    let transfers = try DropLinkTransferHost(directory: root.appendingPathComponent("Transfers"), destination: root.appendingPathComponent("Received")) { offer in
        try await MainActor.run { try events.write(["event": manualFileApproval ? "fixture-waiting-files" : "fixture-approved-files", "session": offer.sessionID.dropLinkD, "count": offer.manifest.items.count]) }
        if manualFileApproval {
            do { while !FileManager.default.fileExists(atPath: root.appendingPathComponent("allow-files").path) { try await Task.sleep(for: .milliseconds(100)) } }
            catch { try? await MainActor.run { try events.write(["event": "fixture-file-approval-cancelled", "session": offer.sessionID.dropLinkD]) }; throw error }
        }
        return true
    }
    try await transfers.start()
    let server = DropLinkHTTPServer(identity: identity, admit: { try await auth.admit($0, address: $1) }, handler: { request in
        if request.head.path.lowercased().hasPrefix("/v1/transfers/") { return try await transfers.handle(request) }
        if request.head.method == "POST", request.head.path == DropLinkRoute.clipboard { return try await DropLinkClipboardHost(receive: bridge.receiver()).handle(request) }
        if request.head.method == "POST", request.head.path == DropLinkRoute.handoffText { return try await handoff.handle(request) }
        return try await host.handle(request)
    })
    func cleanup() async {
        registration.stop(); await transfers.stop(); await server.stop(); await host.shutdown(); await bridge.shutdown(); await capture.stopAndDrain(); board.releaseGlobally()
        for peer in (try? await peers.peers()) ?? [] { try? await peers.remove(peer.id) }
    }
    do {
        let network = try DropLinkNetworkInterface.preferred(), endpoint = try await server.start(address: network.address)
        await host.setEndpoint(endpoint)
        let device = DropLinkDevice(deviceID: identity.deviceID, name: identity.displayName, platform: .macOS, capabilities: capabilities, fingerprint: identity.fingerprint, endpoint: endpoint)
        try await registration.publish(device, interface: network)
        try DropLinkPairingJSON.encode(device).write(to: root.appendingPathComponent("descriptor.json"), options: .atomic)
        try events.write(["event": "ready", "endpoint": endpoint.absoluteString, "deviceId": identity.deviceID.dropLinkD])
        let client = DropLinkPairingClient(identity: identity, peers: peers)
        let app = try await withThrowingTaskGroup(of: DropLinkDevice.self) { tasks in
            tasks.addTask { try await client.device(endpoint: appEndpoint, fingerprint: appFingerprint) }
            tasks.addTask { try await Task.sleep(for: .seconds(10)); throw URLError(.timedOut) }
            defer { tasks.cancelAll() }; return try await tasks.next()!
        }
        let deadline = ContinuousClock.now.advanced(by: .seconds(300))
        var last = ""
        let clipboardText = try ClipboardEnvelopePolicy.text(origin: identity.deviceID, sequence: 1, text: "DropSpace native clipboard remote text\r\nexact event retry")
        while ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(200))
            let command = (try? String(contentsOf: root.appendingPathComponent("command.txt"), encoding: .utf8)) ?? ""
            guard command != last else { continue }; last = command
            if command == "stop" { break }
            if command == "pair-app" {
                _ = try await client.pair(endpoint: appEndpoint, fingerprint: appFingerprint, expectedDevice: app.id, capabilities: capabilities) { offer in
                    try await MainActor.run { try events.write(["event": "fixture-approved-outgoing", "sas": offer.sas]) }; return true
                }
                try events.write(["event": "paired-app"])
            }
            if command.hasPrefix("files-send-") {
                let peer = try #require(await peers.peers().first { $0.id == app.id })
                let folder = root.appendingPathComponent(command)
                try FileManager.default.createDirectory(at: folder.appendingPathComponent("Empty directory"), withIntermediateDirectories: true)
                try Data(repeating: 73, count: 4 * 1024 * 1024 + 19).write(to: folder.appendingPathComponent("Native transfer.bin"))
                try Data().write(to: folder.appendingPathComponent("Empty file.txt"))
                let result = try await DropLinkTransferClient(identity: identity, peers: peers).send(peer: peer, endpoint: appEndpoint, sources: [folder])
                try events.write(["event": command, "state": result.state.rawValue, "completed": result.completedRelativePaths, "category": result.errorCategory ?? "", "session": result.sessionId.dropLinkD])
            }
            if command.hasPrefix("clipboard-local-"), let name = env["DROPSPACE_VALIDATION_PASTEBOARD"], name.hasPrefix("DropSpace.Validation.") {
                let appBoard = NSPasteboard(name: .init(name)); appBoard.clearContents()
                appBoard.setString("DropSpace native local capture: " + command, forType: .string)
                try events.write(["event": command, "changeCount": appBoard.changeCount])
            }
            if command.hasPrefix("clipboard-send-") {
                let peer = try #require(try await peers.peers().first(where: { $0.id == app.id }))
                let envelope: ClipboardEnvelope
                if command == "clipboard-send-image" {
                    let context = try #require(CGContext(data: nil, width: 8, height: 8, bitsPerComponent: 8, bytesPerRow: 32, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
                    let image = try #require(context.makeImage()), bytes = NSMutableData()
                    let destination = try #require(CGImageDestinationCreateWithData(bytes, UTType.png.identifier as CFString, 1, nil))
                    CGImageDestinationAddImage(destination, image, nil); #expect(CGImageDestinationFinalize(destination))
                    envelope = try ClipboardEnvelopePolicy.image(origin: identity.deviceID, sequence: 2, bytes: bytes as Data, mime: "image/jpeg")
                } else { envelope = clipboardText }
                let response = try await DropLinkClipboardClient(identity: identity, peers: peers).send(peer: peer, endpoint: appEndpoint, envelope: envelope)
                try events.write(["event": command, "accepted": response.accepted, "category": response.errorCategory ?? "", "id": envelope.eventId.dropLinkD])
            }
            if command.hasPrefix("verify-auth") {
                let key = try #require(try await peers.secretForTrustedPeer(app.id)), body = Data("{}".utf8), path = "/v1/transfers/validation-unmapped"
                let nonce = UUID().dropLinkN, hash = Digests.sha256(body)
                let response = try await DropLinkHTTPClient(endpoint: appEndpoint, fingerprint: appFingerprint).send(method: "POST", path: path, headers: ["x-droplink-device": identity.deviceID.dropLinkD, "x-droplink-nonce": nonce, "x-droplink-body-sha256": hash, "x-droplink-auth": DropLinkCrypto.authentication(secret: key, method: "POST", path: path, nonce: nonce, bodyHash: hash)], body: body)
                try events.write(["event": command, "status": response.status])
            }
            if command.hasPrefix("handoff-") {
                let peer = try #require(try await peers.peers().first(where: { $0.id == app.id }))
                let url = command == "handoff-url"
                let result = try await DropLinkHandoffClient(identity: identity, peers: peers).send(peer: peer, endpoint: appEndpoint, kind: url ? .url : .text,
                    payload: url ? "https://example.test/dropspace-native-handoff#preserved-key" : "DropSpace native handoff acceptance fixture\r\n" + command,
                    label: "Native handoff fixture")
                try events.write(["event": command, "accepted": result.accepted, "category": result.errorCategory ?? "", "session": result.sessionId.dropLinkD])
            }
        }
        await cleanup(); try events.write(["event": "stopped", "peersRemaining": try await peers.peers().count])
    } catch { try? events.write(["event": "failed", "error": error.localizedDescription]); await cleanup(); throw error }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_NATIVE_PRIVATE_TLS"] == "1"))
func nativeDropLinkPrivateInterfaceTLSDiagnostic() async throws {
    let identity = try DropLinkIdentity.create(name: "Private interface diagnostic")
    let network = try DropLinkNetworkInterface.preferred()
    let server = DropLinkHTTPServer(identity: identity, admit: { _, _ in .init(maximumBodyBytes: 65536) }, handler: { _ in .init(status: 200, body: Data("ok".utf8)) })
    let endpoint = try await server.start(address: network.address)
    let task = Task { try await DropLinkHTTPClient(endpoint: endpoint, fingerprint: identity.fingerprint).send(method: "GET", path: "/v1/device") }
    try await Task.sleep(for: .seconds(3))
    let counts = await server.counts()
    print("Private-interface server counts: \(counts.connections)/\(counts.requests)")
    if counts.connections == 0 { task.cancel() }
    do {
        let response = try await task.value
        #expect(response.status == 200 && response.body == Data("ok".utf8))
        await server.stop()
    } catch { await server.stop(); throw error }
}

/// Bounded, opt-in diagnostic only. The public leaf is scoped to an external
/// client's CA file; no key is exported and no system trust is installed.
@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_PRIVATE_LISTENER_PROBE"] == "1"))
func nativeDropLinkPrivateListenerProbe() async throws {
    let root = URL(fileURLWithPath: try #require(ProcessInfo.processInfo.environment["DROPSPACE_PROBE_DIRECTORY"]))
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    let network = try DropLinkNetworkInterface.preferred(), identity = try DropLinkIdentity.create(name: "Listener probe")
    let bytes = SecCertificateCopyData(identity.certificate) as Data
    let pem = "-----BEGIN CERTIFICATE-----\n" + bytes.base64EncodedString(options: [.lineLength64Characters, .endLineWithLineFeed]) + "\n-----END CERTIFICATE-----\n"
    try Data(pem.utf8).write(to: root.appendingPathComponent("public-leaf.pem"))
    var servers: [DropLinkHTTPServer] = [], endpoints: [String: String] = [:]
    do {
        for (name, address) in [("specific", network.address), ("wildcard", "0.0.0.0")] {
            let server = DropLinkHTTPServer(identity: identity, admit: { _, _ in .init(maximumBodyBytes: 65536) }, handler: { _ in .init(status: 200, body: Data("probe-ok".utf8)) })
            servers.append(server)
            let endpoint = try await server.start(address: address)
            endpoints[name] = "\(network.address):\(endpoint.port!)"
        }
        try JSONSerialization.data(withJSONObject: endpoints).write(to: root.appendingPathComponent("endpoints.json"))
        let deadline = ContinuousClock.now.advanced(by: .seconds(30))
        while ContinuousClock.now < deadline, !FileManager.default.fileExists(atPath: root.appendingPathComponent("done").path) { try await Task.sleep(for: .milliseconds(100)) }
        for server in servers { let counts = await server.counts(); print("Probe retained connections/requests: \(counts.connections)/\(counts.requests)"); await server.stop() }
    } catch { for server in servers { await server.stop() }; throw error }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_PRIVATE_PATH_PROBE"] == "1"))
func nativeDropLinkPrivatePathProbe() async throws {
    let identity = try DropLinkIdentity.create(name: "Private path probe"), network = try DropLinkNetworkInterface.preferred()
    let server = DropLinkHTTPServer(identity: identity, admit: { _, _ in .init(maximumBodyBytes: 65536) }, handler: { _ in .init(status: 200) })
    let endpoint = try await server.start(address: network.address)
    for mode in ["default", "prefer-direct", "isolated-context"] {
        let tls = NWProtocolTLS.Options(), queue = DispatchQueue(label: "DropLink.PathProbe." + mode)
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_add_tls_application_protocol(tls.securityProtocolOptions, "http/1.1")
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, peer, complete in
            let trust = sec_trust_copy_ref(peer).takeRetainedValue()
            let leaf = (SecTrustCopyCertificateChain(trust) as? [SecCertificate])?.first
            let matched = leaf.map { Digests.sha256(SecCertificateCopyData($0) as Data) == identity.fingerprint } ?? false
            print("\(mode): certificate pin \(matched)"); complete(matched)
        }, queue)
        let parameters = NWParameters(tls: tls, tcp: NWProtocolTCP.Options())
        if mode != "default" { parameters.preferNoProxies = true }
        if mode == "isolated-context" { let context = NWParameters.PrivacyContext(description: "DropSpace isolated local path probe"); context.proxyConfigurations = []; parameters.setPrivacyContext(context) }
        let connection = NWConnection(host: .init(network.address), port: .init(rawValue: UInt16(endpoint.port!))!, using: parameters)
        connection.stateUpdateHandler = { print("\(mode): \($0)") }
        connection.pathUpdateHandler = { print("\(mode): path \($0)") }
        connection.start(queue: queue)
        try await Task.sleep(for: .seconds(2))
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            connection.requestEstablishmentReport(queue: queue) { report in
                print("\(mode): establishment \(String(describing: report))"); done.resume()
            }
        }
        connection.cancel()
    }
    await server.stop()
}
