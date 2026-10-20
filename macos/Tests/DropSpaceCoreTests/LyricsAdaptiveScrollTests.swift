import Foundation
import Testing
@testable import DropSpaceCore

@Test func adaptiveTranslationUsesEachLineWindowAndReplansLateTextWithoutRacing() {
    func layout(start: Double = 10, width: Double = 280, text: String = "Translation", enabled: Bool = true, reduced: Bool = false) -> LyricsMarqueeLayout {
        .init(track: "own-track", text: text, start: start, fontSize: 14, scale: 2, textWidth: width, viewportWidth: 180, enabled: enabled, reduced: reduced)
    }
    var slow = LyricsAdaptiveScrollSession(), fast = LyricsAdaptiveScrollSession()
    #expect(slow.update(layout(), lineEnd: 20, position: 10, timelineRevision: 1).offset == 0)
    #expect(fast.update(layout(), lineEnd: 15, position: 10, timelineRevision: 1).offset == 0)
    let a = slow.update(layout(), lineEnd: 20, position: 12.5, timelineRevision: 1)
    let b = fast.update(layout(), lineEnd: 15, position: 12.5, timelineRevision: 1)
    #expect(a.offset < b.offset && abs(b.offset - 50) < 1e-9)
    #expect(slow.update(layout(), lineEnd: 20, position: 15, timelineRevision: 1).offset == 50)
    #expect(slow.update(layout(), lineEnd: 20, position: 14.8, timelineRevision: 1).offset == 50)
    #expect(slow.update(layout(), lineEnd: 20, position: 18.8, timelineRevision: 1).offset == 100)
    #expect(slow.update(layout(), lineEnd: 20, position: 25, timelineRevision: 1).offset == 100) // no repeat
    #expect(slow.update(layout(), lineEnd: 20, position: 12, timelineRevision: 2).offset == 0) // real seek
    var late = LyricsAdaptiveScrollSession()
    #expect(late.update(layout(), lineEnd: 20, position: 18, timelineRevision: 1).offset == 0)
    #expect(abs(late.update(layout(), lineEnd: 20, position: 19, timelineRevision: 1).offset - 50) < 1e-9)
    #expect(late.update(layout(), lineEnd: 20, position: 20, timelineRevision: 1).offset == 100)
    let impossible = late.update(layout(width: 680, text: "Late replacement"), lineEnd: 20, position: 19, timelineRevision: 1)
    #expect(!impossible.canFinish && impossible.offset == 0)
    let bounded = late.update(layout(width: 680, text: "Late replacement"), lineEnd: 20, position: 20, timelineRevision: 1)
    #expect(!bounded.canFinish && bounded.offset <= LyricsAdaptiveScrollSession.maximumSpeed)
    #expect(late.update(layout(width: 100), lineEnd: 20, position: 19, timelineRevision: 1).canFinish)
    #expect(late.update(layout(reduced: true), lineEnd: 20, position: 15, timelineRevision: 1).offset == 0)
    #expect(late.update(layout(), lineEnd: .nan, position: 15, timelineRevision: 1).offset == 0)
    #expect(late.update(layout(), lineEnd: 10, position: 10, timelineRevision: 1).offset == 0)
}

@Test func lyricTimingProvenanceDistinguishesRealCuesFromWholeTrackTextAndOldCache() throws {
    let plain = try LyricsParser.parse("An authored untimed lyric\nAnother line", provider: .localLrc)
    #expect(plain.lines.count == 1 && plain.lines[0].timeBasis == .untimed)
    let query = LyricsQuery(title: "Own song", duration: 300, trackIdentity: "a")
    let bound = plain.binding(.init(title: query.title, artist: "", album: "", duration: 300, score: 10), to: query)
    #expect(bound.lines[0].end == 300 && bound.lines[0].timeBasis == .untimed)
    let lrc = try LyricsParser.parse("[00:10]An authored timed line\n[00:20]Another timed line", provider: .localLrc)
    #expect(lrc.lines[0].timeBasis == .timed)
    #expect(lrc.lines[1].timeBasis == .estimatedEnd) // baseline's synthetic five-second tail is not a real end cue
    let ttml = try LyricsParser.parse("<tt><body><p begin=\"10s\">Authored line</p><p begin=\"20s\" end=\"30s\">Timed end</p></body></tt>", provider: .amll)
    #expect(ttml.lines[0].timeBasis == .estimatedEnd && ttml.lines[1].timeBasis == .timed)
    var old = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(lrc.lines[0])) as? [String: Any])
    old.removeValue(forKey: "timeBasis")
    let cached = try JSONDecoder().decode(LyricLine.self, from: JSONSerialization.data(withJSONObject: old))
    #expect(cached.timeBasis == nil) // missing provenance must not invent sentence timing
}
