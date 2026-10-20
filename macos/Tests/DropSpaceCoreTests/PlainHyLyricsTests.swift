import Foundation
import Testing
import DropSpaceCore

@Test func plainHyPromptsAndStrictOutputPreserveFrozenBoundaries() throws {
    #expect(try PlainHyLyrics.prompt("Own source", target: "en") == "将以下文本翻译为英语，注意只需要输出翻译后的结果，不要额外解释：\nOwn source")
    #expect(throws: (any Error).self) { try PlainHyLyrics.prompt(String(repeating: "x", count: 1800), target: "zh") }
    #expect(throws: (any Error).self) { try PlainHyLyrics.prompt("source", target: "fr") }
    #expect(PlainHyLyrics.removeRuntimeTerminator("Result [end of text]\n") == "Result")
    for invalid in ["", "line\nextra", "broken\u{fffd}", "```quoted```", String(repeating: "x", count: 4097)] { #expect(!PlainHyLyrics.completeLine(invalid)) }
    for invalid in ["translateIds: 1", "<|im_start|>text", "[end of text]"] { #expect(!PlainHyLyrics.validTranslation(invalid)) }
    let valid = Data(#"[{"text":"Music \uD834\uDD1E","id":0}]"#.utf8)
    #expect(try PlainHyOutputDecoder.decode(valid) == [.init(id: 0, text: "Music 𝄞")])
    for invalid in [#"[{"id":0,"id":1,"text":"x"}]"#, #"[{"id":0,"text":"x","text":"y"}]"#,
                    #"[{"id":0,"text":"x","extra":true}]"#, #"[{"id":0.0,"text":"x"}]"#, #"[{"id":00,"text":"x"}]"#,
                    #"[{"id":0,"text":"x"},]"#, #"[{"id":0,"text":"\uD800"}]"#] {
        #expect(throws: (any Error).self) { try PlainHyOutputDecoder.decode(Data(invalid.utf8)) }
    }
}

@Test func plainHyMappingReconcilesOccurrenceAndPreservesNativeTiming() throws {
    var source = try LyricsParser.parse("[00:01]原文第一行\n[00:05]原文第一行\n[00:09]作曲：Someone", provider: .netEase, secondary: "[00:01]ancienne traduction")
    source.match = .init(title: "Song", artist: "Artist", album: "", duration: 20, score: 10, candidateID: "same-recording", trackIdentity: "track")
    source = LyricsLanguagePolicy.prepare(source, target: "en", original: .init(label: "zh", score: 0.9, runnerUpScore: 0.01))
    #expect(PlainHyLyrics.eligible(source, target: "en") == [0, 1])
    #expect(PlainHyLyrics.nextLine(source, pending: [0, 1], position: 5.5) == 1)
    let result = try PlainHyLyrics.apply([.init(id: 0, text: "First translated line"), .init(id: 1, text: "First translated line")], to: source, indices: [0, 1], target: "en")
    #expect(result.lines[0].localAIAdmissionKey != result.lines[1].localAIAdmissionKey)
    #expect(result.lines[0].originalProviderTranslation?.text == "ancienne traduction")
    #expect(LyricsLanguagePolicy.translationRevision(result) == LyricsLanguagePolicy.translationRevision(source))
    var current = source; current.lines[0].words = [.init("原文", start: 1, end: 2)]
    let reconciled = PlainHyLyrics.reconcile(current: current, source: source, generated: result, target: "en")
    #expect(reconciled.lines[0].words == current.lines[0].words && reconciled.lines[0].secondary == "First translated line")
    current.lines[0].start = 1.5
    #expect(PlainHyLyrics.reconcile(current: current, source: source, generated: result, target: "en").lines[0].translationOrigin == .provider)
    let copied = try PlainHyLyrics.apply([.init(id: 0, text: "原文第一行")], to: source, indices: [0], target: "en")
    #expect(!PlainHyLyrics.useful(copied, target: "en") && copied.lines[0].secondary == "ancienne traduction")
    var query = LyricsQuery(title: "Song", artist: "Artist", trackIdentity: "track")
    let identity = String(repeating: "a", count: 64)
    let key = try PlainHyLyrics.cacheIdentity(query: query, source: source, target: "en", inference: identity)
    query.bypassProviderResponseCache = true
    #expect(try PlainHyLyrics.cacheIdentity(query: query, source: result, target: "en", inference: identity) == key)
    #expect(throws: (any Error).self) { try PlainHyLyrics.apply([.init(id: 1, text: "wrong occurrence")], to: source, indices: [0], target: "en") }
}
