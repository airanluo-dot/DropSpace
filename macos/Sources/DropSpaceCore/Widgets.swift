import Foundation

public enum WidgetID: String, Codable, CaseIterable, Sendable {
    case clock, calendar, resourceUsage, settings, battery, uptime, stopwatch, clipboardPause
    case utcClock, focusTimer, countdown, diskSpace, networkStatus, calculator, pinnedItems, weekProgress
    public var sizes: [WidgetSize] {
        switch self {
        case .clock: [.init(1,1), .init(2,1), .init(2,2)]
        case .calendar: [.init(1,1), .init(2,2)]
        case .resourceUsage, .stopwatch: [.init(2,1), .init(2,2)]
        case .battery, .clipboardPause: [.init(1,1), .init(2,1)]
        case .utcClock, .focusTimer, .countdown, .diskSpace, .networkStatus, .weekProgress, .uptime: [.init(2,1)]
        default: [.init(1,1)]
        }
    }
}
public struct WidgetSize: Codable, Equatable, Hashable, Sendable {
    public var columns: Int; public var rows: Int
    public init(_ columns: Int, _ rows: Int) { self.columns = columns; self.rows = rows }
}
public struct WidgetPlacement: Codable, Identifiable, Equatable, Sendable {
    public var id: WidgetID
    public var column: Int; public var row: Int; public var size: WidgetSize
    public init(_ id: WidgetID, _ column: Int, _ row: Int, _ width: Int, _ height: Int) {
        self.id = id; self.column = column; self.row = row; size = .init(width, height)
    }
    public func overlaps(_ other: Self) -> Bool {
        column < other.column + other.size.columns && other.column < column + size.columns && row < other.row + other.size.rows && other.row < row + size.rows
    }
    public var valid: Bool { column >= 0 && row >= 0 && column <= 8 - size.columns && row <= 4 - size.rows && id.sizes.contains(size) }
}
public struct WidgetLayout: Codable, Equatable, Sendable {
    public var expanded: [WidgetPlacement]
    public var compact: [WidgetID?]
    public static let `default` = WidgetLayout(expanded: [.init(.clock,0,0,2,1), .init(.calendar,2,0,2,2), .init(.resourceUsage,4,0,2,1), .init(.settings,0,1,1,1), .init(.battery,6,0,2,1), .init(.uptime,4,1,2,1), .init(.stopwatch,0,2,2,1), .init(.clipboardPause,7,1,1,1)], compact: [.clock, nil, .resourceUsage])
    public func normalized() -> Self {
        var result = Self(expanded: [], compact: [nil,nil,nil])
        for item in expanded where !result.expanded.contains(where: { $0.id == item.id }) {
            let size = item.id.sizes.min { abs(Double($0.columns) - Double(item.size.columns)) + abs(Double($0.rows) - Double(item.size.rows)) < abs(Double($1.columns) - Double(item.size.columns)) + abs(Double($1.rows) - Double(item.size.rows)) }!
            if let slot = result.slot(item.id, size: size, preferredColumn: item.column, preferredRow: item.row) { result.expanded.append(slot) }
        }
        for (i,id) in compact.prefix(3).enumerated() where id == .clock || id == .resourceUsage {
            if !result.compact.contains(id) { result.compact[i] = id }
        }
        return result
    }
    public func slot(_ id: WidgetID, size: WidgetSize, preferredColumn: Int = 0, preferredRow: Int = 0) -> WidgetPlacement? {
        let preferred = min(3,max(0,preferredRow)) * 8 + min(7,max(0,preferredColumn))
        for offset in 0..<32 {
            let index = (preferred + offset) % 32
            let item = WidgetPlacement(id,index % 8,index / 8,size.columns,size.rows)
            if item.valid && !expanded.contains(where: { $0.overlaps(item) }) { return item }
        }
        return nil
    }
    public mutating func move(_ desired: WidgetPlacement) -> Bool {
        guard desired.valid, let old = expanded.first(where: { $0.id == desired.id }) else { return false }
        let collisions = expanded.filter { $0.id != desired.id && $0.overlaps(desired) }
        var swapped: WidgetPlacement?
        if !collisions.isEmpty {
            guard collisions.count == 1, let other = collisions.first, old.size == desired.size, other.size == desired.size, other.column == desired.column, other.row == desired.row else { return false }
            var replacement = other; replacement.column = old.column; replacement.row = old.row
            guard !expanded.contains(where: { $0.id != old.id && $0.id != other.id && $0.overlaps(replacement) }) else { return false }
            swapped = replacement
        }
        expanded = expanded.map { $0.id == desired.id ? desired : ($0.id == swapped?.id ? swapped! : $0) }
        return true
    }
}
public struct WidgetSettings: Codable, Equatable, Sendable {
    public var enabled = true
    public var layout = WidgetLayout.default
    public init() {}
}
