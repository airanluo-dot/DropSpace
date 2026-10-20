import Foundation
import Testing
import DropSpaceCore

@Test func lyricPresentationAndQuotaChangesDoNotRestartSourceLookup() {
    let previous = LyricsSettings()
    var display = previous
    display.fontSize = 24; display.glowMode = "music"; display.delayMilliseconds = 500
    display.secondaryLyrics = true; display.cacheMaximumBytes = 0; display.scrolling = false
    #expect(!LyricsReloadPolicy.requiresReload(previous, display))
    var route = display; route.provider = .amll
    #expect(LyricsReloadPolicy.requiresReload(display, route))
    route = display; route.aiModelID = "hy-mt2-7-q8-plain-beta"
    #expect(LyricsReloadPolicy.requiresReload(display, route))
    route = display; route.gpuBackend = "cpu"
    #expect(LyricsReloadPolicy.requiresReload(display, route))
    route = display; route.enabled = false
    #expect(LyricsReloadPolicy.requiresReload(display, route))
}

@Test func wholeTrackLanguageRulesPreserveContentAndRejectStaleAdmission() throws {
    var document = try LyricsParser.parse("[00:00]Song — Artist\n[00:01]作词：Someone\n[00:02]La la la\n[00:03]Hello   beautiful world", provider: .netEase)
    document.match = .init(title: "Song", artist: "Artist", album: "", duration: 180, score: 10, candidateID: "recording", trackIdentity: "player-track")
    #expect(LyricsLanguagePolicy.sample(document, role: .original) == "La la la Hello beautiful world")
    let prediction = LyricsLanguagePrediction(label: "__label__en", score: 0.8, runnerUpScore: 0.1)
    let prepared = LyricsLanguagePolicy.prepare(document, target: "en-US", original: prediction)
    #expect(prepared.translationAdmission?.decision == .originalTarget)
    #expect(!LyricsLanguagePolicy.canTranslate(prepared, target: "en"))
    #expect(LyricsLanguagePolicy.current(prepared, target: "en"))
    let restored = try JSONDecoder().decode(LyricsDocument.self, from: JSONEncoder().encode(prepared))
    #expect(restored.translationAdmission == nil)
    var changed = prepared; changed.lines[3].text += "!"
    #expect(!LyricsLanguagePolicy.current(changed, target: "en"))
    changed = prepared; changed.match?.candidateID = "other"
    #expect(!LyricsLanguagePolicy.current(changed, target: "en"))
    changed = prepared; changed.match?.score = 20; changed.lines[3].words = [.init("Hello", start: 3, end: 4)]
    #expect(LyricsLanguagePolicy.current(changed, target: "en"))
    #expect(!LyricsLanguagePolicy.current(prepared, target: "zh"))
    changed = prepared; changed.lines[3].end = .infinity
    #expect(!LyricsLanguagePolicy.current(changed, target: "en"))
    #expect(!LyricsLanguagePolicy.predictionMatches(prediction, sample: "abcdefg", target: "en"))
    #expect(LyricsLanguagePolicy.predictionMatches(prediction, sample: "abcdefgh", target: "en"))
    #expect(!LyricsLanguagePolicy.predictionMatches(.init(label: "en", score: 0.7, runnerUpScore: 0.51), sample: "abcdefgh", target: "en"))
    #expect(!LyricsLanguagePolicy.predictionMatches(.init(label: "en", score: .nan), sample: "abcdefgh", target: "en"))
    var unverified = document; unverified.match?.score = 3
    #expect(LyricsLanguagePolicy.sample(unverified, role: .original).hasPrefix("Song — Artist"))
}

@Test func wholeTrackProviderProvenanceSurvivesPresentationAndRequiresEvidence() throws {
    var document = try LyricsParser.parse("[00:01]こんにちは世界\n[00:03]最後の歌", provider: .netEase, secondary: "[00:01]Hello beautiful world\n[00:03]The final song")
    #expect(!LyricsLanguagePolicy.trustedProviderTarget(document, target: "en"))
    let unknown = LyricsLanguagePrediction(label: "__label__ja", score: 0.95, runnerUpScore: 0.01)
    let original = LyricsLanguagePolicy.prepare(document, target: "en", original: unknown)
    #expect(original.translationAdmission?.decision == .allowAI)
    document = LyricsLanguagePolicy.prepare(document, target: "en", original: unknown,
        translation: .init(label: "__label__en", score: 0.95, runnerUpScore: 0.01))
    #expect(document.translationAdmission?.decision == .providerTarget)
    #expect(document.lines.allSatisfy { $0.translationLanguage == "en" && $0.translationLanguageIsExplicit == false })
    #expect(LyricsLanguagePolicy.completeTarget(document, target: "en"))
    let revision = LyricsLanguagePolicy.translationRevision(document)
    document.lines[0].originalProviderTranslation = LyricsLanguagePolicy.providerTranslation(document.lines[0])
    document.lines[0].secondary = "AI display text"; document.lines[0].translationOrigin = .localAI
    #expect(LyricsLanguagePolicy.translationRevision(document) == revision)
    #expect(LyricsLanguagePolicy.current(document, target: "en"))
    document.lines[0].originalProviderTranslation?.text = "changed source translation"
    #expect(!LyricsLanguagePolicy.current(document, target: "en"))
    var falseMetadata = try LyricsParser.parse("[00:01]original song", provider: .amll, secondary: "[00:01]日本語のかな")
    falseMetadata.lines[0].translationLanguage = "zh"; falseMetadata.lines[0].translationLanguageIsExplicit = true
    #expect(!LyricsLanguagePolicy.trustedProviderTarget(falseMetadata, target: "zh"))
}
