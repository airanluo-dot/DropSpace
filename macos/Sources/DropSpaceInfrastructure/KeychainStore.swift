import Foundation
import Security
import DropSpaceCore

public actor KeychainStore {
    private let service: String
    public init(service: String = "io.github.airanluo-dot.DropSpace.macOS") { self.service = service }
    public func read(_ account: String) throws -> Data? {
        var query = base(account); query[kSecReturnData as String] = true; query[kSecMatchLimit as String] = kSecMatchLimitOne
        var value: CFTypeRef?
        let result = SecItemCopyMatching(query as CFDictionary,&value)
        if result == errSecItemNotFound { return nil }; guard result == errSecSuccess else { throw failure(result) }
        guard let data = value as? Data else { throw DropSpaceError.storage("Invalid Keychain data") }; return data
    }
    public func save(_ data: Data,account: String) throws {
        guard data.count <= 1024 * 1024,!account.isEmpty,account.count <= 256 else { throw DropSpaceError.limit("Keychain item") }
        let query = base(account)
        let update = SecItemUpdate(query as CFDictionary,[kSecValueData as String:data] as CFDictionary)
        if update == errSecItemNotFound {
            var item = query; item[kSecValueData as String] = data; item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            let result = SecItemAdd(item as CFDictionary,nil); guard result == errSecSuccess else { throw failure(result) }
        } else if update != errSecSuccess { throw failure(update) }
    }
    public func remove(_ account: String) throws { let result = SecItemDelete(base(account) as CFDictionary); guard result == errSecSuccess || result == errSecItemNotFound else { throw failure(result) } }
    private func base(_ account: String) -> [String:Any] { [kSecClass as String:kSecClassGenericPassword,kSecAttrService as String:service,kSecAttrAccount as String:account,kSecAttrSynchronizable as String:false] }
    private func failure(_ code: OSStatus) -> DropSpaceError { .storage("Keychain: " + (SecCopyErrorMessageString(code,nil) as String? ?? String(code))) }
}
