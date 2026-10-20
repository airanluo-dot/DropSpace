import Foundation
import Testing
import DropSpaceCore

@Test func batchProjectionKeepsOneLoadedHeaderAcrossFilterPagingAndCollapse() throws {
    let batch = UUID()
    var items = (0..<4).map { index in
        var item = DropItem(source: .space, kind: .file, title: "file-\(index)", batchID: batch)
        item.batchIndex = index; item.batchCount = 4; return item
    }
    let filtered = BatchProjection(items: [items[3], items[2]], expanded: [])
    #expect(filtered.visible.map(\.id) == [items[2].id])
    let loaded = BatchProjection(items: [items[3], items[2], items[0]], expanded: [])
    #expect(loaded.headers == [items[0].id])
    #expect(loaded.visible.map(\.id) == [items[0].id])
    #expect(BatchProjection(items: items, expanded: [batch]).visible.count == 4)
    items[0].batchCount = 1; items[0].batchID = UUID()
    #expect(BatchProjection(items: [items[0]], expanded: []).headers.isEmpty)
    var legacy = try JSONSerialization.jsonObject(with: JSONEncoder().encode(items[1])) as! [String: Any]
    legacy.removeValue(forKey: "batchIndex"); legacy.removeValue(forKey: "batchCount"); legacy.removeValue(forKey: "batchAcquisition")
    let decoded = try JSONDecoder().decode(DropItem.self, from: JSONSerialization.data(withJSONObject: legacy))
    #expect(decoded.id == items[1].id && decoded.batchIndex == nil)
}
