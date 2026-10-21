import Foundation
import CryptoKit
import DropSpaceCore

/// Windows Core/Transfer/HandoffMessagePolicy.cs at the fixed Beta 7 baseline.
public enum HandoffMessagePolicy {
    public static let maximumTextBytes = 1024 * 1024, maximumURLBytes = 32 * 1024
    public static func create(sender: UUID, name: String, kind: HandoffMessageKind, payload: String, label: String? = nil, now: Date = Date()) throws -> HandoffMessage {
        let normalized = try kind == .url ? normalizeURL(payload) : normalizeText(payload)
        let name = trim(name.replacingOccurrences(of: "\0", with: " "))
        let label = label.map { trim($0.replacingOccurrences(of: "\0", with: " ")) }.flatMap { $0.isEmpty ? nil : $0 }
        let data = Data(normalized.utf8)
        let message = HandoffMessage(sender: sender, name: name, kind: kind, bytes: Int64(data.count), hash: Digests.sha256(data), payload: normalized, label: label, now: now)
        try validate(message); return message
    }
    public static func validate(_ message: HandoffMessage) throws {
        guard message.sessionId != .zero, message.senderDeviceId != .zero, !trim(message.senderDisplayName).isEmpty,
              message.senderDisplayName.utf16.count <= 64,
              !message.senderDisplayName.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }),
              message.byteLength > 0, message.sha256.utf8.count == 64,
              message.sha256.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }),
              DropLinkHandoffJSON.utcDate(message.createdAtUtc) != nil,
              let kind = HandoffMessageKind(rawValue: message.kind) else { throw DropSpaceError.invalid("Handoff metadata") }
        let normalized = try kind == .url ? normalizeURL(message.utf8Payload) : normalizeText(message.utf8Payload)
        let bytes = Data(message.utf8Payload.utf8)
        // Swift String equality normalizes Unicode; the wire contract is ordinal.
        guard normalized.utf8.elementsEqual(message.utf8Payload.utf8), bytes.count == message.byteLength,
              bytes.count <= (kind == .url ? maximumURLBytes : maximumTextBytes) else { throw DropSpaceError.invalid("Handoff payload or size") }
        let actual = Array(SHA256.hash(data: bytes)), declared = Array(message.sha256.lowercased().utf8)
        let hex = Array("0123456789abcdef".utf8)
        var difference: UInt8 = 0
        for (index, byte) in actual.enumerated() { difference |= hex[Int(byte >> 4)] ^ declared[index * 2]; difference |= hex[Int(byte & 15)] ^ declared[index * 2 + 1] }
        guard difference == 0 else { throw DropSpaceError.invalid("Handoff content hash") }
        if let label = message.displayLabel {
            guard !trim(label).isEmpty, !label.contains("\0"), label.utf16.count <= 160, trim(label).utf8.elementsEqual(label.utf8) else { throw DropSpaceError.invalid("Handoff display label") }
        }
    }
    public static func normalizeText(_ value: String) throws -> String {
        let value = value.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        guard !trim(value).isEmpty else { throw DropSpaceError.invalid("Handoff text cannot be empty") }; return value
    }
    public static func normalizeURL(_ value: String) throws -> String {
        let split = value.firstIndex(where: { $0 == "?" || $0 == "#" }) ?? value.endIndex
        let slashes = value[..<split].replacingOccurrences(of: "\\", with: "/") + value[split...]
        guard !value.unicodeScalars.contains(where: whitespace), let components = URLComponents(string: slashes),
              let scheme = components.scheme?.lowercased(), ["http", "https"].contains(scheme),
              let rawHost = components.host, !rawHost.isEmpty else { throw DropSpaceError.invalid("Handoff URL must be absolute HTTP(S)") }
        let host = canonicalIPv4(rawHost) ?? rawHost.lowercased().precomposedStringWithCanonicalMapping
        var authority = ""
        if let user = components.percentEncodedUser {
            authority = unescapeUnreserved(user)
            if let password = components.percentEncodedPassword { authority += ":" + unescapeUnreserved(password) }
            authority += "@"
        }
        authority += host
        if let port = components.port {
            guard (0...65535).contains(port) else { throw DropSpaceError.invalid("Handoff URL port") }
            if port != (scheme == "https" ? 443 : 80) { authority += ":\(port)" }
        }
        let path = unescapeUnreserved(components.percentEncodedPath)
        var segments: [Substring] = []
        let parts = path.split(separator: "/", omittingEmptySubsequences: false)
        for (index, part) in parts.enumerated() {
            if part == "." { if index == parts.count - 1 { segments.append("") }; continue }
            if part == ".." {
                if segments.count > 1 { segments.removeLast() }
                if index == parts.count - 1 { segments.append("") }; continue
            }
            segments.append(part)
        }
        let normalizedPath = segments.joined(separator: "/")
        var result = scheme + "://" + authority + (normalizedPath.isEmpty ? "/" : normalizedPath)
        if let query = components.percentEncodedQuery { result += "?" + unescapeUnreserved(query) }
        if let fragment = components.percentEncodedFragment { result += "#" + unescapeUnreserved(fragment) }
        return result
    }
    private static func unescapeUnreserved(_ value: String) -> String {
        let bytes = Array(value.utf8); var result: [UInt8] = []; var index = 0
        while index < bytes.count {
            if bytes[index] == 37, index + 2 < bytes.count, let byte = UInt8(String(decoding: bytes[(index + 1)...(index + 2)], as: UTF8.self), radix: 16),
               (65...90).contains(byte) || (97...122).contains(byte) || (48...57).contains(byte) || [45, 46, 95, 126].contains(byte) {
                result.append(byte); index += 3
            } else { result.append(bytes[index]); index += 1 }
        }
        return String(decoding: result, as: UTF8.self)
    }
    private static func canonicalIPv4(_ host: String) -> String? {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard (1...4).contains(parts.count) else { return nil }
        var address: UInt64 = 0
        for (index, part) in parts.enumerated() {
            let text = part.lowercased(), radix = text.hasPrefix("0x") ? 16 : text.count > 1 && text.hasPrefix("0") ? 8 : 10
            let digits = radix == 16 ? String(text.dropFirst(2)) : radix == 8 ? String(text.dropFirst()) : text
            guard !digits.isEmpty, let value = UInt64(digits, radix: radix) else { return nil }
            let bits = index == parts.count - 1 ? 8 * (5 - parts.count) : 8
            guard value < UInt64(1) << bits else { return nil }
            address = address << bits | value
        }
        return [24, 16, 8, 0].map { String((address >> $0) & 255) }.joined(separator: ".")
    }
    // System.Char.IsWhiteSpace uses Unicode separators plus these control values.
    private static func whitespace(_ value: Unicode.Scalar) -> Bool {
        (9...13).contains(value.value) || value.value == 0x85 || [.spaceSeparator, .lineSeparator, .paragraphSeparator].contains(value.properties.generalCategory)
    }
    private static func trim(_ value: String) -> String { String(String.UnicodeScalarView(value.unicodeScalars.drop(while: whitespace).reversed().drop(while: whitespace).reversed())) }
}

public actor DropLinkHandoffReplayCache {
    private struct Key: Hashable { let peer: UUID; let session: UUID }
    private var entries: [Key: Date] = [:]
    public init() {}
    public func reserve(peer: UUID, session: UUID, now: Date = Date()) -> Bool {
        guard peer != .zero, session != .zero else { return false }
        entries = entries.filter { now.timeIntervalSince($0.value) <= 600 }
        let key = Key(peer: peer, session: session)
        guard entries[key] == nil, entries.count < 4096, entries.keys.filter({ $0.peer == peer }).count < 256 else { return false }
        entries[key] = now; return true
    }
    public func clear() { entries.removeAll() }
}
