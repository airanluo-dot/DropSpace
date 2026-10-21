import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func ordinaryFileIntakeRetainsOtherReferencesWhenOneInsertFails() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root), batch = UUID()
    let items = (0..<3).map { index in
        var item = DropItem(source: .space, kind: .file, title: "input-\(index)", file: .init(path: "/external/input-\(index)"), batchID: batch)
        item.batchIndex = index; item.batchCount = 3; item.batchAcquisition = "main-window-drop"
        return item
    }
    let db = try SQLiteConnection(url: root.appendingPathComponent("workspace.sqlite"))
    try db.execute("CREATE TRIGGER fail_one_input BEFORE INSERT ON items WHEN json_extract(NEW.body,'$.title')='input-1' BEGIN SELECT RAISE(ABORT,'validation write failure'); END")
    let result = try await store.insertFileCandidates(items)
    #expect(result.rejected == 1 && result.items.map(\.id) == [items[0].id, items[2].id])
    #expect(result.items.map(\.batchIndex) == [0, 1])
    #expect(result.items.allSatisfy { $0.batchCount == 3 && $0.batchAcquisition == "main-window-drop" })
    #expect(try await store.batch(batch) == result.items)
    #expect(try await store.pendingCleanup().isEmpty)
    let reopened = try ItemStore(directory: root)
    #expect(try await reopened.batch(batch) == result.items)
}
