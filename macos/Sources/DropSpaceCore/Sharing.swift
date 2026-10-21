import Foundation

public struct ShareLimits: Sendable {
    public var nearbyTokenBytes = 24, nearbyTTLMinutes = 10, maxNearbyReceivers = 2, maxNearbyShares = 128
    public var internetMaxBytes: Int64 = 2 * 1024 * 1024 * 1024
    public var internetMaxItems = 100
    public static let chunkPlainBytes = 5 * 1024 * 1024, authTagBytes = 16, manifestNonceBytes = 12
    public static let maxObjectBytes = 6 * 1024 * 1024, maxManifestBytes = 512 * 1024, maxObjectNameLength = 180
    public init() {}
    public func validate() throws {
        guard (24...64).contains(nearbyTokenBytes), (1...60).contains(nearbyTTLMinutes), (1...10).contains(maxNearbyReceivers),
              (1...10000).contains(maxNearbyShares), (1...10_737_418_240).contains(internetMaxBytes), (1...1000).contains(internetMaxItems) else { throw DropSpaceError.invalid("Share limits") }
    }
}
public struct ShareDescriptor: Sendable, Identifiable {
    public let id: UUID, url: URL, expiresAt: Date, itemCount: Int, totalBytes: Int64, isEncrypted: Bool
    public init(id: UUID, url: URL, expiresAt: Date, itemCount: Int, totalBytes: Int64, isEncrypted: Bool) {
        self.id = id; self.url = url; self.expiresAt = expiresAt; self.itemCount = itemCount; self.totalBytes = totalBytes; self.isEncrypted = isEncrypted
    }
}
public struct NearbyShareSummary: Sendable, Identifiable {
    public let id: UUID, displayName: String, itemCount: Int, totalBytes: Int64, expiresAt: Date
    public init(descriptor: ShareDescriptor, name: String) {
        id = descriptor.id; displayName = name; itemCount = descriptor.itemCount; totalBytes = descriptor.totalBytes; expiresAt = descriptor.expiresAt
    }
}
public struct NearbyShareSnapshot: Sendable {
    public let revision: UInt64, items: [NearbyShareSummary]
    public init(revision: UInt64, items: [NearbyShareSummary]) { self.revision = revision; self.items = items }
}
public enum NearbySharePolicy {
    public static func guid(_ text: String) -> UUID? {
        var value = text
        if value.count == 38, (value.hasPrefix("{") && value.hasSuffix("}") || value.hasPrefix("(") && value.hasSuffix(")")) { value.removeFirst(); value.removeLast() }
        if let id = UUID(uuidString: value) { return id }
        guard value.utf8.count == 32, value.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else { return nil }
        let characters = Array(value)
        return UUID(uuidString: [String(characters[0..<8]), String(characters[8..<12]), String(characters[12..<16]), String(characters[16..<20]), String(characters[20..<32])].joined(separator: "-"))
    }
    public static func isPrivate(_ address: String) -> Bool {
        let components = address.split(separator: ".", omittingEmptySubsequences: false)
        let bytes = components.compactMap { UInt8($0) }
        return components.count == 4 && bytes.count == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && (16...31).contains(bytes[1]))
    }
    public static func range(_ value: String, total: Int64) -> ClosedRange<Int64>? {
        guard total > 0 else { return nil }
        let parts = value.split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
        func number(_ part: Substring) -> Int64? { !part.isEmpty && part.utf8.allSatisfy { (48...57).contains($0) } ? Int64(part) : nil }
        guard parts.count == 2 else { return nil }
        if parts[0].isEmpty {
            guard let suffix = number(parts[1]), suffix > 0 else { return nil }; return max(0, total - suffix)...(total - 1)
        }
        guard let start = number(parts[0]), start < total else { return nil }
        let end: Int64
        if parts[1].isEmpty { end = total - 1 }
        else { guard let requested = number(parts[1]) else { return nil }; end = min(requested, total - 1) }
        return end >= start ? start...end : nil
    }
    public static func disposition(_ name: String) -> String {
        let ascii = name.unicodeScalars.filter { (32...126).contains($0.value) && $0 != "\"" && $0 != "\\" }.map(String.init).joined()
        let allowed = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        return "attachment; filename=\"" + ascii + "\"; filename*=UTF-8''" + (name.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")
    }
    public static func html(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;").replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
    }
}
