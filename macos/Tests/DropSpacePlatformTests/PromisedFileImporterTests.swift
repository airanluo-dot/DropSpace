import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure
@testable import DropSpacePlatform

@Test func promisedBatchPreservesIdentityAndSurvivesUndo() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root)
    let payloads = PayloadStore(directory: root.appendingPathComponent("Payloads"))
    let references = FileReferenceService()
    let importer = PromisedFileImporter(store: store, payloads: payloads, references: references)
    let (lease, stage) = try await importer.begin()
    let file = stage.appendingPathComponent("Keep Name.txt"), empty = stage.appendingPathComponent("empty.txt")
    try Data("promised bytes".utf8).write(to: file); try Data().write(to: empty)
    #expect(try await store.pendingCleanup().isEmpty)
    // Ordinary cleanup while a producer is active must not remove its directory.
    try await payloads.reconcile(store)
    #expect(FileManager.default.fileExists(atPath: file.path))
    let imported = try await importer.finish(lease, urls: [file, empty], settings: AppSettings())
    let items = imported.items
    #expect(imported.rejected == 0)
    #expect(items.map(\.title) == ["Keep Name.txt", "empty.txt"])
    #expect(items.map(\.batchIndex) == [0, 1])
    #expect(items.allSatisfy { $0.batchCount == 2 && $0.batchID == lease.id && $0.batchAcquisition == "file-promise" })
    #expect(try Data(contentsOf: URL(fileURLWithPath: items[0].file!.path)) == Data("promised bytes".utf8))
    #expect(try Data(contentsOf: URL(fileURLWithPath: items[1].file!.path)).isEmpty)
    #expect(!FileManager.default.fileExists(atPath: stage.path))
    let now = Date(), receipt = try #require(try await store.removeBatch(lease.id))
    try await store.undo(receipt.id, now: now.addingTimeInterval(1))
    let reopened = try ItemStore(directory: root)
    try await reopened.recoverAfterLaunch()
    #expect(try await reopened.batch(lease.id).count == 2)
    #expect(try await reopened.pendingCleanup().isEmpty)
    _ = try await store.removeBatch(lease.id, now: now)
    try await store.finalizeUndo(now: now.addingTimeInterval(9))
    try await payloads.reconcile(store)
    #expect(items.allSatisfy { !FileManager.default.fileExists(atPath: $0.file!.path) })
    #expect(await references.shutdown())
}

@Test func promisedBatchDatabaseFailurePreservesSuccessfulItems() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root)
    let payloads = PayloadStore(directory: root.appendingPathComponent("Payloads"))
    let references = FileReferenceService()
    let importer = PromisedFileImporter(store: store, payloads: payloads, references: references)
    let (lease, stage) = try await importer.begin()
    let first = stage.appendingPathComponent("first.txt"), second = stage.appendingPathComponent("second.txt"), third = stage.appendingPathComponent("third.txt")
    try Data([1]).write(to: first); try Data([2]).write(to: second); try Data([3]).write(to: third)
    let db = try SQLiteConnection(url: root.appendingPathComponent("workspace.sqlite"))
    try db.execute("CREATE TRIGGER fail_second_promise BEFORE INSERT ON items WHEN json_extract(NEW.body,'$.title')='second.txt' BEGIN SELECT RAISE(ABORT,'validation commit failure'); END")
    let result = try await importer.finish(lease, urls: [first, second, third], settings: AppSettings())
    #expect(result.items.map(\.title) == ["first.txt", "third.txt"] && result.rejected == 1)
    #expect(result.items.map(\.batchIndex) == [0, 1])
    #expect(result.items.allSatisfy { $0.batchCount == 3 && $0.batchID == lease.id })
    #expect(try await store.items().count == 2)
    #expect(try await store.pendingCleanup().isEmpty)
    #expect(Set(try FileManager.default.contentsOfDirectory(atPath: root.appendingPathComponent("Payloads").path)) == Set(result.items.compactMap(\.payloadPath)))
    #expect(try Data(contentsOf: URL(fileURLWithPath: result.items[0].file!.path)) == Data([1]))
    #expect(try Data(contentsOf: URL(fileURLWithPath: result.items[1].file!.path)) == Data([3]))
    #expect(try db.rows("SELECT id FROM owned_imports").isEmpty)
    #expect(await references.shutdown())
}

