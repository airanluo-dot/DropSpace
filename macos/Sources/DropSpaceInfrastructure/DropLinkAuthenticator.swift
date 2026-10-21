import Foundation
import CryptoKit
import DropSpaceCore

/// Authentication before JSON decoding or body buffering. The supplied lookup
/// must return a secret only for a durably trusted peer, never PairingPending.
public struct DropLinkAuthenticator: Sendable {
    private let lookup: @Sendable (UUID) async throws -> SymmetricKey?
    private let nonces: DropLinkNonceCache
    public init(nonces: DropLinkNonceCache = DropLinkNonceCache(), lookup: @escaping @Sendable (UUID) async throws -> SymmetricKey?) {
        self.lookup = lookup; self.nonces = nonces
    }
    public func admit(_ head: DropLinkHTTPHead, address: String) async throws -> DropLinkHTTPAdmission {
        if DropLinkRoute.isPairing(head.path) { return .init(maximumBodyBytes: 65536) }
        let maximum = DropLinkRoute.bodyLimit(head.path)
        guard DropLinkRoute.requiresAuthentication(head.path) else { return .init(maximumBodyBytes: maximum) }
        if let value = head.headers["content-length"], let length = Int64(value), length > maximum { return .init(maximumBodyBytes: maximum, rejection: .init(status: 413)) }
        let fields = head.headers
        guard let text = fields["x-droplink-device"], let peer = UUID(uuidString: text), peer != .zero,
              let nonce = fields["x-droplink-nonce"], let auth = fields["x-droplink-auth"], auth.utf16.count == 44,
              let hash = fields["x-droplink-body-sha256"], hash.utf8.count == 64,
              hash.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }),
              let secret = try await lookup(peer),
              DropLinkCrypto.verifyAuthentication(auth, secret: secret, method: head.method, path: head.path, nonce: nonce, bodyHash: hash),
              await nonces.reserve(peer: peer, nonce: nonce) else { return .init(maximumBodyBytes: maximum, rejection: .init(status: 401)) }
        let cache = nonces
        return .init(maximumBodyBytes: maximum, peerID: peer, expectedBodyHash: hash, finish: { accepted in
            if !accepted { await cache.release(peer: peer, nonce: nonce) }
        })
    }
}
