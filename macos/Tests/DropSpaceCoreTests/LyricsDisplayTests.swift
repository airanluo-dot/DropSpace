import Foundation
import Testing
@testable import DropSpaceCore

@Test func compactLyricsMatchesFrozenDisplayPreviewAndMarquee() throws {
    struct Fixture: Decodable {
        struct Line: Decodable { var start: Double, end: Double, text: String; var words: [Word] }
        struct Word: Decodable { var text: String; var start: Double, end: Double }
        struct Presentation: Decodable {
            var position: Double; var delay: Int
            var index: Int, IsWaiting: Bool, IsInterlude: Bool, next: Int, frameIndex: Int, WordIndex: Int, WordProgress: Double
        }
        struct Offset: Decodable { var wordSynced: Bool; var position: Double; var delay: Int; var width: Double, viewport: Double, highlight: Double, expected: Double }
        struct Session: Decodable {
            struct Layout: Decodable {
                var TrackIdentity: String, LineStartTicks: Double, Text: String
                var FontSize: Double, RasterizationScale: Double, TextWidth: Double, ViewportWidth: Double
                var Enabled: Bool, ReducedMotion: Bool
            }
            var position: Double, layout: Layout, expected: Double
        }
        struct Compact: Decodable { var text: String, expected: String }
        var baseline: String, lines: [Line], presentations: [Presentation], offsets: [Offset], sessions: [Session], compact: [Compact]
    }
    let url = try #require(Bundle.module.url(forResource: "lyrics-display-baseline", withExtension: "json", subdirectory: "Fixtures"))
    let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url))
    #expect(fixture.baseline == "1cd4620dd58a67b0963b73487743137813b8864a")
    let document = LyricsDocument(lines: fixture.lines.map {
        .init(start: $0.start, end: $0.end, text: $0.text, words: $0.words.map { .init($0.text, start: $0.start, end: $0.end) })
    }, provider: .localLrc)
    for row in fixture.presentations {
        let frame = LyricsTimeline.frame(document, position: row.position, delayMilliseconds: row.delay)
        let result = LyricsDisplay.presentation(document, position: row.position, delay: row.delay)
        #expect((frame.lineIndex ?? -1) == row.frameIndex)
        #expect((frame.wordIndex ?? -1) == row.WordIndex)
        #expect(abs(frame.wordProgress - row.WordProgress) < 1e-10)
        #expect((result.lineIndex ?? -1) == row.index)
        #expect(result.waiting == row.IsWaiting && result.interlude == row.IsInterlude)
        #expect((LyricsDisplay.nextLine(document, position: row.position, delay: row.delay) ?? -1) == row.next)
    }
    for row in fixture.offsets {
        #expect(abs(LyricsDisplay.primaryOffset(line: document.lines[0], position: row.position, delay: row.delay,
            wordSynced: row.wordSynced, highlightWidth: row.highlight, textWidth: row.width,
            viewportWidth: row.viewport) - row.expected) < 1e-10)
    }
    var marquee = LyricsMarqueeSession()
    for row in fixture.sessions {
        let l = row.layout
        let offset = marquee.update(.init(track: l.TrackIdentity, text: l.Text, start: l.LineStartTicks / 10_000_000,
            fontSize: l.FontSize, scale: l.RasterizationScale, textWidth: l.TextWidth, viewportWidth: l.ViewportWidth,
            enabled: l.Enabled, reduced: l.ReducedMotion), position: row.position)
        #expect(abs(offset - row.expected) < 1e-10, "position \(row.position), actual \(offset), expected \(row.expected)")
    }
    for row in fixture.compact { #expect(LyricsDisplay.compact(row.text) == row.expected) }
    #expect(LyricsDisplay.presentation(document, position: .nan, delay: 0).lineIndex == nil)
    #expect(LyricsMarqueeSession.offset(textWidth: .infinity, viewportWidth: 10, elapsed: 2, enabled: true, reduced: false) == 0)
    #expect(LyricsMarqueeSession.offset(textWidth: 100, viewportWidth: 10, elapsed: .nan, enabled: true, reduced: false) == 0)
}
