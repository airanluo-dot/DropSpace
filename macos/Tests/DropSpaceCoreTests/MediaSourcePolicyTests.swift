import Foundation
import Testing
@testable import DropSpaceCore

@Test func mediaSourceSelectionRetainsFrozenRestrictionAndOfflineChoices() throws {
    var value = IslandActivitySettings()
    let music = "com.apple.Music", spotify = "com.spotify.client", offline = "example.offline"
    #expect(MediaSourcePolicy.allows(music, settings: value))
    // Legacy persisted nonempty lists restrict even when the explicit flag is false.
    value.allowedMediaSourceAppIDs = ["COM.APPLE.MUSIC", offline]
    #expect(MediaSourcePolicy.restricted(value))
    #expect(MediaSourcePolicy.allows(music, settings: value))
    #expect(!MediaSourcePolicy.allows(spotify, settings: value))
    #expect(MediaSourcePolicy.displayed(available: [music, spotify], settings: value) == [music, spotify, offline])
    MediaSourcePolicy.setAll(true, available: [music, spotify], settings: &value)
    #expect(!value.useMediaSourceAllowList && value.allowedMediaSourceAppIDs.isEmpty)
    MediaSourcePolicy.setSelected(music, selected: false, displayed: [music, spotify], settings: &value)
    #expect(value.useMediaSourceAllowList && value.allowedMediaSourceAppIDs == [spotify])
    MediaSourcePolicy.setSelected(spotify, selected: false, displayed: [music, spotify], settings: &value)
    #expect(value.allowedMediaSourceAppIDs.isEmpty && !MediaSourcePolicy.allows(music, settings: value))
    MediaSourcePolicy.setAll(false, available: [music], settings: &value)
    #expect(value.allowedMediaSourceAppIDs == [music])
    let saved = try JSONDecoder().decode(IslandActivitySettings.self, from: JSONEncoder().encode(value))
    #expect(saved == value && !MediaSourcePolicy.allows(spotify, settings: saved))
}

@Test func mediaSourceNormalizationUsesUTF16BoundsAndPreservesIdentity() throws {
    let emoji = String(repeating: "😀", count: 257)
    let boundary = String(repeating: "a", count: 512)
    let values = ["", " \n\t", emoji, boundary, "A", "a", " A "] + (0..<130).map { "app.\($0)" }
    var settings = AppSettings()
    settings.islandActivity.allowedMediaSourceAppIDs = values
    let result = try settings.validated().islandActivity.allowedMediaSourceAppIDs
    #expect(result.count == 128)
    #expect(Array(result.prefix(3)) == [boundary, "A", " A "])
    #expect(result.last == "app.124")
    #expect(!result.contains(emoji))
}
