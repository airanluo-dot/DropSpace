import Foundation

public struct DropLinkDevice: Codable, Sendable, Identifiable {
    public let `protocol`: DropLinkVersion
    public let deviceId: UUID
    public let displayName: String
    public let platform: DevicePlatform
    public let capabilities: PeerCapabilities
    public let identityFingerprint: String
    public let endpoint: URL
    public var id: UUID { deviceId }
    public init(deviceID: UUID, name: String, platform: DevicePlatform, capabilities: PeerCapabilities, fingerprint: String, endpoint: URL, protocol version: DropLinkVersion = .init()) {
        self.protocol = version; deviceId = deviceID; displayName = name; self.platform = platform; self.capabilities = capabilities; identityFingerprint = fingerprint; self.endpoint = endpoint
    }
}
