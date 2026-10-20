import Foundation

/// Adapts fractional, polled public player observations to a monotonic display
/// anchor. The frozen clock's <1 second anti-rewind window also covers fractional
/// macOS script timestamps; explicit seeks and backward source observations win.
public struct NativePlaybackClock: Sendable {
    private var raw: MediaSnapshot?
    private var presented: MediaSnapshot?
    private var explicitSeek = false
    private var revision: UInt64 = 0
    public init() {}
    public mutating func reset() { raw = nil; presented = nil; explicitSeek = false }
    public mutating func didSeek() { explicitSeek = true }
    public mutating func observe(_ incoming: MediaSnapshot) -> MediaSnapshot {
        var result = incoming
        // Music can report a pause position slightly before even its last raw
        // playing sample. Keep that authoritative lyric time, but do not restart
        // decorative scrolling for a bounded playing -> paused correction.
        let pauseCorrection = !explicitSeek && raw?.trackID == incoming.trackID &&
            raw?.playing == true && !incoming.playing && raw?.canSeek == true && incoming.canSeek &&
            raw.map({ incoming.position >= $0.position - 1 &&
                incoming.position <= $0.interpolatedPosition(uptime: incoming.observedUptime) + 1 }) == true
        if explicitSeek || raw?.trackID != incoming.trackID ||
            !pauseCorrection && (raw.map({ incoming.position < $0.position }) == true ||
            presented.map({ abs(incoming.position - $0.interpolatedPosition(uptime: incoming.observedUptime)) >= 1 && incoming.position != raw?.position }) == true) {
            revision &+= 1
        }
        result.timelineRevision = revision
        result.marqueePosition = incoming.position
        if !explicitSeek, let raw, let presented, raw.trackID == incoming.trackID,
           incoming.position >= raw.position || pauseCorrection, raw.canSeek, incoming.canSeek,
           pauseCorrection || abs(incoming.position - presented.interpolatedPosition(uptime: incoming.observedUptime)) < 1 {
            // Public polling notices an external pause after the actual pause.
            // Correct lyric/seek time below, but freeze decorative motion where
            // it was displayed instead of treating that correction as a seek.
            result.marqueePosition = presented.interpolatedMarqueePosition(uptime: incoming.observedUptime)
        }
        if !explicitSeek, let raw, let presented,
           raw.trackID == incoming.trackID, raw.playing, incoming.playing,
           raw.canSeek, incoming.canSeek, incoming.position >= raw.position {
            let interpolated = presented.interpolatedPosition(uptime: incoming.observedUptime)
            if incoming.position == raw.position {
                result.position = presented.position
                result.observedUptime = presented.observedUptime
                result.observedAt = presented.observedAt
                result.marqueePosition = presented.marqueePosition
            } else if interpolated > incoming.position, interpolated - incoming.position < 1 {
                result.position = interpolated
            }
        }
        #if DEBUG
        if ProcessInfo.processInfo.environment["DROPSPACE_VALIDATION_LYRICS_TRACE"] == "1",
           raw?.playing != incoming.playing || presented?.timelineRevision != revision {
            NSLog("DropSpaceLyrics clock raw=%.3f priorRaw=%.3f priorInterpolated=%.3f playing=%d pauseCorrection=%d explicitSeek=%d revision=%llu",
                incoming.position, raw?.position ?? -1, presented?.interpolatedPosition(uptime: incoming.observedUptime) ?? -1,
                incoming.playing ? 1 : 0, pauseCorrection ? 1 : 0, explicitSeek ? 1 : 0, revision)
        }
        #endif
        explicitSeek = false; raw = incoming; presented = result
        return result
    }
}
