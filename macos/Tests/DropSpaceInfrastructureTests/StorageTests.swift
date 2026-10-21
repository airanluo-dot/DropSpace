import Testing
import Foundation
import DropSpaceCore
@testable import DropSpaceInfrastructure

private func temporary() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: url,withIntermediateDirectories: true)
    return url
}
@Test func undoSurvivesReopenAndNeverDeletesSource() async throws {
    let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
    let original = root.appendingPathComponent("original.txt"); try Data("untouched".utf8).write(to: original)
    let store = try ItemStore(directory: root.appendingPathComponent("db"))
    let item = DropItem(source: .space,kind: .file,title: "original",file: .init(path: original.path))
    try await store.insert([item])
    let now = Date(); let pending = try await store.remove([item.id],now: now); let token = try #require(pending)
    #expect(try await store.items().isEmpty)
    let reopened = try ItemStore(directory: root.appendingPathComponent("db"))
    try await reopened.undo(token.id,now: now.addingTimeInterval(1))
    #expect(try await reopened.items() == [item])
    _ = try await reopened.remove([item.id],now: now)
    try await reopened.finalizeUndo(now: now.addingTimeInterval(9))
    #expect(try await reopened.pendingCleanup().isEmpty)
    #expect(try String(contentsOf: original,encoding: .utf8) == "untouched")
}
@Test func expiredPayloadCleanupAndPathEscape() async throws {
    let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root); let payloads = PayloadStore(directory: root.appendingPathComponent("payloads"))
    let name = try await payloads.write(Data([1,2,3]),extension: "png")
    let item = DropItem(source: .clipboard,kind: .image,title: "image",payloadPath: name)
    try await store.insert([item]); let now = Date(); _ = try await store.remove([item.id],now: now)
    try await store.finalizeUndo(now: now.addingTimeInterval(9)); try await payloads.reconcile(store)
    #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("payloads/" + name).path))
    await #expect(throws: (any Error).self) { try await payloads.remove("../outside") }
}
@Test func settingsRoundTripAndFutureVersionPreserved() async throws {
    let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
    let file = root.appendingPathComponent("settings.json"); let store = SettingsStore(file: file)
    var settings = AppSettings(); settings.language = "zh-CN"; settings.keyboardShortcuts["Space"] = "Cmd+Alt+1"
    try await store.save(settings); #expect(try await store.load() == settings)
    let future = Data("{\"version\":999}".utf8); try future.write(to: file)
    await #expect(throws: (any Error).self) { try await store.load() }
    #expect(try Data(contentsOf: file) == future)
}

@Test func ownedPromiseDirectoryCleanupPreservesLinkedSource() async throws {
    let root = try temporary(); defer { try? FileManager.default.removeItem(at:root) }
    let outside = root.appendingPathComponent("source.txt"); try Data("safe".utf8).write(to:outside)
    let incoming = root.appendingPathComponent("promise"); try FileManager.default.createDirectory(at:incoming,withIntermediateDirectories:true)
    try FileManager.default.createSymbolicLink(at:incoming.appendingPathComponent("link"),withDestinationURL:outside)
    let payloads = PayloadStore(directory:root.appendingPathComponent("owned"))
    let name = try await payloads.importPromisedFile(incoming)
    try await payloads.remove(name)
    #expect(try String(contentsOf:outside,encoding:.utf8) == "safe")
    #expect(FileManager.default.fileExists(atPath:incoming.path))
}

@Test func relocationRejectsStaleEditsAndPinUndoPreservesNewReference() async throws {
    let root = try temporary(); defer { try? FileManager.default.removeItem(at:root) }
    let store = try ItemStore(directory:root)
    let item = DropItem(source:.space,kind:.file,title:"old",file:.init(path:"/original"),payloadPath:"owned.bin")
    try await store.insert([item]); let now = Date()
    let pending = try await store.setPinned([item.id],pinned:true,now:now)
    let token = try #require(pending)
    var stale = item; stale.revision += 1; stale.title = "stale"
    await #expect(throws:(any Error).self) { try await store.replace(stale) }
    let current = try await store.items()
    var changed = try #require(current.first)
    changed.file = .init(path:"/new-source"); changed.payloadPath = nil; changed.title = "relocated"; changed.revision += 1
    try await store.replace(changed)
    #expect(try await store.pendingCleanup() == ["owned.bin"])
    try await store.undo(token.id,now:now.addingTimeInterval(1))
    let restored = try await store.items()
    #expect(restored.first?.isPinned == false)
    #expect(restored.first?.file?.path == "/new-source")
    #expect(restored.first?.title == "relocated")
    #expect(restored.first?.payloadPath == nil)
}

@Test func unchangedPinAndInterruptedRemovalMatchRecoveryRules() async throws {
    let root = try temporary(); defer { try? FileManager.default.removeItem(at:root) }
    let store = try ItemStore(directory:root)
    let item = DropItem(source:.space,kind:.text,title:"keep",text:"keep")
    try await store.insert([item]); let now = Date()
    #expect(try await store.setPinned([item.id],pinned:false,now:now) == nil)
    #expect(try await store.pendingUndo() == nil)
    _ = try await store.remove([item.id],now:now)
    let reopened = try ItemStore(directory:root)
    try await reopened.recoverAfterLaunch(now:now.addingTimeInterval(7.999))
    #expect(try await reopened.items().map(\.id) == [item.id])
    #expect(try await reopened.pendingUndo() == nil)
    _ = try await reopened.remove([item.id],now:now)
    try await reopened.recoverAfterLaunch(now:now.addingTimeInterval(8))
    #expect(try await reopened.items().isEmpty)
}
