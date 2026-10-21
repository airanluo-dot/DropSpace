import Foundation

public enum DownloadRequestRules {
    private static let reserved: Set<String> = Set(["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"])
        .union((1...9).flatMap { ["COM\($0)", "LPT\($0)"] })
    private static let invalid = Set("<>:\"/\\|?*".unicodeScalars)
    private static func trim(_ value: String) -> String { value.trimmingCharacters(in: .whitespacesAndNewlines) }
    public static func sanitize(_ value: String, fallback: String = "video") -> String {
        var candidate = trim(value); if candidate.isEmpty { candidate = fallback }
        candidate = String(String.UnicodeScalarView(candidate.unicodeScalars.map {
            invalid.contains($0) || $0.properties.generalCategory == .control ? UnicodeScalar(95) : $0
        })).trimmingCharacters(in: .whitespacesAndNewlines)
        while candidate.last == "." || candidate.last == " " { candidate.removeLast() }
        if candidate.isEmpty { candidate = fallback }
        if reserved.contains(trim(candidate.components(separatedBy: ".")[0]).uppercased()) { candidate = "_" + candidate }
        // Preserve valid Unicode at the Windows 180 UTF-16-unit limit. Splitting a
        // surrogate pair would create a name that native filesystem APIs cannot represent.
        if candidate.utf16.count > 180 {
            var units = Array(candidate.utf16.prefix(180))
            if let last = units.last, (0xD800...0xDBFF).contains(last) { units.removeLast() }
            candidate = String(decoding: units, as: UTF16.self)
            while candidate.last == "." || candidate.last == " " { candidate.removeLast() }
        }
        return candidate
    }
    public static func validate(source: URL, directory: URL, name: String?) throws -> String {
        guard source.absoluteString.utf16.count <= 16384, ["http", "https"].contains(source.scheme?.lowercased() ?? ""),
              source.host?.isEmpty == false, source.user == nil, source.password == nil else { throw DropSpaceError.invalid("Download URL") }
        guard directory.isFileURL, directory.path.hasPrefix("/"), !directory.path.contains("\0") else { throw DropSpaceError.invalid("Absolute download directory required") }
        let encoded = URLComponents(url: source, resolvingAgainstBaseURL: true)?.percentEncodedPath.components(separatedBy: "/").last ?? ""
        let suggested = sanitize(encoded.removingPercentEncoding ?? encoded, fallback: "download.bin")
        let input = name.map(trim).flatMap { $0.isEmpty ? nil : $0 } ?? suggested
        guard input != ".", input != "..", sanitize(input) == input else { throw DropSpaceError.invalid("Invalid download filename") }
        return input
    }
}
