import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func fileAvailabilityReconfirmsIdentityAndPreservesConcurrentMutations() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root)
    let original = DropItem(source: .space, kind: .file, title: "original", file: .init(path: "/original", bookmark: Data([1]), volumeID: "volume"))
    try await store.insert([original])
    let missing = FileAvailability(status: .missing, reason: .missing)
    let accepted = try await store.refreshFileStatus(expected: original, availability: missing)
    #expect(accepted.accepted && accepted.current?.status == .missing)
    #expect(accepted.current?.file?.lastCheckedAt == missing.checkedAt)
    let unavailable = try #require(accepted.current)
    _ = try await store.setPinned([original.id], pinned: true)
    // A late recovery must not overwrite the revision changed by a pin.
    let lateRecovery = try await store.refreshFileStatus(expected: unavailable, availability: .init(status: .available))
    #expect(!lateRecovery.accepted && lateRecovery.current?.isPinned == true)
    let pinned = try #require(lateRecovery.current)
    let recovery = try await store.refreshFileStatus(expected: pinned, availability: .init(status: .available))
    #expect(recovery.accepted && recovery.current?.file?.availabilityReason == nil)
    let available = try #require(recovery.current)
    var replacement = available
    replacement.file?.bookmark = Data([2]); replacement.revision += 1
    try await store.replace(replacement)
    // Same path/volume and unchanged status are insufficient after relocation.
    let lateUnchanged = try await store.refreshFileStatus(expected: available, availability: .init(status: .available))
    #expect(!lateUnchanged.accepted && lateUnchanged.current?.file?.bookmark == Data([2]))
    let now = Date()
    try await store.markUsed([original.id], now: now)
    let used = try #require(try await store.item(original.id))
    #expect(used.file == replacement.file && used.isPinned && used.lastUsedAt == now)
    #expect(used.revision == replacement.revision + 1)
    let undo = try #require(try await store.remove([original.id], now: now))
    #expect(try await store.item(original.id) == nil)
    let hidden = try await store.refreshFileStatus(expected: used, availability: missing)
    #expect(!hidden.accepted && hidden.current == nil)
    try await store.markUsed([original.id], now: now.addingTimeInterval(1))
    try await store.undo(undo.id, now: now.addingTimeInterval(2))
    let restored = try #require(try await store.item(original.id))
    #expect(restored.file == used.file && restored.lastUsedAt == now && restored.status == .available)
    let reopened = try ItemStore(directory: root)
    #expect(try await reopened.item(original.id) == restored)
}

@Test func fileAvailabilityTransactionFailureDoesNotPublishOrDamageReference() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root)
    let original = DropItem(source: .space, kind: .folder, title: "source", file: .init(path: "/source"))
    try await store.insert([original])
    let db = try SQLiteConnection(url: root.appendingPathComponent("workspace.sqlite"))
    try db.execute("CREATE TRIGGER fail_file_status BEFORE UPDATE ON items BEGIN SELECT RAISE(ABORT,'test write failure'); END")
    await #expect(throws: (any Error).self) {
        try await store.refreshFileStatus(expected: original, availability: .init(status: .unavailable, reason: .typeChanged))
    }
    #expect(try await store.item(original.id) == original)
    try db.execute("DROP TRIGGER fail_file_status")
    let result = try await store.refreshFileStatus(expected: original, availability: .init(status: .unavailable, reason: .typeChanged))
    #expect(result.accepted && result.current?.file?.availabilityReason == .typeChanged)
    // Optional fields keep pre-change records readable without rewriting the DB.
    let oldJSON = #"{"path":"/legacy","size":7}"#
    let reference = try JSONDecoder().decode(FileReference.self, from: Data(oldJSON.utf8))
    #expect(reference.availabilityReason == nil && reference.lastCheckedAt == nil && reference.size == 7)
}
