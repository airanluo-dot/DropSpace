import Foundation
import Testing
@testable import DropSpaceCore

@Test func glowMatchesFrozenEligibilityAndContinuousEnvelope() throws {
    let url = try #require(Bundle.module.url(forResource: "glow-baseline", withExtension: "json", subdirectory: "Fixtures"))
    let root = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    #expect(root["baseline"] as? String == "1cd4620dd58a67b0963b73487743137813b8864a")
    for row in try #require(root["eligibility"] as? [[String: Any]]) {
        #expect(LyricsGlowPolicy.eligible(mode: ["off","aiLyrics","music"][try #require(row["mode"] as? Int)],
            playing: try #require(row["playing"] as? Bool), islandVisible: try #require(row["visible"] as? Bool),
            translationVisible: try #require(row["shown"] as? Bool),
            origin: [.none, .provider, .localAI][try #require(row["origin"] as? Int)],
            translation: row["text"] as? String) == row["expected"] as? Bool)
    }
    var envelope = LyricsGlowEnvelope(), audio = LyricsGlowAudioResponse()
    for row in try #require(root["steps"] as? [[String: Any]]) {
        let eligible = try #require(row["eligible"] as? Bool), reduced = try #require(row["reduced"] as? Bool)
        let seconds = try #require(row["seconds"] as? Double)
        if eligible && !reduced {
            audio.advance(energy: try #require(row["energy"] as? Double), bands: try #require(row["bands"] as? [Double]), seconds: seconds)
        } else { audio.reset() }
        envelope.advance(eligible: eligible, energy: audio.energy, seconds: seconds, reducedMotion: reduced,
                         bands: audio.bands, simplified: try #require(row["simplified"] as? Bool))
        let state = try #require(row["state"] as? [String: Double])
        let expected = try ["Brightness","Phase","Simplification","Band0","Band1","Band2","Band3","Band4","Band5"].map { try #require(state[$0]) }
        let actual = [envelope.state.brightness, envelope.state.phase, envelope.state.simplification] + envelope.state.bands
        for (a, b) in zip(actual, expected) { #expect(abs(a - b) < 1e-10) }
        #expect(abs(audio.energy - (try #require(row["audioEnergy"] as? Double))) < 1e-10)
        for (a, b) in zip(audio.bands, try #require(row["audioBands"] as? [Double])) { #expect(abs(a - b) < 1e-10) }
    }
    let before = envelope.state
    envelope.advance(eligible: true, energy: 1, seconds: .nan)
    #expect(envelope.state == before)
    var corrupt = LyricsGlowVisualState()
    corrupt.brightness = .infinity; corrupt.phase = -.infinity; corrupt.bands = [.nan, 2, -1]
    envelope.restore(corrupt)
    #expect(envelope.state.brightness == 0 && envelope.state.phase == 0)
    #expect(envelope.state.bands == [0, 1, 0, 0, 0, 0])
    #expect(LyricsGlowPolicy.aiEnabledChanged(true) == "aiLyrics")
    #expect(LyricsGlowPolicy.aiEnabledChanged(false) == "off")
}

@Test func glowTranslationDisplayFencesTargetAndPreservesSource() {
    var line = LyricLine(start: 0, end: 10, text: "First\r\nsecond", secondary: "The river.")
    line.translationOrigin = .localAI; line.translationLanguage = "en-US"
    #expect(LyricsDisplay.secondary(line, target: "en", enabled: true, showAI: true) == "AI · The river.")
    #expect(LyricsDisplay.secondary(line, target: "en", enabled: true, showAI: false) == "The river.")
    #expect(LyricsDisplay.secondary(line, target: "zh", enabled: true, showAI: true) == nil)
    #expect(LyricsDisplay.secondary(line, target: "en", enabled: false, showAI: true) == nil)
    line.translationLanguage = nil
    #expect(LyricsDisplay.secondary(line, target: "en", enabled: true, showAI: true) == nil)
    line.translationOrigin = .provider; line.secondary = "河流"
    #expect(LyricsDisplay.secondary(line, target: "en", enabled: true, showAI: true) == nil)
    #expect(LyricsDisplay.secondary(line, target: "zh", enabled: true, showAI: true) == "河流")
    #expect(LyricsDisplay.compact(line.text) == "First second")
    #expect(line.text == "First\r\nsecond")
}
