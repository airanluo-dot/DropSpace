import Foundation
import CryptoKit
import DropSpaceCore

public struct PairingAdmissionFailure: Error, Sendable { public let category: String }
public actor DropLinkPairingService {
    public typealias Commit = @Sendable (PairingHello, SymmetricKey) async throws -> Void
    private struct Pending { let offer: PairingOffer; let secret: SymmetricKey; let address: String }
    private struct Rate { var began: Date; var attempts: Int }
    public let identity: DropLinkIdentity
    private let commit: Commit
    private var pending: [UUID: Pending] = [:]
    private var rates: [String: Rate] = [:]
    private var expiry: Task<Void, Never>?
    private var commits: [UUID: Task<Void, any Error>] = [:]
    private var disposed = false
    public init(identity: DropLinkIdentity, commit: @escaping Commit) { self.identity = identity; self.commit = commit }
    public func pendingCount() -> Int { pending.count }
    public func accept(_ remote: PairingHello, capabilities: PeerCapabilities, address: String?, now: Date = Date()) throws -> PairingOffer {
        try requireActive()
        let trimmed = address?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let address = trimmed.isEmpty ? "unknown" : String(trimmed.prefix(128))
        rates = rates.filter { now.timeIntervalSince($0.value.began) <= 60 }
        var rate = rates[address] ?? Rate(began: now, attempts: 0)
        guard rates[address] != nil || rates.count < 1024 else { throw PairingAdmissionFailure(category: "pairing-rate-capacity") }
        guard rate.attempts < 20 else { throw PairingAdmissionFailure(category: "pairing-rate-limited") }
        rate.attempts += 1; rates[address] = rate
        try remote.validate()
        _ = try P256.KeyAgreement.PublicKey(derRepresentation: DropLinkEncoding.base64(remote.publicKeyBase64)!)
        let (key, local) = try DropLinkCrypto.makeHello(deviceID: identity.deviceID, name: identity.displayName, fingerprint: identity.fingerprint, capabilities: capabilities)
        pending = pending.filter { $0.value.offer.expiresAtUtc > now }
        guard pending.count < 16 else { throw PairingAdmissionFailure(category: "pairing-capacity") }
        guard pending.values.filter({ $0.address == address }).count < 4 else { throw PairingAdmissionFailure(category: "pairing-address-capacity") }
        guard !pending.values.contains(where: { $0.offer.remoteHello.deviceId == remote.deviceId || $0.offer.remoteHello.publicKeyBase64 == remote.publicKeyBase64 }) else { throw PairingAdmissionFailure(category: "pairing-duplicate") }
        let secret = try DropLinkCrypto.deriveSecret(privateKey: key, local: local, remote: remote)
        let offer = PairingOffer(sessionId: UUID(), remoteHello: remote, localHello: local, sas: DropLinkCrypto.sas(secret: secret, first: local, second: remote), expiresAtUtc: now.addingTimeInterval(300))
        pending[offer.sessionId] = Pending(offer: offer, secret: secret, address: address); scheduleExpiry(); return offer
    }
    public func offer(_ id: UUID, now: Date = Date()) -> PairingOffer? {
        guard let value = pending[id] else { return nil }
        guard value.offer.expiresAtUtc > now else { pending[id] = nil; scheduleExpiry(); return nil }
        return value.offer
    }
    public func confirm(_ request: PairingConfirmationRequest, now: Date = Date()) async throws -> PairingConfirmationResponse {
        try requireActive()
        guard let value = pending.removeValue(forKey: request.sessionId) else { throw DropSpaceError.invalid("Pairing session is not available") }
        scheduleExpiry()
        let peer = value.offer.remoteHello.deviceId
        if value.offer.expiresAtUtc <= now { return .init(trusted: false, peerId: peer, state: .expired, errorCategory: "expired") }
        if request.decision == PairingDecision.cancel.rawValue { return .init(trusted: false, peerId: peer, state: .cancelled, errorCategory: "cancelled") }
        guard request.decision == PairingDecision.confirm.rawValue, request.confirmed, request.sas == value.offer.sas else {
            return .init(trusted: false, peerId: peer, state: .rejected, errorCategory: "sas-mismatch-or-rejected")
        }
        let commit = commit, id = request.sessionId
        let operation = Task { try Task.checkCancellation(); try await commit(value.offer.remoteHello, value.secret) }
        commits[id] = operation; defer { commits[id] = nil }
        try await withTaskCancellationHandler { try await operation.value } onCancel: { operation.cancel() }
        try requireActive()
        return .init(trusted: true, peerId: peer, state: .trusted)
    }
    public func shutdown() async {
        disposed = true; expiry?.cancel(); let timer = expiry; expiry = nil
        pending.removeAll(); rates.removeAll()
        let tasks = Array(commits.values); for task in tasks { task.cancel() }
        await timer?.value; for task in tasks { _ = await task.result }; commits.removeAll()
    }
    private func requireActive() throws { try Task.checkCancellation(); guard !disposed else { throw CancellationError() } }
    private func scheduleExpiry() {
        expiry?.cancel(); expiry = nil
        guard !disposed, let next = pending.values.map(\.offer.expiresAtUtc).min() else { return }
        expiry = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(max(0, next.timeIntervalSinceNow))) } catch { return }
            await self?.expire()
        }
    }
    private func expire() { pending = pending.filter { $0.value.offer.expiresAtUtc > Date() }; scheduleExpiry() }
}
