import Foundation

/// User-approved macOS alternative to Beta 7's repeating 24-point/second marquee.
/// It reveals each translation once within its real line window, then holds the
/// ending. It never assigns invented word timings to translated text.
public struct LyricsAdaptiveScrollSession: Sendable {
    public struct Frame: Equatable, Sendable {
        public var offset: Double
        public var canFinish: Bool
    }
    public static let maximumSpeed = 96.0
    private var layout: LyricsMarqueeLayout?
    private var revision: UInt64 = 0
    private var end = 0.0, started = 0.0, lastPosition = 0.0
    public init() {}
    public mutating func reset() { layout = nil }
    public mutating func update(_ value: LyricsMarqueeLayout, lineEnd: Double, position: Double,
                                timelineRevision: UInt64) -> Frame {
        guard [value.start, lineEnd, position, value.textWidth, value.viewportWidth].allSatisfy(\.isFinite),
              lineEnd > value.start, value.viewportWidth > 0 else { return .init(offset: 0, canFinish: false) }
        if layout != value || end != lineEnd || revision != timelineRevision {
            layout = value; end = lineEnd; revision = timelineRevision
            started = max(value.start, position); lastPosition = position
        }
        // Poll-delayed pause corrections must not restart decorative motion;
        // actual seeks carry a new revision and replan from the new native time.
        lastPosition = max(lastPosition, position)
        let overflow = max(0, value.textWidth - value.viewportWidth)
        guard overflow > 0 else { return .init(offset: 0, canFinish: true) }
        guard value.enabled, !value.reduced else { return .init(offset: 0, canFinish: false) }
        let remaining = max(0, end - started)
        let hold = min(1.2, remaining * 0.15)
        let moving = remaining - 2 * hold
        guard moving > 0 else { return .init(offset: 0, canFinish: false) }
        let speed = min(Self.maximumSpeed, overflow / moving)
        let elapsed = min(moving, max(0, lastPosition - started - hold))
        return .init(offset: min(overflow, elapsed * speed), canFinish: overflow / moving <= Self.maximumSpeed)
    }
}
