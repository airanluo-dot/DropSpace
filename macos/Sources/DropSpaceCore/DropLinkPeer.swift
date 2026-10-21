import Foundation

public enum PeerTrustState: Int, Codable, Sendable { case unknown, pairingPending, trusted, unpairPending, blocked }
public struct DropLinkPeer: Identifiable, Codable, Sendable {
    public let id: UUID
    public var displayName: String
    public var platform: DevicePlatform
    public var identityFingerprint: String
    public var capabilities: PeerCapabilities
    public var trustState: PeerTrustState
    public var createdAtUtc: Date
    public var lastSeenAtUtc: Date?
    public init(hello: PairingHello, state: PeerTrustState, now: Date = Date()) {
        id = hello.deviceId; displayName = hello.displayName; platform = hello.platform; identityFingerprint = hello.identityFingerprint
        capabilities = hello.capabilities; trustState = state; createdAtUtc = now; lastSeenAtUtc = now
    }
}
