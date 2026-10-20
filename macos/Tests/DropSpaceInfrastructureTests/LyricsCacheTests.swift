import Foundation
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

@Test func lyricsCacheQuotaRetirementAndOwnedFileBoundaries() async throws {
    let parent = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let root = parent.appendingPathComponent("LyricsCache")
    defer { try? FileManager.default.removeItem(at: parent) }
    let cache = LyricsCache(directory: root)
    try await cache.configure(maximumBytes: 8)
    #expect(!FileManager.default.fileExists(atPath: root.path))
    let run = await cache.generation
    try await cache.write(category: .ai, identity: "one", bytes: Data("first".utf8), generation: run)
    try await cache.write(category: .source, identity: "two", bytes: Data("next".utf8), generation: run)
    #expect(try await cache.read(category: .ai, identity: "one") == nil)
    #expect(try await cache.usage() == 4)
    let outsider = parent.appendingPathComponent("source.txt"), unrelated = root.appendingPathComponent("notes.txt")
    try Data("do not delete".utf8).write(to: outsider)
    try Data("not owned".utf8).write(to: unrelated)
    let link = root.appendingPathComponent("source-" + String(repeating: "0", count: 64) + ".lyrics-cache")
    try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outsider)
    try await cache.clear()
    try await cache.write(category: .source, identity: "late", bytes: Data("late".utf8), generation: run)
    #expect(try await cache.usage() == 0)
    #expect(try String(contentsOf: outsider, encoding: .utf8) == "do not delete")
    #expect(try String(contentsOf: unrelated, encoding: .utf8) == "not owned")
    let current = await cache.generation
    try await cache.configure(maximumBytes: 0)
    try await cache.configure(maximumBytes: 10000)
    try await cache.write(category: .ai, identity: "disabled-generation", bytes: Data("late".utf8), generation: current)
    #expect(try await cache.usage() == 0)
    let redirected = parent.appendingPathComponent("Redirected")
    try FileManager.default.createSymbolicLink(at: redirected, withDestinationURL: root)
    await #expect(throws: (any Error).self) { try await LyricsCache(directory: redirected).clear() }
}

@Test func lyricsCacheRestoresRecordingAndRejectsCorruptPayload() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let cache = LyricsCache(directory: root)
    let query = LyricsQuery(title: "Recording", artist: "Artist", duration: 180.125, trackIdentity: "track-1")
    let document = try LyricsParser.parse("[00:01]first\n[00:03]second", provider: .netEase).binding(.init(title: "Recording", artist: "Artist", album: "", duration: 180.125, score: 7, candidateID: "13"), to: query)
    try await cache.writeDocument(identity: "recording", document: document, generation: cache.generation)
    #expect(try await LyricsCache(directory: root).readDocument(identity: "recording") == document)
    try await cache.write(category: .source, identity: "corrupt", bytes: Data("{ broken".utf8), generation: cache.generation)
    #expect(try await cache.readDocument(identity: "corrupt") == nil)
    try await cache.configure(maximumBytes: 0)
    #expect(try await cache.readDocument(identity: "recording") == nil)
    #expect(try await cache.usage() == 0)
}
