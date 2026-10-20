import Foundation

/// Frozen NativeIslandSettingsPolicy and MusicPage source-selection semantics.
/// Platform application identities replace AUMIDs, not the allow-list rules.
public enum MediaSourcePolicy {
    public static func normalized(_ sources: [String], limit: Int = 128) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        guard limit > 0 else { return result }
        for source in sources {
            guard !source.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  source.utf16.count <= 512, seen.insert(source.lowercased()).inserted else { continue }
            result.append(source)
            if result.count == limit { break }
        }
        return result
    }
    public static func restricted(_ settings: IslandActivitySettings) -> Bool {
        settings.useMediaSourceAllowList || !settings.allowedMediaSourceAppIDs.isEmpty
    }
    public static func allows(_ source: String, settings: IslandActivitySettings) -> Bool {
        !restricted(settings) || settings.allowedMediaSourceAppIDs.contains { $0.lowercased() == source.lowercased() }
    }
    public static func displayed(available: [String], settings: IslandActivitySettings) -> [String] {
        normalized(available + settings.allowedMediaSourceAppIDs, limit: 512)
    }
    public static func setAll(_ all: Bool, available: [String], settings: inout IslandActivitySettings) {
        settings.useMediaSourceAllowList = !all
        settings.allowedMediaSourceAppIDs = all ? [] : normalized(available)
    }
    public static func setSelected(_ source: String, selected: Bool, displayed: [String], settings: inout IslandActivitySettings) {
        var values = restricted(settings) ? settings.allowedMediaSourceAppIDs : displayed
        values.removeAll { $0.lowercased() == source.lowercased() }
        if selected { values.append(source) }
        settings.useMediaSourceAllowList = true
        settings.allowedMediaSourceAppIDs = normalized(values)
    }
}
