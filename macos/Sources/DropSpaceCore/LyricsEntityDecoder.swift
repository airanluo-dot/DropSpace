import Foundation

/// HTML4 named/numeric entities used by the baseline's WebUtility.HtmlDecode.
/// No HTML renderer, browser engine or WebView is involved.
public enum LyricsEntityDecoder {
    public static func decode(_ text: String) -> String {
        var output = "", cursor = text.startIndex
        while cursor < text.endIndex {
            guard text[cursor] == "&" else { output.append(text[cursor]); cursor = text.index(after: cursor); continue }
            let start = text.index(after: cursor)
            guard let end = text[start...].firstIndex(where: { $0 == ";" || $0 == "&" }), text[end] == ";" else { output.append("&"); cursor = start; continue }
            let token = String(text[start..<end]); var scalar: Unicode.Scalar?
            if token.hasPrefix("#x") || token.hasPrefix("#X") {
                if let number = UInt32(token.dropFirst(2), radix: 16) { scalar = Unicode.Scalar(number) }
            } else if token.hasPrefix("#") {
                if let number = UInt32(token.dropFirst().trimmingCharacters(in: .whitespacesAndNewlines)) { scalar = Unicode.Scalar(number) }
            } else if let code = named[token] { scalar = Unicode.Scalar(code) }
            if let scalar { output.unicodeScalars.append(scalar); cursor = text.index(after: end) }
            else { output.append("&"); cursor = start }
        }
        return output
    }
    private static let named: [String: UInt32] = {
        let url = Bundle.module.url(forResource: "HTML4Entities", withExtension: "json", subdirectory: "Resources")!
        return try! JSONDecoder().decode([String: UInt32].self, from: Data(contentsOf: url))
    }()
}
