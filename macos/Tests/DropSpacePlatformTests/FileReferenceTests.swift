import Foundation
import Testing
import DropSpaceCore
import DropSpacePlatform

@Test func manualIntakeDoesNotInheritClipboardLimitsAndPreservesPartialBatch() throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let files = (0..<3).map { root.appendingPathComponent("source-\($0).txt") }
    for file in files { try Data("original file bytes".utf8).write(to: file) }
    var settings = AppSettings(); settings.maxClipboardFileItems = 1
    settings.maxClipboardFileBytes = 1; settings.maxClipboardFileTotalBytes = 1
    let result = try FileIntake.prepare(files + [files[0], root.appendingPathComponent("missing"), URL(string: "https://example.invalid/not-a-file")!], source: .space, settings: settings)
    #expect(result.items.count == 4 && result.failures.count == 1)
    #expect(result.items.map(\.batchIndex) == [0, 1, 2, 3])
    #expect(result.items.allSatisfy { $0.batchID == result.items[0].batchID && $0.batchCount == 4 && $0.payloadPath == nil })
    #expect(result.items.last?.status == .missing && result.items.last?.file?.availabilityReason == .missing)
    #expect(throws: (any Error).self) { try FileIntake.prepare(files, source: .clipboard, settings: settings) }
    #expect(try FileIntake.prepare([files[0]], source: .clipboard, settings: settings).items.isEmpty)
    for file in files { #expect(try String(contentsOf: file, encoding: .utf8) == "original file bytes") }
}

@Test func fileReferencesRejectWrongVolumeWithoutFollowingPathSubstitution() throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
    defer { try? FileManager.default.removeItem(at:root) }
    let source = root.appendingPathComponent("Source.txt")
    try Data("original".utf8).write(to:source)
    let result = try FileIntake.prepare([source],source:.space,settings:AppSettings())
    let reference = try #require(result.items.first?.file)
    #expect(try FileIntake.resolve(reference).lastPathComponent == "Source.txt")
    var otherVolume = reference; otherVolume.volumeID = "different-volume"
    #expect(throws:(any Error).self) { try FileIntake.resolve(otherVolume) }
    var pathOnly = otherVolume; pathOnly.bookmark = nil
    #expect(throws:(any Error).self) { try FileIntake.resolve(pathOnly) }
    #expect(try String(contentsOf:source,encoding:.utf8) == "original")
}
