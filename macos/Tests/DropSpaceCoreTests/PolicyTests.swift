import Testing
import Foundation
@testable import DropSpaceCore

@Test func settingsPreserveBaselineAndRejectInvalidLimits() throws {
    let defaults = AppSettings()
    #expect(defaults.retentionDays == 30 && defaults.retentionItemCount == 1000)
    #expect(defaults.maxDownloadConnections == 64 && defaults.maxConcurrentDownloads == 2)
    #expect(defaults.lyrics.aiTranslationEnabled == false && defaults.lyricsTranslationTarget == .en)
    #expect(try defaults.validated() == defaults)
    var invalid = defaults; invalid.maxClipboardFileTotalBytes = defaults.maxClipboardFileBytes - 1
    #expect(throws: (any Error).self) { try invalid.validated() }
    invalid = defaults; invalid.keyboardShortcuts["Clipboard"] = "Cmd+1"
    #expect(throws: (any Error).self) { try invalid.validated() }
}
@Test func retentionProtectsPinnedAndSpace() {
    let old = Date(timeIntervalSince1970: 0)
    let space = DropItem(source: .space,kind: .file,title: "source",createdAt: old)
    let pinned = DropItem(source: .clipboard,kind: .text,title: "pinned",createdAt: old,isPinned: true)
    let ordinary = DropItem(source: .clipboard,kind: .text,title: "expired",createdAt: old)
    #expect(ItemPolicy.expired([space,pinned,ordinary],cutoff: Date(),limit: 1) == [ordinary.id])
}
@Test func widgetsKeepSixteenKindsAndRejectCollision() {
    #expect(WidgetID.allCases.count == 16)
    var layout = WidgetLayout.default
    #expect(layout.normalized() == layout)
    let rejected = layout.move(.init(.clock,2,0,2,1))
    #expect(!rejected)
    let swapped = layout.move(.init(.clock,4,0,2,1))
    #expect(swapped)
    #expect(layout.expanded.first(where: { $0.id == .resourceUsage })?.column == 0)
}
@Test func languageAndHotkeyRules() {
    #expect(Localizer.match(["zh-Hant-HK"]) == "zh-TW")
    #expect(Localizer.match(["it-IT", "ja-JP"]) == "ja-JP")
    #expect(Localizer("zh-CN")("NavSpace.Content") != "NavSpace.Content")
    #expect(Hotkey.parse("shift+cmd+space")?.canonical == "Cmd+Shift+Space")
    #expect(Hotkey.parse("Cmd+Cmd+S") == nil)
}

@Test func pinnedContentClassifierAndUnicodeSearchRules() throws {
    #expect(try ContentRules.normalizedBody("one\r\ntwo") == "one\ntwo")
    #expect(throws:(any Error).self) { try ContentRules.normalizedBody(" \n\t") }
    #expect(ContentRules.classify("rgba(255, 0, 12, .5)") == .color)
    #expect(ContentRules.classify("rgb(256, 0, 0)") == .text)
    #expect(ContentRules.classify("const a = 1;") == .code)
    #expect(ContentRules.classify("line\n  indented") == .code)
    #expect(ContentRules.classify("42") == .text)
    #expect(ContentRules.title("https://example.com/path#secret") == "example.com")
    #expect(ContentRules.title("\nsecond") == "Text")
    let title = ContentRules.title(String(repeating:"a",count:158) + "😀XYZ")
    #expect(title == String(repeating:"a",count:158)+"…")
    #expect(ContentRules.normalizeSearch("  Ａ Café\n\tWORLD  ") == "a cafe world")
    #expect(ContentRules.searchText(title:"Title",body:"Body") == "title body")
}

@Test func concurrentSettingsFormsPreserveIndependentEditsAndRejectShortcutConflict() throws {
    let baseline = AppSettings()
    var first = baseline; first.lyrics.fontSize = 22; first.language = "ja-JP"
    var second = baseline; second.lyrics.scrolling = false
    let merged = try SettingsMerge.merge(baseline:baseline,requested:second,latest:first)
    #expect(merged.lyrics.fontSize == 22 && !merged.lyrics.scrolling && merged.language == "ja-JP")
    var conflict = merged; conflict.keyboardShortcuts["Space"] = conflict.quickPanelHotkey
    #expect(throws:(any Error).self) { try conflict.validated() }
}
