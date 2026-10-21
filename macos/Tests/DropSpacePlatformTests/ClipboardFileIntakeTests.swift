import AppKit
import Foundation
import Testing
import DropSpaceCore
import DropSpaceInfrastructure
import DropSpacePlatform

@Test @MainActor func nativeClipboardFileCaptureKeepsMissingAndFolderReferencesUnderByteLimits() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let folder = root.appendingPathComponent("folder"), file = root.appendingPathComponent("file"), large = root.appendingPathComponent("large"), missing = root.appendingPathComponent("missing")
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    try Data([1, 2]).write(to: file); try Data([3, 4, 5]).write(to: large)
    let board = NSPasteboard(name: .init("DropSpace.Validation.FileCapture." + UUID().uuidString))
    defer { board.releaseGlobally() }
    let store = try ItemStore(directory: root.appendingPathComponent("db"))
    let capture = ClipboardCapture(store: store, payloads: PayloadStore(directory: root.appendingPathComponent("payloads")), pasteboard: board)
    var settings = AppSettings(); settings.privacyChoicesCompleted = true
    settings.maxClipboardFileBytes = 2; settings.maxClipboardFileTotalBytes = 2
    do {
        try await capture.configure(settings)
        board.clearContents()
        #expect(board.writeObjects([folder, file, large, missing].map { $0 as NSURL }))
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        var items: [DropItem] = []
        while items.count < 3, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(25)); items = try await store.items()
        }
        #expect(Set(items.map(\.title)) == ["folder", "file", "missing"])
        #expect(items.allSatisfy { $0.source == .clipboard && $0.payloadPath == nil && $0.batchCount == 3 && $0.batchAcquisition == "clipboard-files" })
        #expect(items.first(where: { $0.title == "missing" })?.status == .missing)
        #expect(items.first(where: { $0.kind == .folder })?.file?.size == nil)
        settings.clipboardPaused = true; try await capture.configure(settings)
        board.clearContents(); #expect(board.writeObjects([file as NSURL]))
        try await Task.sleep(for: .milliseconds(500))
        #expect(try await store.items().count == 3)
        await capture.stopAndDrain()
    } catch { await capture.stopAndDrain(); throw error }
    #expect(try Data(contentsOf: file) == Data([1, 2]))
    #expect(try Data(contentsOf: large) == Data([3, 4, 5]))
}
