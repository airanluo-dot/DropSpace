import Foundation
import DropSpaceCore

public struct DropLinkPairingClient: Sendable {
    public typealias Approval = @Sendable (PairingOffer) async throws -> Bool
    private let identity: DropLinkIdentity
    private let peers: DropLinkPeerStore
    public init(identity: DropLinkIdentity, peers: DropLinkPeerStore) { self.identity = identity; self.peers = peers }
    public func device(endpoint: URL, fingerprint: String) async throws -> DropLinkDevice {
        let client = try DropLinkHTTPClient(endpoint: endpoint, fingerprint: fingerprint)
        let response = try await client.send(method: "GET", path: DropLinkRoute.device)
        try Self.requireSuccess(response)
        let result = try DropLinkPairingJSON.decode(DropLinkDevice.self, from: response.body)
        guard result.identityFingerprint.lowercased() == Self.normalized(fingerprint), result.deviceId != .zero,
              result.protocol.major == 1 else { throw DropSpaceError.invalid("DropLink device identity") }
        return result
    }
    public func pair(endpoint: URL, fingerprint: String, expectedDevice: UUID? = nil, capabilities: PeerCapabilities, approve: @escaping Approval) async throws -> DropLinkPeer {
        let client = try DropLinkHTTPClient(endpoint: endpoint, fingerprint: fingerprint)
        let (key, hello) = try DropLinkCrypto.makeHello(deviceID: identity.deviceID, name: identity.displayName, fingerprint: identity.fingerprint, capabilities: capabilities)
        let reply = try await client.send(method: "POST", path: DropLinkRoute.pairingHello, body: DropLinkPairingJSON.encode(hello))
        try Self.requireSuccess(reply)
        let offer = try DropLinkPairingJSON.decode(PairingOffer.self, from: reply.body)
        guard offer.state == .awaitingLocalSasConfirmation, offer.sessionId != .zero, offer.localHello.deviceId != identity.deviceID,
              offer.localHello.identityFingerprint.lowercased() == Self.normalized(fingerprint),
              expectedDevice == nil || expectedDevice == offer.localHello.deviceId else { throw DropSpaceError.invalid("DropLink pairing offer identity") }
        let secret = try DropLinkCrypto.deriveSecret(privateKey: key, local: hello, remote: offer.localHello)
        let sas = DropLinkCrypto.sas(secret: secret, first: hello, second: offer.localHello)
        do {
            guard sas == offer.sas else { throw DropSpaceError.invalid("Pairing security codes do not match") }
            guard offer.expiresAtUtc > Date() else { throw DropSpaceError.unavailable("Pairing offer expired") }
            let accepted = try await dropLinkDeadline(seconds: max(0, offer.expiresAtUtc.timeIntervalSinceNow)) { try await approve(offer) }
            guard accepted else { throw DropSpaceError.unavailable("Pairing confirmation was declined") }
            let result = try await Self.decide(client, offer: offer, sas: sas, decision: .confirm)
            guard result.trusted, result.state == .trusted, result.peerId == offer.localHello.deviceId else {
                throw DropSpaceError.unavailable("The other device did not complete pairing: \(result.errorCategory ?? String(result.state.rawValue))")
            }
            try await peers.trust(offer.localHello, secret: secret)
            guard let peer = try await peers.peers().first(where: { $0.id == offer.localHello.deviceId && $0.trustState == .trusted }) else { throw DropSpaceError.storage("Pairing did not commit") }
            return peer
        } catch {
            let decision: PairingDecision = Task.isCancelled ? .cancel : .reject
            // A bounded notification is independent of the cancelled caller but is
            // joined before return; it cannot retain background work indefinitely.
            let cleanup = Task.detached {
                _ = try? await dropLinkDeadline(seconds: 5) { try await Self.decide(client, offer: offer, sas: sas, decision: decision) }
            }
            await cleanup.value; throw error
        }
    }
    private static func decide(_ client: DropLinkHTTPClient, offer: PairingOffer, sas: Int, decision: PairingDecision) async throws -> PairingConfirmationResponse {
        let body = try DropLinkPairingJSON.encode(PairingConfirmationRequest(sessionId: offer.sessionId, sas: sas, confirmed: decision == .confirm, decision: decision.rawValue))
        let response = try await client.send(method: "POST", path: DropLinkRoute.pairingConfirm, body: body)
        try requireSuccess(response); return try DropLinkPairingJSON.decode(PairingConfirmationResponse.self, from: response.body)
    }
    private static func normalized(_ fingerprint: String) -> String { fingerprint.replacingOccurrences(of: ":", with: "").lowercased() }
    private static func requireSuccess(_ response: DropLinkHTTPResponse) throws {
        guard (200...299).contains(response.status) else { throw DropSpaceError.unavailable("DropLink HTTP \(response.status)") }
    }
}
