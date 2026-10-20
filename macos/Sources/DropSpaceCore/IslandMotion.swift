import Foundation

/// The frozen bounded semi-implicit spring integration, independent of any UI
/// framework or timer. Retargeting preserves velocity; reduce motion snaps.
public struct IslandMotion: Sendable {
    private struct Channel: Sendable {
        var value: Double, target: Double, velocity = 0.0
        let frequency: Double, damping: Double, distance: Double
        var settled: Bool { abs(target - value) <= distance && abs(velocity) <= 0.025 }
        mutating func step(_ seconds: Double) {
            if settled { value = target; velocity = 0; return }
            let acceleration = frequency * frequency * (target - value) - 2 * damping * frequency * velocity
            velocity += acceleration * seconds; value += velocity * seconds
            if !value.isFinite || !velocity.isFinite || settled { value = target; velocity = 0 }
        }
    }
    private var channels: [Channel] = [
        .init(value: 120, target: 120, frequency: 20, damping: 0.92, distance: 0.025),
        .init(value: 12, target: 12, frequency: 20, damping: 0.92, distance: 0.025),
        .init(value: 6, target: 6, frequency: 20, damping: 0.92, distance: 0.025),
        .init(value: 0, target: 0, frequency: 26, damping: 1, distance: 0.018),
        .init(value: 0, target: 0, frequency: 24, damping: 1, distance: 0.012),
        .init(value: 0, target: 0, frequency: 24, damping: 1, distance: 0.012),
        .init(value: 0, target: 0, frequency: 24, damping: 1, distance: 0.012),
    ]
    public var width: Double { max(1, channels[0].value) }
    public var height: Double { max(1, channels[1].value) }
    public var radius: Double { min(max(0, channels[2].value), min(width, height) / 2) }
    public var opacity: Double { min(1, max(0, channels[3].value)) }
    public var compact: Double { min(1, max(0, channels[4].value)) }
    public var drag: Double { min(1, max(0, channels[5].value)) }
    public var expanded: Double { min(1, max(0, channels[6].value)) }
    public var animating: Bool { channels.contains { !$0.settled } }
    public init() {}
    public mutating func target(_ geometry: IslandGeometry, state: IslandState, reduced: Bool) {
        let visible = state != .hidden && state != .dismissing
        let values = [geometry.width, geometry.height, geometry.radius, visible ? 1.0 : 0,
                      state == .compact ? 1 : 0, state == .dragApproaching || state == .dragReady ? 1 : 0, state == .expanded ? 1 : 0]
        for index in channels.indices {
            channels[index].target = values[index]
            if reduced { channels[index].value = values[index]; channels[index].velocity = 0 }
        }
    }
    public mutating func step(seconds: Double) {
        guard seconds.isFinite, seconds > 0 else { return }
        var remaining = min(0.25, max(0.001, seconds))
        while remaining > 0 {
            let step = min(1.0 / 120, remaining)
            for index in channels.indices { channels[index].step(step) }; remaining -= step
        }
    }
}
