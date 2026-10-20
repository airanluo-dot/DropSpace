import Foundation
import Network
import CryptoKit
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private actor LinkAcceptedRequests {
    var requests: [DropLinkHTTPRequest] = []
    func add(_ request: DropLinkHTTPRequest) { requests.append(request) }
}
private func authenticationHeaders(peer: UUID, secret: SymmetricKey, path: String, body: Data, nonceByte: UInt8) -> [String: String] {
    let nonce = Data(repeating: nonceByte, count: 24).base64EncodedString(), hash = Digests.sha256(body)
    return ["X-DropLink-Device": peer.dropLinkD, "X-DropLink-Nonce": nonce, "X-DropLink-Body-SHA256": hash,
            "X-DropLink-Auth": DropLinkCrypto.authentication(secret: secret, method: "POST", path: path, nonce: nonce, bodyHash: hash)]
}

@Test func nativeDropLinkServerAuthenticatesRealTLSBodiesAndRejectsReplay() async throws {
    let identity = try DropLinkIdentity.create(name: "Native server"), peer = UUID(), secret = SymmetricKey(size: .bits256)
    let accepted = LinkAcceptedRequests(), auth = DropLinkAuthenticator { id in id == peer ? secret : nil }
    let server = DropLinkHTTPServer(identity: identity, admit: { try await auth.admit($0, address: $1) }, handler: { request in
        await accepted.add(request); return .init(status: 200, body: request.body)
    })
    do {
        let endpoint = try await server.start(address: "127.0.0.1")
        let client = try DropLinkHTTPClient(endpoint: endpoint, fingerprint: identity.fingerprint)
        let body = Data("{\"text\":\"native transfer\"}".utf8), path = "/v1/clipboard"
        let headers = authenticationHeaders(peer: peer, secret: secret, path: path, body: body, nonceByte: 1)
        let wrongBody = try await client.send(method: "POST", path: path, headers: headers, body: Data("wrong".utf8))
        let afterMismatch = await accepted.requests
        #expect(wrongBody.status == 401 && afterMismatch.isEmpty)
        // A body mismatch releases only this reservation; the valid request can retry.
        let valid = try await client.send(method: "POST", path: path, headers: headers, body: body)
        #expect(valid.status == 200 && valid.body == body)
        #expect(await accepted.requests.count == 1)
        #expect(await accepted.requests.first?.peerID == peer)
        let replay = try await client.send(method: "POST", path: path, headers: headers, body: body)
        #expect(replay.status == 401)
        let unknown = authenticationHeaders(peer: UUID(), secret: secret, path: path, body: body, nonceByte: 2)
        #expect(try await client.send(method: "POST", path: path, headers: unknown, body: body).status == 401)
        // Auth classifies the same case/trailing-slash alias as routing, while the
        // HMAC still binds the precise path, including its case and separators.
        let alias = "/v1/CLIPBOARD///", aliasHeaders = authenticationHeaders(peer: peer, secret: secret, path: alias, body: body, nonceByte: 3)
        #expect(try await client.send(method: "POST", path: alias, headers: aliasHeaders, body: body).status == 200)
        #expect(await accepted.requests.count == 2)
        await server.stop()
        let stopped = await server.counts(); #expect(stopped.connections == 0 && stopped.requests == 0)
    } catch { await server.stop(); throw error }
}

