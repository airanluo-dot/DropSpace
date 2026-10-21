import Foundation

public struct Localizer: Sendable {
    public let language: String
    public init(_ choice: String = "system", preferred: [String] = Locale.preferredLanguages) {
        language = choice == "system" ? Self.match(preferred) : choice
    }
    public func callAsFunction(_ key: String, _ arguments: String...) -> String {
        var value = Self.catalog[key]?[language] ?? Self.catalog[key]?["en-US"] ?? key
        for (index,argument) in arguments.enumerated() { value = value.replacingOccurrences(of: "{\(index)}",with: argument) }
        return value
    }
    public static func match(_ preferred: [String]) -> String {
        for raw in preferred {
            let tag = raw.replacingOccurrences(of: "_",with: "-").lowercased()
            if let exact = AppSettings.languages.first(where: { $0.lowercased() == tag }) { return exact }
            if tag.hasPrefix("zh") {
                if tag.contains("hant") || tag.contains("-tw") || tag.contains("-hk") || tag.contains("-mo") { return "zh-TW" }
                return "zh-CN"
            }
            if let match = AppSettings.languages.first(where: { $0.lowercased().split(separator: "-").first == tag.split(separator: "-").first }) { return match }
        }
        return "en-US"
    }
    private static let catalog: [String: [String: String]] = {
        guard let url = Bundle.module.url(forResource: "Localizable",withExtension: "xcstrings",subdirectory: "Resources"),
              let data = try? Data(contentsOf: url),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let strings = root["strings"] as? [String: [String: Any]] else { return [:] }
        return strings.mapValues { entry in
            var output: [String: String] = [:]
            for (locale,value) in entry["localizations"] as? [String: [String: Any]] ?? [:] {
                output[locale] = (value["stringUnit"] as? [String: String])?["value"]
            }
            return output
        }
    }()
}
