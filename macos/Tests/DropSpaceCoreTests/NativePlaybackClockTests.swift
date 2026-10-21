import Foundation
import Testing
@testable import DropSpaceCore

@Test func nativePlayerClockPreservesSeekAndStopsPollingJitterRewinds() {
    func sample(_ position: Double, _ uptime: Double, playing: Bool = true, track: String = "a") -> MediaSnapshot {
        var result = MediaSnapshot()
        result.sourceID = "com.apple.Music"; result.sourceTrackID = track; result.duration = 300
        result.canSeek = true; result.position = position; result.playing = playing; result.observedUptime = uptime
        return result
    }
    var clock = NativePlaybackClock()
    var current = clock.observe(sample(25, 100))
    #expect(current.interpolatedPosition(uptime: 100.5) == 25.5)
    current = clock.observe(sample(25.99, 101))
    #expect(current.position == 26)
    current = clock.observe(sample(27.005, 102))
    #expect(current.position == 27.005)
    current = clock.observe(sample(27.99, 103))
    #expect(current.position == 28.005)
    // A native backwards observation and a smaller explicit backwards seek both win.
    current = clock.observe(sample(12, 104))
    #expect(current.position == 12)
    clock.didSeek()
    current = clock.observe(sample(12.4, 105))
    #expect(current.position == 12.4)
    current = clock.observe(sample(12.5, 106, playing: false))
    #expect(current.position == 12.5 && current.interpolatedPosition(uptime: 200) == 12.5)
    current = clock.observe(sample(12.5, 210))
    #expect(current.position == 12.5)
    #expect(current.interpolatedPosition(uptime: 400) == 14.5)
    current = clock.observe(sample(12.5, 211))
    current = clock.observe(sample(12.5, 212))
    current = clock.observe(sample(12.5, 213))
    #expect(current.interpolatedPosition(uptime: 213) == 14.5)
    // A real source correction larger than the baseline one-second window wins.
    current = clock.observe(sample(12.6, 220))
    #expect(current.position == 12.6)
    current = clock.observe(sample(0, 221, track: "b"))
    #expect(current.position == 0)
    current.observedAt = .distantFuture
    #expect(current.interpolatedPosition(uptime: 221.5) == 0.5)
    #expect(sample(299.5, 0).interpolatedPosition(uptime: 10) == 300)
    clock.reset()
    #expect(clock.observe(sample(4, 5)).position == 4)

    // External pause is observed after the last displayed frame. Trust the real
    // native position, while keeping the secondary row's independent phase.
    clock.reset()
    _ = clock.observe(sample(49, 100))
    current = clock.observe(sample(49.5, 101, playing: false))
    #expect(current.position == 49.5)
    #expect(current.interpolatedMarqueePosition(uptime: 150) == 50)
    current = clock.observe(sample(49.5, 151, playing: false))
    #expect(current.interpolatedMarqueePosition(uptime: 200) == 50)
    current = clock.observe(sample(49.5, 210))
    #expect(current.interpolatedMarqueePosition(uptime: 210.5) == 50.5)
    clock.didSeek()
    current = clock.observe(sample(49.6, 211))
    #expect(current.interpolatedMarqueePosition(uptime: 211) == 49.6)

    // Reproduces actual Music external pause returning an earlier raw position.
    clock.reset()
    let beforePause = clock.observe(sample(47.1, 300))
    current = clock.observe(sample(46.728, 300.6, playing: false))
    #expect(current.position == 46.728 && current.timelineRevision == beforePause.timelineRevision)
    #expect(abs(current.interpolatedMarqueePosition(uptime: 400) - 47.7) < 1e-9)
    current = clock.observe(sample(46.728, 401, playing: false))
    #expect(abs(current.interpolatedMarqueePosition(uptime: 450) - 47.7) < 1e-9)
    clock.didSeek()
    current = clock.observe(sample(46.5, 451, playing: false))
    #expect(current.position == 46.5 && current.timelineRevision != beforePause.timelineRevision)

    // A one-second polling interval plus source latency can put the rendered
    // anchor more than one second ahead; the raw source interval is authoritative.
    clock.reset()
    _ = clock.observe(sample(35.1, 500))
    let delayed = clock.observe(sample(36.096, 501.09))
    current = clock.observe(sample(36.137, 502.09, playing: false))
    #expect(current.timelineRevision == delayed.timelineRevision && current.position == 36.137)
    #expect(current.interpolatedMarqueePosition(uptime: 550) > 37)
    // A distant external seek combined with a pause still retires the phase.
    clock.reset()
    let beforeJump = clock.observe(sample(36, 600))
    current = clock.observe(sample(12, 601, playing: false))
    #expect(current.timelineRevision != beforeJump.timelineRevision && current.marqueePosition == 12)
}
