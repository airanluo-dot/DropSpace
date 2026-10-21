import Foundation
import CryptoKit
import Network
import Security
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private final class LocalTLSFixture: @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "DropSpace.TLSFixture")
    private var connections: [UUID: NWConnection] = [:]
    private var requests = 0
    init(identity: DropLinkIdentity) throws {
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_add_tls_application_protocol(tls.securityProtocolOptions, "http/1.1")
        sec_protocol_options_set_local_identity(tls.securityProtocolOptions, sec_identity_create(identity.identity)!)
        let parameters = NWParameters(tls: tls, tcp: NWProtocolTCP.Options())
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }
    func start() async throws -> UInt16 {
        try await withCheckedThrowingContinuation { continuation in
            listener.stateUpdateHandler = { [self] state in
                switch state {
                case .ready: listener.stateUpdateHandler = nil; continuation.resume(returning: listener.port!.rawValue)
                case .failed(let error): listener.stateUpdateHandler = nil; continuation.resume(throwing: error)
                default: break
                }
            }
            listener.newConnectionHandler = { [self] connection in
                let id = UUID(); connections[id] = connection
                connection.stateUpdateHandler = { [weak self] state in
                    if case .failed = state { connection.cancel(); connection.stateUpdateHandler = nil; self?.connections[id] = nil }
                    if case .cancelled = state { connection.stateUpdateHandler = nil; self?.connections[id] = nil }
                }
                connection.start(queue: queue); receive(connection, buffer: Data())
            }
            listener.start(queue: queue)
        }
    }
    private func receive(_ connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 32768) { [self] bytes, _, complete, error in
            var buffer = buffer; if let bytes { buffer.append(bytes) }
            guard error == nil, buffer.count <= 32768 else { connection.cancel(); return }
            guard let split = buffer.range(of: Data("\r\n\r\n".utf8)) else {
                if complete { connection.cancel() } else { receive(connection, buffer: buffer) }; return
            }
            requests += 1
            let header = String(decoding: buffer[..<split.lowerBound], as: UTF8.self)
            if header.contains("/v1/slow ") { return }
            let response: Data
            if header.contains("/v1/framed ") {
                response = Data("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n3;extension=yes\r\none\r\n3\r\ntwo\r\n0\r\nX-Checksum: none\r\n\r\n".utf8)
            } else if header.contains("/v1/ambiguous ") {
                response = Data("HTTP/1.1 200 OK\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n0\r\n\r\n".utf8)
            } else if header.contains("/v1/truncated ") {
                response = Data("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\na".utf8)
            } else if header.contains("/v1/repeated-length ") {
                response = Data("HTTP/1.1 200 OK\r\nContent-Length: 1\r\nContent-Length: 1\r\nConnection: close\r\n\r\na".utf8)
            } else if header.contains("/v1/close-body ") {
                response = Data("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nclosed".utf8)
            } else if header.contains("/v1/redirect ") {
                response = Data("HTTP/1.1 302 Found\r\nLocation: https://127.0.0.1:1/v1/device\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".utf8)
            } else if header.contains("/v1/large ") {
                response = Data("HTTP/1.1 200 OK\r\nContent-Length: 65537\r\nConnection: close\r\n\r\n".utf8) + Data(repeating: 65, count: 65537)
            } else if header.contains("/v1/chunked ") {
                response = Data("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n10001\r\n".utf8) + Data(repeating: 65, count: 65537) + Data("\r\n0\r\n\r\n".utf8)
            } else {
                let body = Data("{\"nativeTLS\":true}".utf8)
                response = Data("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: \(body.count)\r\nConnection: close\r\n\r\n".utf8) + body
            }
            connection.send(content: response, completion: .contentProcessed { _ in connection.cancel() })
        }
    }
    func requestCount() async -> Int { await withCheckedContinuation { continuation in queue.async { continuation.resume(returning: self.requests) } } }
    func stop() { queue.sync { listener.cancel(); listener.newConnectionHandler = nil; for connection in connections.values { connection.cancel() }; connections.removeAll() } }
}

@Test func nativeDropLinkCertificateUsesMatchingP256IdentityAndSystemVerifier() throws {
    let identity = try DropLinkIdentity.create(name: "Validation Mac")
    #expect(identity.platform == .macOS && identity.deviceID != .zero && identity.displayName == "Validation Mac")
    #expect(SecCertificateCopySubjectSummary(identity.certificate) as String? == "DropSpace-" + identity.deviceID.dropLinkN)
    #expect(identity.fingerprint == Digests.sha256(SecCertificateCopyData(identity.certificate) as Data))
    var trust: SecTrust?
    #expect(SecTrustCreateWithCertificates(identity.certificate, SecPolicyCreateBasicX509(), &trust) == errSecSuccess)
    let evaluated = try #require(trust)
    #expect(SecTrustSetAnchorCertificates(evaluated, [identity.certificate] as CFArray) == errSecSuccess)
    #expect(SecTrustSetAnchorCertificatesOnly(evaluated, true) == errSecSuccess)
    SecTrustSetNetworkFetchAllowed(evaluated, false)
    var error: CFError?
    #expect(SecTrustEvaluateWithError(evaluated, &error))
    let encoded = try identity.keychainData(), restored = try DropLinkIdentity.restore(encoded)
    #expect(restored.deviceID == identity.deviceID && restored.fingerprint == identity.fingerprint)
    var object = try #require(try JSONSerialization.jsonObject(with: encoded) as? [String: Any])
    object["privateKey"] = P256.Signing.PrivateKey().x963Representation.base64EncodedString()
    #expect(throws: (any Error).self) { try DropLinkIdentity.restore(JSONSerialization.data(withJSONObject: object)) }
}

