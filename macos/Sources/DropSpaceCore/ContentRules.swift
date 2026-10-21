import Foundation

/// Direct rule mapping of Windows ContentClassifier/SearchNormalizer at the pinned baseline.
public enum ContentRules {
    public static func normalizedBody(_ value: String) throws -> String {
        let value = value.replacingOccurrences(of:"\r\n",with:"\n")
        guard !value.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty else { throw DropSpaceError.invalid("Clipboard text cannot be empty") }; return value
    }
    public static func classify(_ value: String) -> ItemKind {
        let value = value.trimmingCharacters(in:.whitespacesAndNewlines)
        if actionableURL(value) != nil { return .url }
        let component = #"(?:25[0-5]|2[0-4]\d|1?\d?\d)"#
        if value.range(of:#"^#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$"#,options:.regularExpression) != nil || value.range(of:"^rgba?\\(\\s*"+component+"\\s*,\\s*"+component+"\\s*,\\s*"+component+#"(?:\s*,\s*(?:0|1|0?\.\d+))?\s*\)$"#,options:[.regularExpression,.caseInsensitive]) != nil { return .color }
        if (value.hasPrefix("{") && value.hasSuffix("}") || value.hasPrefix("[") && value.hasSuffix("]")),let data = value.data(using:.utf8),(try? JSONSerialization.jsonObject(with:data)) != nil { return .code }
        let signals = ["=>","{","}",";","public ","private ","class ","const ","function ","import ","SELECT "].filter { value.contains($0) }.count
        if signals >= 2 || (value.contains("\n") && value.range(of:#"(?m)^(?: {2,}|\t)\S"#,options:.regularExpression) != nil) { return .code }
        return .text
    }
    public static func actionableURL(_ value: String) -> URL? {
        guard !value.contains(where: { $0.isWhitespace }),let url = URL(string:value),["http","https"].contains(url.scheme?.lowercased() ?? ""),url.host != nil else { return nil }; return url
    }
    public static func title(_ normalized: String) -> String {
        if let host = actionableURL(normalized.trimmingCharacters(in:.whitespacesAndNewlines))?.host { return utf16Prefix(host,maximum:160) }
        let first = String(normalized.prefix { $0 != "\n" }).trimmingCharacters(in:.whitespacesAndNewlines)
        if first.isEmpty { return "Text" }; if first.utf16.count <= 160 { return first }
        return utf16Prefix(first,maximum:159) + "…"
    }
    public static func normalizeSearch(_ value: String) -> String {
        let decomposed = value.decomposedStringWithCompatibilityMapping
        var result = String.UnicodeScalarView(),space = false
        for rune in decomposed.unicodeScalars {
            if rune.properties.generalCategory == .nonspacingMark || rune.properties.generalCategory == .spacingMark { continue }
            if CharacterSet.whitespacesAndNewlines.contains(rune) { if !space { result.append(" ") }; space = true }
            else { space = false; result.append(contentsOf:String(rune).lowercased().unicodeScalars) }
        }
        return String(result).trimmingCharacters(in:.whitespacesAndNewlines).precomposedStringWithCanonicalMapping
    }
    public static func searchText(title: String,body: String?) -> String { utf16Prefix(normalizeSearch(title+" "+(body ?? "")),maximum:65536) }
    private static func utf16Prefix(_ value: String,maximum: Int) -> String {
        var result = String.UnicodeScalarView(),length = 0
        for scalar in value.unicodeScalars { let size = scalar.value > 0xffff ? 2 : 1; if length + size > maximum { break }; result.append(scalar); length += size }
        return String(result)
    }
}
