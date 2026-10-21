import Foundation

public enum ActionOutputNames {
    public static let maximumCollisionAttempts = 1_000

    /// Retain the baseline's portable stem rules. macOS additionally has a
    /// byte-based component limit; reserve room for the suffix and extension.
    public static func stem(_ value: String, extension suffix: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let source = trimmed.isEmpty ? "DropSpace export" : trimmed
        let invalid = Set("\\/:*?\"<>|".unicodeScalars)
        var result = String(String.UnicodeScalarView(source.unicodeScalars.filter {
            $0.value >= 32 && !invalid.contains($0)
        }))
        while result.last == " " || result.last == "." { result.removeLast() }
        if result.isEmpty { result = "DropSpace export" }
        let base = result.split(separator: ".", maxSplits: 1).first.map(String.init)?.uppercased() ?? ""
        let devices = ["CON", "PRN", "AUX", "NUL"] + (1...9).flatMap { ["COM\($0)", "LPT\($0)"] }
        if devices.contains(base) { result = "_" + result }
        var bounded = ""
        let maximumBytes = max(1, 255 - suffix.utf8.count - 1 - " (999)".utf8.count)
        for scalar in result.unicodeScalars {
            let next = bounded + String(scalar)
            guard next.utf16.count <= 160, next.utf8.count <= maximumBytes else { break }
            bounded = next
        }
        while bounded.last == " " || bounded.last == "." { bounded.removeLast() }
        return bounded.isEmpty ? "DropSpace export" : bounded
    }
}