@Test func nativeDropLinkTLSClientPinsLeafRejectsRedirectAndBoundsActualResponse() async throws {
    let identity = try DropLinkIdentity.create(name: "Local TLS verification"), server = try LocalTLSFixture(identity: identity)
    defer { server.stop() }
    let port = try await server.start(), endpoint = URL(string: "https://127.0.0.1:\(port)")!
    let client = try DropLinkHTTPClient(endpoint: endpoint, fingerprint: identity.fingerprint)
    let response = try await client.send(method: "GET", path: "/v1/device")
    #expect(response.status == 200 && String(decoding: response.body, as: UTF8.self) == "{\"nativeTLS\":true}")
    let wrong = try DropLinkHTTPClient(endpoint: endpoint, fingerprint: String(repeating: "0", count: 64))
    await #expect(throws: (any Error).self) { try await wrong.send(method: "GET", path: "/v1/device") }
    #expect(await server.requestCount() == 1)
    for path in ["/v1/redirect", "/v1/large", "/v1/chunked"] {
        await #expect(throws: (any Error).self) { try await client.send(method: "GET", path: path) }
    }
    #expect(await server.requestCount() == 4)
    // A request blocked awaiting response headers must settle when cancelled.
    let pending = Task { try await client.send(method: "POST", path: "/v1/slow", body: Data("{}".utf8)) }
    let deadline = ContinuousClock.now.advanced(by: .seconds(5))
    while await server.requestCount() < 5, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
    #expect(await server.requestCount() == 5)
    pending.cancel()
    await #expect(throws: (any Error).self) { try await pending.value }
}

@Test func nativeDropLinkTLSReadsChunkedWindowsStyleJSONAndRejectsAmbiguousFraming() async throws {
    let identity = try DropLinkIdentity.create(name: "Framing validation"), server = try LocalTLSFixture(identity: identity)
    defer { server.stop() }
    let port = try await server.start()
    let client = try DropLinkHTTPClient(endpoint: URL(string: "https://127.0.0.1:\(port)")!, fingerprint: identity.fingerprint)
    let framed = try await client.send(method: "POST", path: "/v1/framed", body: Data("{}".utf8))
    #expect(framed.status == 200 && String(decoding: framed.body, as: UTF8.self) == "onetwo")
    let closed = try await client.send(method: "GET", path: "/v1/close-body")
    #expect(String(decoding: closed.body, as: UTF8.self) == "closed")
    for path in ["/v1/ambiguous", "/v1/truncated", "/v1/repeated-length"] {
        await #expect(throws: (any Error).self) { try await client.send(method: "POST", path: path, body: Data("{}".utf8)) }
    }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_KEYCHAIN"] == "1"))
func nativeDropLinkIdentityKeychainRoundTripUsesOnePersistentDeviceAndRetainsCorruption() async throws {
    let keychain = KeychainStore(service: "io.github.airanluo-dot.DropSpace.validation." + UUID().uuidString)
    let account = "droplink.identity.v1", store = DropLinkIdentityStore(keychain: keychain)
    do {
        async let first = store.getOrCreate(name: "Local Keychain test")
        async let concurrent = store.getOrCreate(name: "Concurrent request")
        let a = try await first, b = try await concurrent
        #expect(a.deviceID == b.deviceID && a.fingerprint == b.fingerprint)
        let saved = try #require(try await keychain.read(account))
        let reopened = try await DropLinkIdentityStore(keychain: keychain).getOrCreate(name: "Do not replace the saved identity")
        #expect(reopened.deviceID == a.deviceID && reopened.fingerprint == a.fingerprint)
        #expect(reopened.platform == .macOS)
        let broken = Data("invalid owned test identity".utf8); try await keychain.save(broken, account: account)
        await #expect(throws: (any Error).self) { try await DropLinkIdentityStore(keychain: keychain).getOrCreate(name: "No silent replacement") }
        #expect(try await keychain.read(account) == broken)
        try await keychain.save(saved, account: account)
        let restored = try await DropLinkIdentityStore(keychain: keychain).getOrCreate(name: "Recovered")
        #expect(restored.fingerprint == a.fingerprint)
        try await keychain.remove(account)
        #expect(try await keychain.read(account) == nil)
    } catch { try? await keychain.remove(account); throw error }
}
