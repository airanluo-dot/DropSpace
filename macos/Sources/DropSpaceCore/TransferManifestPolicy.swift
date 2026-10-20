import Foundation

public enum TransferFilenameRules: Sendable { case windows, macOS }
public enum TransferManifestPolicy {
    public static func create(session: UUID = UUID(), items: [TransferItemManifest], limits: TransferLimits = .init(), now: Date = Date(), filenames: TransferFilenameRules = .windows) throws -> TransferManifest {
        try limits.validate()
        guard session != .zero, (1...limits.maxItems).contains(items.count) else { throw DropSpaceError.limit("Transfer item count or session") }
        var total: Int64 = 0, ids = Set<UUID>(), paths = Set<[UInt32]>()
        for item in items {
            guard item.id != .zero, (1...5).contains(item.kind), item.size >= 0,
                  !trim(item.relativePath).isEmpty, item.relativePath.utf16.count <= limits.maxRelativePathLength,
                  item.sha256.utf8.count == 64, item.sha256.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }),
                  let mime = item.mimeType, !trim(mime).isEmpty, mime.utf16.count <= 128 else { throw DropSpaceError.invalid("Transfer item manifest") }
            _ = try safeDisplayName(item.displayName)
            let path = try normalizeRelativePath(item.relativePath, maximum: limits.maxRelativePathLength, filenames: filenames)
            let chunks = item.size == 0 ? 0 : 1 + (item.size - 1) / Int64(limits.chunkBytes)
            guard chunks <= Int32.max, item.chunkCount == Int32(chunks), ids.insert(item.id).inserted,
                  paths.insert(ordinalIgnoreCaseKey(path)).inserted else { throw DropSpaceError.invalid("Transfer chunk count or duplicate destination") }
            let (sum, overflow) = total.addingReportingOverflow(item.size)
            guard !overflow else { throw DropSpaceError.limit("Transfer byte count overflow") }; total = sum
        }
        guard total <= limits.maxTotalBytes else { throw DropSpaceError.limit("Transfer total bytes") }
        return .init(session: session, items: items, total: total, now: now, chunkBytes: Int32(limits.chunkBytes))
    }
    public static func validate(_ manifest: TransferManifest, limits: TransferLimits = .init(), filenames: TransferFilenameRules = .windows) throws {
        guard manifest.protocol.major == 1, Int32(exactly: manifest.protocol.minor) != nil, let date = timestamp(manifest.createdAtUtc) else { throw DropSpaceError.invalid("Transfer protocol or timestamp") }
        var limits = limits; limits.chunkBytes = Int(manifest.chunkBytes)
        let expected = try create(session: manifest.sessionId, items: manifest.items, limits: limits, now: date, filenames: filenames)
        guard expected.totalBytes == manifest.totalBytes else { throw DropSpaceError.invalid("Transfer total") }
    }
    public static func normalizeRelativePath(_ path: String, maximum: Int = 1024, filenames: TransferFilenameRules = .windows) throws -> String {
        var value = path.replacingOccurrences(of: "\\", with: "/")
        let bytes = Array(value.utf8.prefix(2))
        guard !trim(value).isEmpty, !value.hasPrefix("/"), !(bytes.count == 2 && ((65...90).contains(bytes[0]) || (97...122).contains(bytes[0])) && bytes[1] == 58) else { throw DropSpaceError.invalid("Transfer path must be relative") }
        while value.hasSuffix("/") { value.removeLast() }
        guard !value.isEmpty, value.utf16.count <= maximum else { throw DropSpaceError.limit("Transfer relative path") }
        let segments = value.split(separator: "/", omittingEmptySubsequences: true)
        guard !segments.isEmpty, segments.allSatisfy({ segment in
            segment != "." && segment != ".." && !segment.unicodeScalars.contains { scalar in
                scalar.value == 0 || filenames == .windows && (scalar.value < 32 || "<>:\"|?*".unicodeScalars.contains(scalar))
            }
        }) else { throw DropSpaceError.invalid("Transfer path segment") }
        return segments.joined(separator: "/")
    }
    public static func safeDisplayName(_ name: String) throws -> String {
        let value = trim(name.replacingOccurrences(of: "\0", with: " ")).replacingOccurrences(of: "\\", with: "/")
        let leaf = value.components(separatedBy: "/").last ?? ""
        guard !trim(leaf).isEmpty, leaf != ".", leaf != "..", leaf.utf16.count <= 512 else { throw DropSpaceError.invalid("Transfer display name") }; return leaf
    }
    /// Preserve scalar identity (no Swift canonical-equivalence comparison or
    /// multi-scalar uppercase expansion). This mirrors ordinal wire uniqueness.
    public static func ordinalIgnoreCaseKey(_ value: String) -> [UInt32] {
        value.unicodeScalars.map { scalar in
            let upper = scalar.properties.uppercaseMapping.unicodeScalars
            guard upper.count == 1, let mapped = upper.first, !(scalar.value > 127 && mapped.value < 128) else { return scalar.value }
            return mapped.value
        }
    }
    public static func timestamp(_ text: String) -> Date? {
        TransferWireTimestamp.parse(text)
    }
    private static func whitespace(_ value: Unicode.Scalar) -> Bool {
        [.spaceSeparator, .lineSeparator, .paragraphSeparator].contains(value.properties.generalCategory) || (9...13).contains(value.value) || value.value == 0x85
    }
    private static func trim(_ value: String) -> String { String(String.UnicodeScalarView(value.unicodeScalars.drop(while: whitespace).reversed().drop(while: whitespace).reversed())) }
}
