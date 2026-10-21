import Foundation
import Testing
import DropSpaceCore

@Test func nativeDragSelectionMatchesBatchAndMixedContentRules() throws {
    let batch = UUID()
    var file = DropItem(source: .space, kind: .file, title: "file", file: .init(path: "/file"), batchID: batch)
    file.batchCount = 3; file.batchIndex = 2
    let second = DropItem(source: .clipboard, kind: .image, title: "image", payloadPath: "images/owned.png")
    let text = DropItem(source: .space, kind: .text, title: "text", text: "selected text")
    var missing = file; missing.id = UUID(); missing.status = .missing
    // A single member expands its persisted batch even if the first member
    // is excluded by search/paging; Island does not inherit main selection.
    #expect(DragSelectionRules.plan([file]) == .batch(batch))
    #expect(DragSelectionRules.plan([file], expandSingleBatch: false) == .files([file]))
    #expect(DragSelectionRules.plan([file, second]) == .files([file, second]))
    #expect(DragSelectionRules.plan([file, text]) == .files([file]))
    #expect(DragSelectionRules.plan([missing, text]) == .text(text))
    #expect(DragSelectionRules.plan([missing]) == .none)
    #expect(DragSelectionRules.plan([text, text]) == .none)
    #expect(DragSelectionRules.plan([]) == .none)
}
