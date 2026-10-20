import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func adaptiveLyricsDefaultMergesExistingSettingsAndPersistsExplicitBaselineChoice() async throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let file = directory.appendingPathComponent("settings.json")
    let original = Data(#"{"lyrics":{"fontSize":22,"scrollingMaxWidth":400,"secondaryLyrics":true},"clipboardPaused":true}"#.utf8)
    try original.write(to: file)
    let store = SettingsStore(file: file)
    var loaded = try await store.load()
    #expect(loaded.lyrics.translationScrollMode == .adaptive)
    #expect(loaded.lyrics.fontSize == 22 && loaded.lyrics.scrollingMaxWidth == 400 && loaded.lyrics.secondaryLyrics)
    #expect(try Data(contentsOf: file) == original) // read does not rewrite the user's file
    loaded.lyrics.translationScrollMode = .baseline
    try await store.save(loaded)
    #expect(try await SettingsStore(file: file).load().lyrics.translationScrollMode == .baseline)
    let invalid = Data(#"{"lyrics":{"translationScrollMode":"invalid-mode"}}"#.utf8)
    try invalid.write(to: file)
    await #expect(throws: (any Error).self) { try await store.load() }
    #expect(try Data(contentsOf: file) == invalid)
}
