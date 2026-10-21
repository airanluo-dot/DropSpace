import Foundation
import DropSpaceCore

/// Routes used by the real listener. A remote confirmation cannot authorize a
/// peer until an explicit local decision finishes and both durable stores commit.
public actor DropLinkPairingHost {
    public typealias Approval = @Sendable (PairingOffer) async throws -> Bool
    private let pairing: DropLinkPairingService
    private let approve: Approval
    private let capabilities: PeerCapabilities
    private let identity: DropLinkIdentity
    private var endpoint: URL?
    private var confirmations: Set<UUID> = []
    private var approvals: [UUID: Task<Bool, any Error>] = [:]
    public init(identity: DropLinkIdentity, capabilities: PeerCapabilities, peers: DropLinkPeerStore, approve: @escaping Approval) {
        self.identity = identity; self.capabilities = capabilities; self.approve = approve
        pairing = DropLinkPairingService(identity: identity) { hello, secret in try await peers.trust(hello, secret: secret) }
    }
    public func setEndpoint(_ endpoint: URL?) { self.endpoint = endpoint }
    public func handle(_ request: DropLinkHTTPRequest) async throws -> DropLinkHTTPResponse {
        guard let endpoint else { return .init(status: 503) }
        let path = request.head.path.trimmingCharacters(in: CharacterSet(charactersIn: "/")).lowercased()
        if path == "v1/device", request.head.method == "GET" {
            return try response(DropLinkDevice(deviceID: identity.deviceID, name: identity.displayName, platform: .macOS, capabilities: capabilities, fingerprint: identity.fingerprint, endpoint: endpoint))
        }
        if path == "v1/pairing/hello", request.head.method == "POST" {
            do {
                let hello = try DropLinkPairingJSON.decode(PairingHello.self, from: request.body)
                return try response(await pairing.accept(hello, capabilities: capabilities, address: request.address))
            } catch is PairingAdmissionFailure { return .init(status: 429) }
            catch { return .init(status: 400, body: Data("{\"error\":\"pairing-invalid\"}".utf8)) }
        }
        if path == "v1/pairing/confirm", request.head.method == "POST" {
            let decision: PairingConfirmationRequest
            do { decision = try DropLinkPairingJSON.decode(PairingConfirmationRequest.self, from: request.body) }
            catch { return .init(status: 400) }
            if decision.decision != PairingDecision.confirm.rawValue || !decision.confirmed {
                approvals[decision.sessionId]?.cancel()
                guard await pairing.offer(decision.sessionId) != nil else {
                    return try response(PairingConfirmationResponse(trusted: false, peerId: .zero, state: .failed, errorCategory: "pairing-state-missing"))
                }
                return try response(await pairing.confirm(decision))
            }
            guard let offer = await pairing.offer(decision.sessionId), confirmations.insert(decision.sessionId).inserted else {
                return try response(PairingConfirmationResponse(trusted: false, peerId: .zero, state: .failed, errorCategory: "pairing-state-missing"))
            }
            defer { confirmations.remove(decision.sessionId) }
            do {
                var decision = decision
                if decision.decision == PairingDecision.confirm.rawValue, decision.confirmed {
                    let task = Task { [approve] in try await dropLinkDeadline(seconds: max(0, offer.expiresAtUtc.timeIntervalSinceNow)) { try await approve(offer) } }
                    approvals[offer.sessionId] = task
                    defer { approvals[offer.sessionId] = nil }
                    let accepted = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                    try Task.checkCancellation()
                    decision.confirmed = accepted
                    decision.decision = (accepted ? PairingDecision.confirm : .reject).rawValue
                }
                let result = try await pairing.confirm(decision)
                // Successful endpoint response identifies the receiving device,
                // whereas the service result identifies the newly stored peer.
                return try response(PairingConfirmationResponse(trusted: result.trusted, peerId: result.trusted ? identity.deviceID : result.peerId, state: result.state, errorCategory: result.errorCategory))
            } catch {
                _ = try? await pairing.confirm(.init(sessionId: offer.sessionId, sas: offer.sas, confirmed: false, decision: PairingDecision.reject.rawValue))
                return try response(PairingConfirmationResponse(trusted: false, peerId: .zero, state: .failed, errorCategory: "pairing-failed"))
            }
        }
        return .init(status: 404)
    }
    public func shutdown() async {
        endpoint = nil
        let tasks = Array(approvals.values); for task in tasks { task.cancel() }
        await pairing.shutdown()
        for task in tasks { _ = await task.result }
        confirmations.removeAll(); approvals.removeAll()
    }
    private func response<T: Encodable>(_ body: T) throws -> DropLinkHTTPResponse { .init(status: 200, body: try DropLinkPairingJSON.encode(body)) }
}

func dropLinkDeadline<T: Sendable>(seconds: Double, operation: @escaping @Sendable () async throws -> T) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { tasks in
        tasks.addTask { try Task.checkCancellation(); return try await operation() }
        tasks.addTask { try await Task.sleep(for: .seconds(seconds)); throw URLError(.timedOut) }
        defer { tasks.cancelAll() }; return try await tasks.next()!
    }
}
