import Foundation
import DropSpaceCore

public struct DropLinkClipboardHost: Sendable {
    private let receive: (@Sendable (ClipboardEnvelope) async throws -> Void)?
    public init(receive: (@Sendable (ClipboardEnvelope) async throws -> Void)?) { self.receive = receive }
    public func handle(_ request: DropLinkHTTPRequest) async throws -> DropLinkHTTPResponse {
        guard let peer = request.peerID else { return .init(status: 401) }
        let value: ClipboardSyncRequest
        do { value = try DropLinkClipboardJSON.decode(ClipboardSyncRequest.self, from: request.body) }
        catch { return .init(status: 400) }
        guard value.peerId == peer else { return .init(status: 401) }
        func response(_ category: String?, status: Int = 200) throws -> DropLinkHTTPResponse {
            .init(status: status, body: try DropLinkClipboardJSON.encode(ClipboardSyncResponse(accepted: category == nil, errorCategory: category)))
        }
        do {
            guard let envelope = value.envelope else { return try response("clipboard-invalid", status: 400) }
            try ClipboardEnvelopePolicy.validate(envelope)
            guard envelope.originDeviceId == peer else { return try response("origin-device-mismatch", status: 400) }
            guard let receive else { throw ClipboardReceiveError.unavailable }
            try Task.checkCancellation(); try await receive(envelope); return try response(nil)
        } catch is CancellationError { throw CancellationError() }
        catch ClipboardReceiveError.unavailable { return try response("clipboard-receiver-unavailable") }
        catch ClipboardReceiveError.paused { return try response("clipboard-paused") }
        catch { return try response("clipboard-invalid", status: 400) }
    }
}
public struct DropLinkClipboardClient: Sendable {
    private let identity: DropLinkIdentity
    private let peers: DropLinkPeerStore
    public init(identity: DropLinkIdentity, peers: DropLinkPeerStore) { self.identity = identity; self.peers = peers }
    public func send(peer: DropLinkPeer, endpoint: URL, envelope: ClipboardEnvelope) async throws -> ClipboardSyncResponse {
        try ClipboardEnvelopePolicy.validate(envelope)
        guard envelope.originDeviceId == identity.deviceID, let secret = try await peers.secretForTrustedPeer(peer.id) else { throw DropSpaceError.unavailable("Clipboard sender or peer identity") }
        let body = try DropLinkClipboardJSON.encode(ClipboardSyncRequest(peer: identity.deviceID, envelope: envelope))
        let nonce = UUID().dropLinkN, hash = Digests.sha256(body), path = DropLinkRoute.clipboard
        let response = try await DropLinkHTTPClient(endpoint: endpoint, fingerprint: peer.identityFingerprint).send(method: "POST", path: path, headers: [
            "x-droplink-device": identity.deviceID.dropLinkD, "x-droplink-nonce": nonce, "x-droplink-body-sha256": hash,
            "x-droplink-auth": DropLinkCrypto.authentication(secret: secret, method: "POST", path: path, nonce: nonce, bodyHash: hash)
        ], body: body)
        guard response.status == 200 else { throw DropSpaceError.unavailable("Clipboard sync HTTP \(response.status)") }
        return try DropLinkClipboardJSON.decode(ClipboardSyncResponse.self, from: response.body)
    }
}
