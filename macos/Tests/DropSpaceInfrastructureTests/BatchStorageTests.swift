import Foundation
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

@Test func batchCommandsUsePersistedMembersAndUndoPreservesIdentity() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-batch-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root), batch = UUID(), now = Date()
    let items = (0..<5).map { index in
        var item = DropItem(source: .space, kind: .file, title: "file-\(index)", isPinned: index == 0, batchID: batch)
        item.batchIndex = index; item.batchCount = 5; return item
    }
    try await store.insert(items)
    var query = ItemQuery(text: "file-4"); query.limit = 1
    #expect(try await store.items(query).count == 1)
    let pin = try #require(await store.toggleBatchPin(batch, now: now))
    #expect(pin.count == 4)
    #expect(try await store.batch(batch).allSatisfy(\.isPinned))
    try await store.undo(pin.id, now: now.addingTimeInterval(1))
    #expect(try await store.batch(batch).filter(\.isPinned).map(\.id) == [items[0].id])
    let removed = try #require(await store.removeBatch(batch, now: now.addingTimeInterval(2)))
    #expect(removed.count == 5)
    #expect(try await store.batch(batch).isEmpty)
    try await store.undo(removed.id, now: now.addingTimeInterval(3))
    #expect(try await store.batch(batch).map(\.id) == items.map(\.id))
    #expect(try await store.batch(batch).map(\.batchIndex) == [0, 1, 2, 3, 4])
}
