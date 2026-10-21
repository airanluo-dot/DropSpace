import Foundation

/// MainViewModel.ItemSelection: one actionable loaded header, even when paging,
/// filtering or removal omits the persisted first member. Expansion is UI state.
public struct BatchProjection: Sendable {
    public let visible: [DropItem]
    public let headers: Set<UUID>
    public init(items: [DropItem], expanded: Set<UUID>) {
        let groups = Dictionary(grouping: items.filter { $0.batchID != nil }, by: { $0.batchID! })
        var headers = Set<UUID>(), hidden = Set<UUID>()
        for (batch, members) in groups where members.contains(where: { ($0.batchCount ?? members.count) > 1 }) {
            let header = members.enumerated().min { lhs, rhs in
                let a = lhs.element.batchIndex ?? Int.max, b = rhs.element.batchIndex ?? Int.max
                return a == b ? lhs.offset < rhs.offset : a < b
            }!.element.id
            headers.insert(header)
            if !expanded.contains(batch) { hidden.formUnion(members.filter { $0.id != header }.map(\.id)) }
        }
        self.headers = headers
        visible = items.filter { !hidden.contains($0.id) }
    }
}
