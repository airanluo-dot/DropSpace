import Foundation

public enum DragSelectionPlan: Sendable, Equatable {
    case none
    case batch(UUID)
    case files([DropItem])
    case text(DropItem)
}

public enum DragSelectionRules {
    public static func plan(_ rows: [DropItem], expandSingleBatch: Bool = true) -> DragSelectionPlan {
        let actionable = rows.filter { $0.status == .available }
        if actionable.count == 1, let item = actionable.first,
           expandSingleBatch, (item.batchCount ?? 1) > 1, let batch = item.batchID { return .batch(batch) }
        let files = actionable.filter { $0.file != nil || ($0.kind == .image && $0.payloadPath != nil) }
        if !files.isEmpty { return .files(files) }
        if actionable.count == 1, let item = actionable.first, item.text != nil { return .text(item) }
        return .none
    }
}
