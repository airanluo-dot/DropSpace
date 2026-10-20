import Foundation

// Expressions copied from the fixed Beta 7 LyricsLanguagePolicy, not a new detector.
public enum LyricsTextPolicy {
    private static let credit = try! NSRegularExpression(pattern: #"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|词|詞|曲|制作人|製作人|制作|製作|监制|監製|混音|母带|母帶|录音|錄音|演唱|原唱|和声|和聲|吉他|贝斯|貝斯|鼓|钢琴|鋼琴|出品|发行|發行|版权|版權|翻译|翻譯|译者|譯者|词作者|曲作者|lyrics(?: by)?|words(?: by)?|music(?: by)?|written by|composed by|composer|arranged by|arranger|producer|produced by|mixed by|mastered by|vocal(?:s)?|guitar|bass|drums)\s*[:：/／]|^\s*(?:written|composed|arranged|produced|mixed|mastered|lyrics|words|music)\s+by\s+\S"#, options: [.caseInsensitive])
    private static let spacedCredit = try! NSRegularExpression(pattern: #"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|制作人|製作人|编曲|混音|母带|母帶|录音|錄音|演唱|原唱)\s+\S"#, options: [])
    private static let structuredCredit = try! NSRegularExpression(pattern: #"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|制谱|製譜|音乐誊谱|音樂謄譜|乐队|樂隊|管弦乐队|管弦樂隊|演唱|电吉他|電吉他|民谣吉他|民謠吉他|木吉他|录音棚|錄音棚|录音师|錄音師|混音师|混音師|母带制作|母帶製作|出品)\s*(?:(?:lyricist|composer|arranger|music\s+copyist|orchestra|artist|electric\s+guitar|acoustic\s+guitar|recording\s+(?:studio|engineer)|mixing\s+engineer|mastering\s+engineer|produced\s+by)\s*)?[:：]\s*\S"#, options: [.caseInsensitive])
    public static func isCredit(_ text: String) -> Bool {
        let lines = text.components(separatedBy: CharacterSet(charactersIn: "\r\n\u{85}\u{2028}\u{2029}")).map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
        return !lines.isEmpty && lines.allSatisfy { line in
            [credit, spacedCredit, structuredCredit].contains { $0.firstMatch(in: line, range: NSRange(line.startIndex..., in: line)) != nil }
        }
    }
    public static func normalizedLanguage(_ input: String?) -> String? {
        let tag = (input ?? "").trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "_", with: "-").lowercased()
        switch tag {
        case "": return nil
        case "zh", "zh-cn", "zh-sg", "zh-hans", "zh-hans-cn": return "zh-Hans"
        case "zh-tw", "zh-hk", "zh-mo", "zh-hant", "zh-hant-tw": return "zh-Hant"
        default: return tag.components(separatedBy: "-")[0]
        }
    }
    public static func quality(_ lines: [LyricLine], confirmedInstrumental: Bool = false) -> LyricsBodyQuality {
        let body = lines.flatMap { $0.text.components(separatedBy: .newlines) }.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty && !isCredit($0) }
        guard !body.isEmpty else { return confirmedInstrumental ? .confirmedInstrumental : .noLyrics }
        let wrappers = CharacterSet(charactersIn: "[]()【】。.!！")
        return body.allSatisfy { placeholders.contains($0.trimmingCharacters(in: wrappers).trimmingCharacters(in: .whitespacesAndNewlines).lowercased()) } ? .placeholderOnly : .usable
    }
    private static let placeholders: Set<String> = [
        "纯音乐，请欣赏", "纯音乐,请欣赏", "纯音乐请欣赏", "純音樂，請欣賞", "純音樂請欣賞",
        "此歌曲为没有填词的纯音乐，请您欣赏", "此歌曲为没有填词的纯音乐,请您欣赏", "此歌曲为没有填词的纯音乐",
        "暂无歌词", "暫無歌詞", "没有歌词", "純音樂", "纯音乐", "instrumental", "no lyrics", "no lyrics available", "lyrics not available", "lyrics unavailable"
    ]
}