@Test func nativeDropLinkServerEnforcesEightRequestsAndJoinsStopBeforeRestart() async throws {
    let identity = try DropLinkIdentity.create(name: "Native admission")
    let server = DropLinkHTTPServer(identity: identity, admit: { _, _ in .init(maximumBodyBytes: 65536) }, handler: { request in
        if request.head.path == "/v1/hold" { try await Task.sleep(for: .seconds(60)) }
        return .init(status: 200, body: Data("ok".utf8))
    })
    var pending = [Task<DropLinkHTTPResponse, any Error>]()
    do {
        let endpoint = try await server.start(address: "127.0.0.1")
        let client = try DropLinkHTTPClient(endpoint: endpoint, fingerprint: identity.fingerprint)
        for _ in 0..<8 { pending.append(Task { try await client.send(method: "GET", path: "/v1/hold") }) }
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while await server.counts().requests < 8, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
        #expect(await server.counts().requests == 8)
        let rejected = try await client.send(method: "GET", path: "/v1/device")
        #expect(rejected.status == 429)
        async let first: Void = server.stop()
        async let concurrent: Void = server.stop()
        await first; await concurrent
        for task in pending { await #expect(throws: (any Error).self) { try await task.value } }
        let stopped = await server.counts(); #expect(stopped.connections == 0 && stopped.requests == 0)
        let restarted = try await server.start(address: "127.0.0.1")
        let next = try DropLinkHTTPClient(endpoint: restarted, fingerprint: identity.fingerprint)
        #expect(try await next.send(method: "GET", path: "/v1/device").status == 200)
        await server.stop()
    } catch { for task in pending { task.cancel() }; await server.stop(); throw error }
}

@Test func nativeDropLinkServerLimitsUnfinishedTLSConnectionsAndClosesAllAtStop() async throws {
    let identity = try DropLinkIdentity.create(name: "Connection ceiling")
    let server = DropLinkHTTPServer(identity: identity, admit: { _, _ in .init(maximumBodyBytes: 65536) }, handler: { _ in .init(status: 200) })
    var sockets = [NWConnection]()
    do {
        let endpoint = try await server.start(address: "127.0.0.1")
        let port = NWEndpoint.Port(rawValue: UInt16(endpoint.port!))!, queue = DispatchQueue(label: "DropSpace.idle-client-tests")
        // Open TCP sockets without completing TLS. The bound applies before an
        // untrusted peer has sent any request headers or supplied an identity.
        for _ in 0..<32 {
            let socket = NWConnection(host: "127.0.0.1", port: port, using: .tcp)
            sockets.append(socket); socket.start(queue: queue)
        }
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while await server.counts().connections < 32, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
        #expect(await server.counts().connections == 32)
        #expect(await server.counts().requests == 0)
        let extra = try DropLinkHTTPClient(endpoint: endpoint, fingerprint: identity.fingerprint)
        await #expect(throws: (any Error).self) { try await extra.send(method: "GET", path: "/v1/device") }
        #expect(await server.counts().connections == 32)
        await server.stop()
        let stopped = await server.counts(); #expect(stopped.connections == 0 && stopped.requests == 0)
        for socket in sockets { socket.cancel() }
    } catch { for socket in sockets { socket.cancel() }; await server.stop(); throw error }
}

@Test func nativeDropLinkServerHashesSpooledTLSBodyAndKeepsMappedOwnership() async throws {
    let identity = try DropLinkIdentity.create(name: "Spool TLS"), peer = UUID(), secret = SymmetricKey(size: .bits256)
    let auth = DropLinkAuthenticator { $0 == peer ? secret : nil }, accepted = LinkAcceptedRequests()
    let body = Data((0..<(5 * 65536 + 19)).map { UInt8(truncatingIfNeeded: $0) }), path = DropLinkRoute.clipboard
    let server = DropLinkHTTPServer(identity: identity, admit: { try await auth.admit($0, address: $1) }, handler: { request in
        await accepted.add(request); return .init(status: 200, body: Data(Digests.sha256(request.body).utf8))
    })
    do {
        let endpoint = try await server.start(address: "127.0.0.1"), client = try DropLinkHTTPClient(endpoint: endpoint, fingerprint: identity.fingerprint)
        let headers = authenticationHeaders(peer: peer, secret: secret, path: path, body: body, nonceByte: 71)
        var corrupt = body; corrupt[corrupt.count - 1] ^= 1
        #expect(try await client.send(method: "POST", path: path, headers: headers, body: corrupt).status == 401)
        #expect(await accepted.requests.isEmpty)
        let result = try await client.send(method: "POST", path: path, headers: headers, body: body)
        #expect(result.status == 200 && result.body == Data(Digests.sha256(body).utf8))
        await server.stop()
        #expect(await accepted.requests.count == 1)
        #expect(await accepted.requests.first?.body == body)
    } catch { await server.stop(); throw error }
}
