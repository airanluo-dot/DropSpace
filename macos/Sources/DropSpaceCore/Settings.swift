import Foundation

public enum Theme: String, Codable, CaseIterable, Sendable { case system, light, dark }
public enum Motion: String, Codable, CaseIterable, Sendable { case system, full, reduced }
public enum DragWakeMode: String, Codable, CaseIterable, Sendable { case smart, classicTopEdge, disabled }
public enum CloseBehavior: String, Codable, CaseIterable, Sendable { case hideToMenuBar, exit }
public enum LyricsProvider: String, Codable, CaseIterable, Sendable { case netEase, qqMusic, kugou, lrclib, amll, localLrc }
public enum LyricsTarget: String, Codable, CaseIterable, Sendable { case en, zh }
public enum UpdateChannel: String, Codable, CaseIterable, Sendable { case stable, beta }
public enum ClipboardSyncMode: Int, Codable, CaseIterable, Sendable { case off, manual, textAndURL, textURLAndImage }
public enum QuickAction: String, Codable, CaseIterable, Sendable {
    case open, showInFolder, copy, copyPath, preview
    case hashSha256, compressZip, generateQr, resizeImage, convertImage, stripMetadata, sendToDevice, nearbyLink, internetLink
}
public struct QuickActionPreference: Codable, Equatable, Sendable {
    public var automatic = true
    // Preserve unfilled positions, including a gap before a later action. Old
    // development files containing a compact array decode without data loss.
    public var actions: [QuickAction?] = []
    public init() {}
}
public struct MonitorPlacement: Codable, Equatable, Sendable {
    public var custom = false
    public var x: Double = 0
    public var y: Double = 0
    public init(custom: Bool = false, x: Double = 0, y: Double = 0) { self.custom = custom; self.x = x; self.y = y }
}
public struct IslandActivitySettings: Codable, Equatable, Sendable {
    public var enableMediaActivity = true
    public var showArtwork = true
    public var showProgress = true
    public var showSpectrum = true
    public var showLyricsInCompact = true
    public var compactDynamicWidth = true
    public var allowedMediaSourceAppIDs: [String] = []
    public var useMediaSourceAllowList = false
    public init() {}
}
public enum LyricsTranslationScrollMode: String, Codable, CaseIterable, Sendable {
    case adaptive, baseline
}
public struct LyricsSettings: Codable, Equatable, Sendable {
    public var cacheMaximumBytes: Int64 = 1_000_000_000
    public var aiTranslationEnabled = false
    public var gpuAccelerationEnabled = true
    public var gpuBackend = "automatic" // Native replacement for Automatic/Vulkan/Cuda: automatic/metal/cpu.
    public var aiModelID = "hy-mt2-18-q8-plain-beta"
    public var glowMode = "off"
    public var simplifiedGlow = false
    public var fontSize: Double = 16
    public var enabled = true
    public var localOnly = false
    public var provider: LyricsProvider = .netEase
    public var backupProvider: LyricsProvider?
    public var searchRemainingProviders = true
    public var secondaryLyrics = false
    public var showAiLyricsLabel = true
    public var wordSyncedHighlighting = true
    public var delayMilliseconds = 0
    public var scrolling = true
    public var translationScrollMode = LyricsTranslationScrollMode.adaptive
    public var scrollingMaxWidth = 260
    public var unlimitedScrollingWidth = false
    public var localLrcDirectory = ""
    public init() {}
}
public struct IslandAppearanceSettings: Codable, Equatable, Sendable {
    public var theme: Theme = .system
    public var resident = false
    public var showLogoWhenIdle = false
    public var forceShowOverFullscreen = false
    public var hideDelayMilliseconds = 3000
    public var compactScale: Double = 1
    public var expandedScale: Double = 1
    public var rightClickHoldToMove = true
    public init() {}
}
public struct SystemActivitySettings: Codable, Equatable, Sendable {
    public var showApplicationNotifications = false
    public var showVolumeChanges = false
    public init() {}
}
public struct AppSettings: Codable, Equatable, Sendable {
    public var version = 1
    public var islandActivity = IslandActivitySettings()
    public var lyrics = LyricsSettings()
    public var islandAppearance = IslandAppearanceSettings()
    public var islandContentPriority = "music"
    public var systemActivities = SystemActivitySettings()
    public var widgets = WidgetSettings()
    public var maxDownloadConnections = 64
    public var maxConcurrentDownloads = 2
    public var downloadSpeedLimitBytesPerSecond: Int64 = 0
    public var defaultDownloadDirectory = ""
    public var privacyChoicesCompleted = false
    public var clipboardPaused = false
    public var captureImages = true
    public var captureFiles = true
    public var captureFolders = true
    public var startAtLogin = true
    public var retentionDays = 30
    public var retentionItemCount = 1000
    public var maxImageBytes: Int64 = 25 * 1024 * 1024
    public var maxImagePixels: Int64 = 50_000_000
    public var maxClipboardFileBytes: Int64 = 2 * 1024 * 1024 * 1024
    public var maxClipboardFileTotalBytes: Int64 = 8 * 1024 * 1024 * 1024
    public var maxClipboardFileItems = 100
    public var maxTextCharacters = 2 * 1024 * 1024
    public var theme: Theme = .system
    public var language = "system"
    public var lyricsTranslationTarget: LyricsTarget = .en
    public var closeBehavior: CloseBehavior = .hideToMenuBar
    public var closeExplanationShown = false
    public var launchPage = "space"
    public var overlayMotion: Motion = .system
    public var overlayMonitor = "automatic"
    public var fileDragWakeMode: DragWakeMode = .smart
    public var overlayPlacements: [String: MonitorPlacement] = [:]
    public var quickActionPreferences: [String: QuickActionPreference] = ["file": .init(), "image": .init(), "text": .init(), "url": .init()]
    public var quickPanelHotkey = "Cmd+Shift+Space"
    public var keyboardShortcuts: [String: String] = [:]
    public var smartDragExcludedProcesses: [String] = []
    public var autoCheckForUpdates = true
    public var autoDownloadUpdates = true
    public var autoInstallUpdates = false
    public var updateChannel: UpdateChannel = .stable
    public var lastUpdateCheck: Date?
    public var enableDeviceHandoff = false
    public var enableCrossDeviceClipboard = false
    public var enableNearbySharing = false
    public var enableInternetSharing = false
    public var defaultClipboardSyncMode: ClipboardSyncMode = .off
    public var clipboardPeerModes: [String: ClipboardSyncMode] = [:]
    public init() {}
    public static let languages = ["en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "pt-BR", "ru-RU"]
    public static let shortcutDefaults = ["Space": "Cmd+1", "Clipboard": "Cmd+2", "Pinned": "Cmd+3", "Search": "Cmd+F", "SearchAlternate": "Cmd+L", "Settings": "Cmd+Shift+S", "SettingsAlternate": "Cmd+,", "Undo": "Cmd+Z"]
    public func shortcut(_ id: String) -> String { keyboardShortcuts[id] ?? Self.shortcutDefaults[id] ?? "" }
    public func validated() throws -> AppSettings {
        func require(_ ok: Bool, _ name: String) throws { if !ok { throw DropSpaceError.invalid(name) } }
        try require(version == 1, "Unsupported settings version")
        try require((1...256).contains(maxDownloadConnections) && (1...3).contains(maxConcurrentDownloads), "Download concurrency")
        try require((0...1_073_741_824).contains(downloadSpeedLimitBytesPerSecond), "Download bandwidth")
        try require(defaultDownloadDirectory.isEmpty || (defaultDownloadDirectory.hasPrefix("/") && defaultDownloadDirectory.count <= 32000 && !defaultDownloadDirectory.contains("\0")), "Download directory")
        try require((1...3650).contains(retentionDays) && (10...100_000).contains(retentionItemCount), "Clipboard retention")
        try require((1_048_576...268_435_456).contains(maxImageBytes) && (1_000_000...200_000_000).contains(maxImagePixels), "Image limit")
        try require((1_048_576...1_099_511_627_776).contains(maxClipboardFileBytes), "File limit")
        try require((1_048_576...4_398_046_511_104).contains(maxClipboardFileTotalBytes) && maxClipboardFileTotalBytes >= maxClipboardFileBytes, "Total file limit")
        try require((1...1000).contains(maxClipboardFileItems) && (1024...16_777_216).contains(maxTextCharacters), "Clipboard limit")
        try require(language == "system" || Self.languages.contains(language), "Interface language")
        try require(overlayPlacements.count <= 64 && overlayPlacements.allSatisfy { !$0.key.isEmpty && $0.key.count <= 256 && $0.value.x.isFinite && $0.value.y.isFinite && abs($0.value.x) <= 100_000 && abs($0.value.y) <= 100_000 }, "Monitor placement")
        try require(smartDragExcludedProcesses.count <= 128 && smartDragExcludedProcesses.allSatisfy { !$0.isEmpty && $0.count <= 260 && !$0.contains("\0") }, "Excluded applications")
        try require(clipboardPeerModes.count <= 128 && clipboardPeerModes.keys.allSatisfy { UUID(uuidString: $0) != nil }, "Clipboard peers")
        try require(quickActionPreferences.count <= 4 && quickActionPreferences.allSatisfy {
            let selected = $0.value.actions.compactMap { $0 }
            return ["file", "image", "text", "url"].contains($0.key) && $0.value.actions.count <= 3 && Set(selected).count == selected.count
        }, "Quick actions")
        try require(Hotkey.parse(quickPanelHotkey) != nil, "Global shortcut")
        try require(keyboardShortcuts.keys.allSatisfy { Self.shortcutDefaults[$0] != nil }, "Unknown shortcut")
        let gestures = try Self.shortcutDefaults.keys.map { id -> String in
            guard let chord = Hotkey.parse(shortcut(id)) else { throw DropSpaceError.invalid("Shortcut: " + id) }
            return chord.canonical
        }
        try require(Set(gestures).count == gestures.count, "Duplicate shortcut")
        try require(!gestures.contains(Hotkey.parse(quickPanelHotkey)!.canonical), "Global and window shortcut conflict")
        var result = self
        if !["off","aiLyrics","music"].contains(lyrics.glowMode) { result.lyrics.glowMode = "off" }
        if lyrics.localLrcDirectory.utf16.count > 32767 { result.lyrics.localLrcDirectory = "" }
        if !["music","space"].contains(islandContentPriority) { result.islandContentPriority = "music" }
        result.lyrics.cacheMaximumBytes = min(10_000_000_000, max(0, lyrics.cacheMaximumBytes))
        result.lyrics.fontSize = lyrics.fontSize.isFinite ? min(28, max(12, lyrics.fontSize)) : 16
        result.lyrics.delayMilliseconds = min(30_000, max(-30_000, lyrics.delayMilliseconds))
        result.lyrics.scrollingMaxWidth = min(600, max(80, lyrics.scrollingMaxWidth))
        if lyrics.provider == .localLrc { result.lyrics.provider = .netEase }
        if lyrics.backupProvider == .localLrc || lyrics.backupProvider == result.lyrics.provider { result.lyrics.backupProvider = nil }
        if !["hy-mt2-18-q8-plain-beta", "hy-mt2-7b-q8-plain-beta"].contains(lyrics.aiModelID) { result.lyrics.aiModelID = "hy-mt2-18-q8-plain-beta" }
        if !["automatic", "metal", "cpu"].contains(lyrics.gpuBackend) { result.lyrics.gpuBackend = "automatic" }
        result.islandAppearance.hideDelayMilliseconds = min(10000, max(0, Int((Double(islandAppearance.hideDelayMilliseconds) / 1000).rounded()) * 1000))
        result.islandAppearance.compactScale = islandAppearance.compactScale.isFinite ? min(2, max(0.5, islandAppearance.compactScale)) : 1
        result.islandAppearance.expandedScale = islandAppearance.expandedScale.isFinite ? min(2, max(0.5, islandAppearance.expandedScale)) : 1
        result.islandActivity.allowedMediaSourceAppIDs = MediaSourcePolicy.normalized(islandActivity.allowedMediaSourceAppIDs)
        result.widgets.layout = widgets.layout.normalized()
        result.quickPanelHotkey = Hotkey.parse(quickPanelHotkey)!.canonical
        return result
    }
}
public struct Hotkey: Equatable, Sendable {
    public let modifiers: [String]
    public let key: String
    public var canonical: String { (modifiers + [key]).joined(separator: "+") }
    public static func parse(_ value: String) -> Hotkey? {
        guard value.count <= 128 else { return nil }
        let parts = value.split(separator: "+").map { $0.trimmingCharacters(in: .whitespaces) }
        let order = ["Cmd", "Ctrl", "Alt", "Shift"]
        var modifiers = Set<String>(); var key: String?
        for part in parts {
            if let mod = order.first(where: { $0.lowercased() == part.lowercased() }) {
                guard modifiers.insert(mod).inserted else { return nil }
            } else {
                guard key == nil else { return nil }
                let k = part.lowercased() == "space" ? "Space" : part.uppercased()
                guard k == "Space" || k == "," || (k.utf8.count == 1 && k.range(of: "^[A-Z0-9]$", options: .regularExpression) != nil) || (k.hasPrefix("F") && (1...24).contains(Int(k.dropFirst()) ?? 0)) else { return nil }
                key = k
            }
        }
        guard let key, !modifiers.isEmpty else { return nil }
        return Hotkey(modifiers: order.filter { modifiers.contains($0) }, key: key)
    }
}
