import Foundation
import DropSpaceCore

public actor DropLinkHandoffHost {
    public typealias Receive = @Sendable (HandoffMessage) async throws -> Bool
    private let receive: Receive?
    private let replay = DropLinkHandoffReplayCache()
    public init(receive: Receive?) { self.receive = receive }
    public func handle(_ request: DropLinkHTTPRequest, now: Date = Date()) async throws -> DropLinkHTTPResponse {
        guard let peer = request.peerID else { return .init(status: 401) }
        let body: HandoffMessageRequest
        do { body = try DropLinkHandoffJSON.decode(HandoffMessageRequest.self, from: request.body) }
        catch { return .init(status: 400) }
        guard body.peerId == peer else { return .init(status: 401) }
        guard let message = body.message, message.senderDeviceId == peer else { return .init(status: 400, body: Data("{\"error\":\"handoff-origin-mismatch\"}".utf8)) }
        func response(_ category: String?) throws -> DropLinkHTTPResponse {
            .init(status: 200, body: try DropLinkHandoffJSON.encode(HandoffMessageResponse(sessionId: message.sessionId, accepted: category == nil, errorCategory: category)))
        }
        do {
            try HandoffMessagePolicy.validate(message)
            guard let created = DropLinkHandoffJSON.utcDate(message.createdAtUtc), created >= now.addingTimeInterval(-600), created <= now.addingTimeInterval(60) else { return try response("expired") }
            guard await replay.reserve(peer: peer, session: message.sessionId, now: now) else { return try response("duplicate-session") }
            guard let receive else { return try response("handoff-receiver-unavailable") }
            try Task.checkCancellation()
            guard try await receive(message) else { return try response("rejected") }
            return try response(nil)
        } catch is CancellationError { throw CancellationError() }
        catch { return try response("handoff-invalid") }
    }
}

public struct DropLinkHandoffClient: Sendable {
    private let identity: DropLinkIdentity
    private let peers: DropLinkPeerStore
    public init(identity: DropLinkIdentity, peers: DropLinkPeerStore) { self.identity = identity; self.peers = peers }
    public func send(peer: DropLinkPeer, endpoint: URL, kind: HandoffMessageKind, payload: String, label: String? = nil) async throws -> HandoffMessageResponse {
        let message = try HandoffMessagePolicy.create(sender: identity.deviceID, name: identity.displayName, kind: kind, payload: payload, label: label)
        guard let secret = try await peers.secretForTrustedPeer(peer.id) else { throw DropSpaceError.unavailable("Device is no longer trusted") }
        let body = try DropLinkHandoffJSON.encode(HandoffMessageRequest(peerId: identity.deviceID, message: message))
        let nonce = UUID().dropLinkN, hash = Digests.sha256(body), path = DropLinkRoute.handoffText
        let response = try await DropLinkHTTPClient(endpoint: endpoint, fingerprint: peer.identityFingerprint).send(method: "POST", path: path, headers: [
            "x-droplink-device": identity.deviceID.dropLinkD, "x-droplink-nonce": nonce, "x-droplink-body-sha256": hash,
            "x-droplink-auth": DropLinkCrypto.authentication(secret: secret, method: "POST", path: path, nonce: nonce, bodyHash: hash)
        ], body: body)
        guard response.status == 200 else { throw DropSpaceError.unavailable("Device handoff HTTP \(response.status)") }
        let result = try DropLinkHandoffJSON.decode(HandoffMessageResponse.self, from: response.body)
        guard result.sessionId == message.sessionId else { throw DropSpaceError.invalid("Handoff response session") }; return result
    }
}
