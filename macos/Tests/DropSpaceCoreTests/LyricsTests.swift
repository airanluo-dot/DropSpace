import Foundation
import Testing
import DropSpaceCore

// Inputs and expected outputs from frozen LyricsParserTests, with hostile-input
// checks for the native XML adapter. These do not claim provider/network coverage.
@Test func lyricsTimingRetainsBoundariesAndTranslationIdentity() throws {
    for (offset, count) in [(-3000, 1), (-2000, 1), (-1500, 2), (0, 2), (500, 2)] {
        let doc = try LyricsParser.parse("[offset:\(offset)]\n[00:01]first\n[00:02]\n[00:10]second", provider: .localLrc)
        #expect(doc.lines.count == count)
        if count == 1 { #expect(doc.lines[0].text == "second") }
        else { #expect(doc.lines[0].end == 2 + Double(offset) / 1000) }
        #expect(LyricsTimeline.frame(doc, position: 5, delayMilliseconds: 0).lineIndex == nil)
    }
    let duplicate = try LyricsParser.parse("[0,0](0,0,0)Composer credit\n[0,0](0,0,0)Writer credit\n[0,3000](0,3000,0)first\n[4000,2000](4000,2000,0)second",
                                          provider: .netEase, secondary: "[00:00.000]translated first\n[00:04.000]translated second")
    #expect(duplicate.lines.map(\.secondary) == [nil, nil, "translated first", "translated second"])
    let repeated = try LyricsParser.parse("[01:02:03.500][01:02:13.500]<01:02:03.500>hour<01:02:04.500>word", provider: .netEase)
    #expect(repeated.lines.map(\.start) == [3723.5, 3733.5])
    #expect(repeated.lines[1].words[0].end == 3734.5)
    let final = try LyricsParser.parse("[00:00]first\n[00:02]last", provider: .netEase)
    #expect(LyricsTimeline.frame(final, position: 7, delayMilliseconds: 0).lineIndex == nil)
    #expect(LyricsTimeline.frame(final, position: 1, delayMilliseconds: 1000).lineIndex == 1)
}

@Test func lyricsTtmlPreservesDialectRubyAndLanguageProvenance() throws {
    let xml = #"<tt xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:tts="http://www.w3.org/ns/ttml#styling" xml:lang="zh"><body><p begin="20s" end="25s"><span begin="20s" end="21s">你</span><span ttm:role="x-translation" xml:lang="en-US">you</span><span tts:ruby="container"><span tts:ruby="base">好</span><span tts:ruby="textContainer"><span tts:ruby="text">hao</span></span></span></p></body></tt>"#
    let line = try #require(LyricsParser.parse(xml, provider: .amll).lines.first)
    #expect(line.text == "你好" && line.secondary == "you")
    #expect(line.sourceLanguage == "zh-Hans" && line.translationLanguage == "en" && line.translationLanguageIsExplicit == true)
    #expect(line.words == [LyricWord("你", start: 20, end: 21)])
    for parent in [1, 2] {
        let relative = try LyricsParser.parse("<tt><body><p begin=\"\(parent)s\" dur=\"10s\"><span begin=\"2s\" end=\"3s\">word</span></p></body></tt>", provider: .amll, timing: .parentRelative)
        #expect(relative.lines[0].words == [LyricWord("word", start: Double(parent + 2), end: Double(parent + 3))])
    }
    for role in ["x-roman", "x-transliteration", "x-romanization", "pinyin"] {
        let roman = try LyricsParser.parse("<tt><body><p begin=\"1s\" end=\"3s\">原文<span role=\"\(role)\" xml:lang=\"en\">genbun</span></p></body></tt>", provider: .amll)
        #expect(roman.lines[0].secondary == "genbun" && roman.lines[0].translationOrigin == .none && roman.lines[0].translationLanguage == nil)
    }
    for spans in [#"<span role="x-translation">Hello</span>"#, #"<span role="x-translation" xml:lang="en">Hello<span xml:lang="fr">bonjour</span></span>"#] {
        let unknown = try LyricsParser.parse("<tt xml:lang=\"en\"><body><p begin=\"1s\" end=\"3s\">原文\(spans)</p></body></tt>", provider: .amll)
        #expect(unknown.lines[0].translationOrigin == .provider && unknown.lines[0].translationLanguage == nil)
    }
}

@Test func lyricsParserRejectsEntitiesAndLimitsAndKeepsBodyQuality() throws {
    #expect(throws: (any Error).self) { try LyricsParser.parse(#"<!DOCTYPE tt [<!ENTITY stolen SYSTEM "file:///etc/passwd">]><tt><body><p>&stolen;</p></body></tt>"#, provider: .amll) }
    #expect(throws: (any Error).self) { try LyricsParser.parse("<tt>" + String(repeating: "<span>", count: 65) + "x" + String(repeating: "</span>", count: 65) + "</tt>", provider: .amll) }
    #expect(throws: (any Error).self) { try LyricsParser.parse("x", provider: .localLrc, secondary: String(repeating: "x", count: LyricsParser.maximumCharacters + 1)) }
    let placeholder = try LyricsParser.parse("[00:01]纯音乐，请欣赏", provider: .netEase)
    #expect(placeholder.lines.isEmpty && placeholder.bodyQuality == .placeholderOnly)
    let plain = try LyricsParser.parse("first\n\nsecond", provider: .lrclib).binding(.init(title: "Song", artist: "Artist", album: "", duration: 180, score: 12), to: .init(title: "Song", duration: 180))
    #expect(plain.lines[0].text == "first\n\nsecond" && plain.lines[0].end == 180)
    #expect(LyricsTextPolicy.isCredit("作词：Writer\n作曲：Composer"))
    #expect(!LyricsTextPolicy.isCredit("I wrote this song for you"))
}

@Test func lyricsMatcherRetainsFrozenRecordingIdentity() {
    let chinese = LyricsQuery(title: "小幸运", artist: "田馥甄", duration: 260)
    #expect(LyricsMatcher.score(chinese, title: "小幸運", artist: "田馥甄", album: "", duration: 260) > 4)
    #expect(!LyricsMatcher.areTitlesEquivalent("乾杯", "干杯"))
    for (title, artist, duration) in [("小幸運 (Live)", "田馥甄", 260.0), ("小幸運", "Other Artist", 260), ("小幸運", "田馥甄", 100)] {
        #expect(LyricsMatcher.score(chinese, title: title, artist: artist, album: "", duration: duration) == 0)
    }
    let featured = LyricsQuery(title: "Midnight Drive (feat. Guest)", artist: "Artist", duration: 258)
    #expect(LyricsMatcher.score(featured, title: "Midnight Drive - 2024 Remastered", artist: "Artist; Guest", album: "Deluxe Reissue", duration: 258) > 4)
    #expect(LyricsMatcher.score(featured, title: "Midnight Drive", artist: "Artist", album: "", duration: 258) == 0)
    #expect(LyricsMatcher.searchTitle("Midnight Drive (feat. Guest) - 网易云音乐") == "Midnight Drive")
    #expect(!LyricsMatcher.areTitlesEquivalent("爱与诚 [国语版]", "愛與誠 [粵語版]"))
    // Older historical tests expected fuzzy spelling acceptance; the production
    // baseline now requires exact comparable titles and rejects this recording.
    #expect(LyricsMatcher.score(.init(title: "Beautiful Night", artist: "Artist", duration: 201), title: "Beautful Night", artist: "Artist", album: "", duration: 201) == 0)
    #expect(LyricsMatcher.score(.init(title: "Song"), title: "Song", artist: "Artist", album: "Album", duration: 180) == 0)
}
