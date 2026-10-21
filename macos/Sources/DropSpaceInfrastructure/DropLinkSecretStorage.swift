import Foundation

/// The peer transaction owns metadata while this boundary owns protected bytes.
/// Production uses Keychain; deterministic IO faults can be exercised independently.
public protocol DropLinkSecretStorage: Sendable {
    func read(_ account: String) async throws -> Data?
    func save(_ data: Data, account: String) async throws
    func remove(_ account: String) async throws
}
extension KeychainStore: DropLinkSecretStorage {}
