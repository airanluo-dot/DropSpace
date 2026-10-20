import Foundation
import DropSpaceCore

extension WorkspaceModel {
    func prepareNativeDrag(_ id: UUID, rows: [DropItem], useSelection: Bool) async throws -> [NativeDragContent] {
        let chosen = useSelection && selection.contains(id) ? rows.filter { selection.contains($0.id) } : rows.filter { $0.id == id }
        let generation = refreshGeneration
        let files: [DropItem]
        switch DragSelectionRules.plan(chosen, expandSingleBatch: useSelection) {
        case .none: return []
        case .text(let item):
            guard let text = item.text else { return [] }
            return [.text(text, url: item.kind == .url ? URL(string: text) : nil)]
        case .files(let values): files = values
        case .batch(let batch):
            // Read the full persisted batch when the gesture begins, including
            // members outside the current search, expanded state or page.
            files = try await store?.batch(batch).filter {
                $0.status == .available && ($0.file != nil || ($0.kind == .image && $0.payloadPath != nil))
            } ?? []
        }
        try Task.checkCancellation()
        guard !isQuitting, generation == refreshGeneration else { throw CancellationError() }
        return try await withThrowingTaskGroup(of: (Int, NativeDragContent).self) { group in
            for (index, item) in files.enumerated() {
                group.addTask {
                    let url = try await self.checkedContentURL(item)
                    try Task.checkCancellation()
                    guard await self.dragIsCurrent(generation) else { throw CancellationError() }
                    return (index, .file(url, folder: item.kind == .folder))
                }
            }
            var results: [(Int, NativeDragContent)] = []
            for try await result in group { results.append(result) }
            return results.sorted { $0.0 < $1.0 }.map(\.1)
        }
    }
    private func dragIsCurrent(_ generation: Int) -> Bool { !isQuitting && generation == refreshGeneration }
}