@Test func promisedBatchLimitsCancellationAndEscapedReferencesDoNotAdopt() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root)
    let payloads = PayloadStore(directory: root.appendingPathComponent("Payloads"))
    let references = FileReferenceService()
    let importer = PromisedFileImporter(store: store, payloads: payloads, references: references)
    let outside = root.appendingPathComponent("original.txt")
    try Data("source stays".utf8).write(to: outside)
    let (escaped, stage) = try await importer.begin()
    let link = stage.appendingPathComponent("linked.txt")
    try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outside)
    await #expect(throws: (any Error).self) { try await importer.finish(escaped, urls: [link], settings: AppSettings()) }
    #expect(try String(contentsOf: outside, encoding: .utf8) == "source stays")
    let (large, largeStage) = try await importer.begin()
    let largeFile = largeStage.appendingPathComponent("sparse.bin")
    #expect(FileManager.default.createFile(atPath: largeFile.path, contents: nil))
    let handle = try FileHandle(forWritingTo: largeFile)
    try handle.truncate(atOffset: UInt64(PromisedFileRules.maximumFileBytes + 1)); try handle.close()
    await #expect(throws: (any Error).self) { try await importer.finish(large, urls: [largeFile], settings: AppSettings()) }
    let (cancelled, cancelledStage) = try await importer.begin()
    let cancelledFile = cancelledStage.appendingPathComponent("cancel.txt"); try Data([3]).write(to: cancelledFile)
    let gate = PromiseTestGate()
    let task = Task {
        await gate.wait()
        return try await importer.finish(cancelled, urls: [cancelledFile], settings: AppSettings())
    }
    task.cancel(); await gate.open()
    await #expect(throws: CancellationError.self) { try await task.value }
    #expect(try await store.items().isEmpty)
    #expect(try await store.pendingCleanup().isEmpty)
    #expect(try String(contentsOf: outside, encoding: .utf8) == "source stays")
    #expect(await references.shutdown())
}
private actor PromiseTestGate {
    private var opened = false
    private(set) var reached = false
    private var continuation: CheckedContinuation<Void, Never>?
    func wait() async { if !opened { await withCheckedContinuation { continuation = $0 } } }
    func reachAndWait() async { reached = true; await wait() }
    func open() { opened = true; continuation?.resume(); continuation = nil }
}

@Test func promisedBatchCancellationKeepsCommittedPrefixAndCleansRemainder() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root)
    let payloads = PayloadStore(directory: root.appendingPathComponent("Payloads"))
    let references = FileReferenceService()
    let importer = PromisedFileImporter(store: store, payloads: payloads, references: references)
    let (lease, stage) = try await importer.begin()
    let first = stage.appendingPathComponent("accepted.txt"), second = stage.appendingPathComponent("cancelled.txt")
    try Data([1]).write(to: first); try Data([2]).write(to: second)
    let gate = PromiseTestGate()
    let task = Task {
        try await importer.finish(lease, urls: [first, second], settings: AppSettings(), onAccepted: { count in
            if count == 1 { await gate.reachAndWait() }
        })
    }
    let deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while !(await gate.reached), ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
    let reached = await gate.reached
    task.cancel(); await gate.open()
    await #expect(throws: CancellationError.self) { try await task.value }
    #expect(reached)
    let kept = try await store.batch(lease.id)
    #expect(kept.count == 1 && kept.first?.title == "accepted.txt")
    #expect(kept.first?.batchIndex == 0 && kept.first?.batchCount == 2)
    let item = try #require(kept.first)
    #expect(try Data(contentsOf: URL(fileURLWithPath: item.file!.path)) == Data([1]))
    #expect(!FileManager.default.fileExists(atPath: stage.path))
    #expect(try await store.pendingCleanup().isEmpty)
    #expect(Set(try FileManager.default.contentsOfDirectory(atPath: root.appendingPathComponent("Payloads").path)) == [try #require(item.payloadPath)])
    let reopened = try ItemStore(directory: root); try await reopened.recoverAfterLaunch()
    #expect(try await reopened.batch(lease.id) == kept)
    #expect(await references.shutdown())
}
