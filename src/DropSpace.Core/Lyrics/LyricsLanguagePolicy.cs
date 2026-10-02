using System.Text;
using System.Text.RegularExpressions;

namespace DropSpace.Core.Lyrics;

public enum LyricsLanguageEvidenceKind { Unknown, Lexical, Context, Explicit }

/// <summary>Rule confidence, not a calibrated probability. Only high-confidence evidence can suppress AI.</summary>
public readonly record struct LyricsLanguageEvidence(string? Language, double Confidence,
    LyricsLanguageEvidenceKind Kind = LyricsLanguageEvidenceKind.Unknown)
{
    public bool IsConfident => Language is not null && Confidence >= 0.9;
}

/// <summary>
/// Bounded language evidence for translation admission. Script alone never identifies Han or Latin
/// text. Unknown names, romanization and short Han lines stay unknown; a document majority never
/// suppresses a foreign verse. Credits have neither language evidence nor an inference ID.
/// </summary>
public static class LyricsLanguagePolicy
{
    public const string Version = "lexical-context-eligibility-v3";
    private static readonly Regex Credit = new(@"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|词|詞|曲|制作人|製作人|制作|製作|监制|監製|混音|母带|母帶|录音|錄音|演唱|原唱|和声|和聲|吉他|贝斯|貝斯|鼓|钢琴|鋼琴|出品|发行|發行|版权|版權|翻译|翻譯|译者|譯者|词作者|曲作者|lyrics(?: by)?|words(?: by)?|music(?: by)?|written by|composed by|composer|arranged by|arranger|producer|produced by|mixed by|mastered by|vocal(?:s)?|guitar|bass|drums)\s*[:：/／]|^\s*(?:written|composed|arranged|produced|mixed|mastered|lyrics|words|music)\s+by\s+\S", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Words = new(@"[a-z]+(?:['’][a-z]+)?", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    { "i", "the", "you", "your", "you're", "i'm", "i've", "don't", "doesn't", "isn't", "it's", "we're", "they", "their", "with", "without", "this", "that", "and", "are", "was", "were", "will", "would", "could", "should", "have", "never", "for", "from", "my", "me" };
    // Chinese grammatical phrases, excluding nouns shared with Japanese Han text.
    private static readonly string[] Simplified = ["我会", "你会", "我想", "你想", "我在", "你在", "我要", "你要", "我们", "你们", "他们", "她们", "什么", "这个", "那个", "这样", "没有", "不会", "还在", "在这", "你的", "我的", "他的", "她的", "是你", "是我", "不是", "为了", "因为", "怎么", "让我", "让你", "着你", "过了", "里的", "无论", "爱你", "爱我"];
    private static readonly string[] Traditional = ["我們", "你們", "他們", "她們", "什麼", "這個", "那個", "這樣", "沒有", "不會", "還在", "在這", "為了", "因為", "怎麼", "讓我", "讓你", "著你", "過了", "裡的", "無論", "愛你", "愛我"];

    private static readonly Regex SpacedCredit = new(@"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|制作人|製作人|编曲|混音|母带|母帶|录音|錄音|演唱|原唱)\s+\S", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> DistinctiveEnglish = new(StringComparer.OrdinalIgnoreCase)
    { "the", "you", "your", "you're", "i'm", "i've", "don't", "doesn't", "isn't", "it's", "we're", "they", "their", "with", "without", "this", "that", "would", "could", "should" };
    // A small auditable vocabulary bounds the grammar rule. Unknown Latin words
    // can be another language or romanization, so English cues must not swallow them.
    private static readonly HashSet<string> EnglishLexicalWords = new(StringComparer.OrdinalIgnoreCase)
    { "love", "need", "miss", "want", "wait", "stay", "light", "life", "here", "there", "always", "forever",
        "know", "see", "hear", "feel", "hold", "leave", "come", "go", "home", "heart", "away", "alone", "together",
        "be", "am", "is", "it", "not", "no", "we", "us", "he", "she", "him", "her", "them", "a", "an", "to",
        "of", "in", "on", "at", "as", "so", "but", "or", "only", "again", "all", "one", "two" };

    private static string[] PhysicalLines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool IsCreditLine(string text) => Credit.IsMatch(text) || SpacedCredit.IsMatch(text);
    public static bool IsCredit(string text)
    {
        var lines = PhysicalLines(text);
        return lines.Length > 0 && lines.All(IsCreditLine);
    }

    public static LyricsLanguageEvidence Identify(string text, string? explicitLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        // Untimed providers keep a whole physical document in one display row.
        // A credit header neither classifies nor excludes the lyric body below it.
        text = string.Join(" ", PhysicalLines(text).Where(line => !IsCreditLine(line)));
        if (text.Length == 0) return default;
        if (!string.IsNullOrWhiteSpace(explicitLanguage))
        {
            var language = LyricsTranslationPolicy.NormalizeLanguage(explicitLanguage);
            // Mixed TTML spans are explicitly multilingual; a contradictory inherited tag
            // cannot turn a visibly foreign-script verse into same-language original text.
            return language == "mul" || CompatibleScript(text, language)
                ? new(language, 1, LyricsLanguageEvidenceKind.Explicit) : default;
        }
        var letters = text.EnumerateRunes().Where(Rune.IsLetter).ToArray();
        if (letters.Length == 0) return default;
        var kana = letters.Count(r => r.Value is >= 0x3040 and <= 0x30ff);
        var han = letters.Count(IsHan);
        var latin = letters.Count(r => r.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        var hangul = letters.Count(r => r.Value is >= 0xac00 and <= 0xd7af or >= 0x1100 and <= 0x11ff);
        // A line with a foreign-script phrase must remain eligible as a whole line.
        if (kana >= 2 && kana + han == letters.Length) return new("ja", 0.95, LyricsLanguageEvidenceKind.Lexical);
        if (hangul >= 2 && hangul == letters.Length) return new("ko", 0.95, LyricsLanguageEvidenceKind.Lexical);
        if (han >= 4 && han == letters.Length)
        {
            if (Traditional.Any(text.Contains)) return new("zh-Hant", 0.95, LyricsLanguageEvidenceKind.Lexical);
            if (Simplified.Any(text.Contains)) return new("zh-Hans", 0.95, LyricsLanguageEvidenceKind.Lexical);
            // These simplified forms differ from Japanese kanji; shared characters such
            // inside Japanese Han compounds are never independent language evidence.
            var distinctive = "这们说觉远让听爱风梦时过轻满阳为与语记认顾谁该难边欢飞头经红纸乡渐离".Count(text.Contains);
            if (distinctive >= 2) return new("zh-Hans", 0.92, LyricsLanguageEvidenceKind.Lexical);
            if (distinctive == 1 && text.Contains('的')) return new("zh-Hans", 0.65, LyricsLanguageEvidenceKind.Lexical);
        }
        if (latin == letters.Length)
        {
            var words = Words.Matches(text).Select(m => m.Value.Replace('’', '\'')).ToArray();
            var shortImperative = words.Length == 3 && words[0].Equals("let", StringComparison.OrdinalIgnoreCase) &&
                new[] { "it", "me", "us", "him", "her", "them" }.Contains(words[1], StringComparer.OrdinalIgnoreCase) &&
                new[] { "be", "go" }.Contains(words[2], StringComparer.OrdinalIgnoreCase);
            if (shortImperative || words.Length >= 3 && words.All(word => English.Contains(word) || EnglishLexicalWords.Contains(word)) &&
                words.Any(DistinctiveEnglish.Contains) &&
                words.Where(English.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
                return new("en", 0.95, LyricsLanguageEvidenceKind.Lexical);
        }
        return default;
    }

    public static IReadOnlyList<LyricsLanguageEvidence> SourceEvidence(LyricsDocument document)
    {
        var evidence = document.Lines.Select(l => Identify(l.Text, l.SourceLanguage)).ToArray();
        return AddNeighbourContext(document.Lines.Select(line => line.Text).ToArray(), evidence,
            (before, after) => document.Lines[after].Start - document.Lines[before].End <= TimeSpan.FromSeconds(5));
    }

    private static LyricsLanguageEvidence[] AddNeighbourContext(IReadOnlyList<string> text,
        LyricsLanguageEvidence[] evidence, Func<int, int, bool> adjacent)
    {
        var result = evidence.ToArray();
        for (var i = 0; i < evidence.Length; i++)
        {
            if (evidence[i].IsConfident || evidence[i].Confidence < 0.6) continue;
            var before = i - 1;
            while (before >= 0 && IsCredit(text[before])) before--;
            var after = i + 1;
            while (after < evidence.Length && IsCredit(text[after])) after++;
            // Do not bridge a section gap, an unknown/foreign line or a language change.
            if (before >= 0 && after < evidence.Length && evidence[before].IsConfident && evidence[after].IsConfident &&
                SameSourceLanguage(evidence[i].Language, evidence[before].Language) &&
                SameSourceLanguage(evidence[i].Language, evidence[after].Language) &&
                adjacent(before, i) && adjacent(i, after))
                result[i] = evidence[i] with { Confidence = 0.9, Kind = LyricsLanguageEvidenceKind.Context };
        }
        return result;
    }

    public static bool SameSourceLanguage(string? source, string? target)
    {
        var left = LyricsTranslationPolicy.NormalizeLanguage(source);
        var right = LyricsTranslationPolicy.NormalizeLanguage(target);
        return left.Length > 0 && (left == right || left.StartsWith("zh-", StringComparison.Ordinal) && right.StartsWith("zh-", StringComparison.Ordinal));
    }

    public static int[] EligibleIndices(LyricsDocument document, string targetLanguage)
    {
        if (document.Lines.Count is 0 or > 500) return [];
        var evidence = SourceEvidence(document);
        var indices = Enumerable.Range(0, document.Lines.Count).Where(i =>
            !string.IsNullOrWhiteSpace(document.Lines[i].Text) && !IsCredit(document.Lines[i].Text) &&
            (PhysicalLines(document.Lines[i].Text).Length > 1 || !evidence[i].IsConfident ||
                !SameSourceLanguage(evidence[i].Language, targetLanguage)) &&
            EligibleSegments(document.Lines[i], targetLanguage).Length > 0).ToArray();
        return indices.Sum(i => EligibleSegments(document.Lines[i], targetLanguage).Length) <= 500 ? indices : [];
    }

    // Keep the original display row/ID/timing intact. Only physical lyric segments
    // go to the fixed per-line model template; credits and same-target segments do not.
    public static string[] EligibleSegments(LyricsLine line, string targetLanguage)
    {
        // Preserve blank section boundaries while admitting physical segments. A CRLF
        // is one delimiter, not an artificial blank section between every two lines.
        var text = line.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries);
        var evidence = AddNeighbourContext(text, text.Select(part => Identify(part, line.SourceLanguage)).ToArray(),
            static (_, _) => true);
        return text.Where((part, i) => part.Length > 0 && !IsCreditLine(part) &&
            (!evidence[i].IsConfident || !SameSourceLanguage(evidence[i].Language, targetLanguage))).ToArray();
    }

    internal static bool ProviderTranslationMatches(LyricsLine line, string normalizedTarget)
    {
        // A provider's explicit language tag remains authoritative (including short
        // TTML translations). Untagged multi-line display rows can carry evidence
        // for more than one language without forcing it into the single tag field.
        if (!string.IsNullOrWhiteSpace(line.TranslationLanguage))
            return LyricsTranslationPolicy.NormalizeLanguage(line.TranslationLanguage) == normalizedTarget;
        return ProviderSegmentEvidence(line).Any(evidence => evidence.IsConfident &&
            LyricsTranslationPolicy.NormalizeLanguage(evidence.Language) == normalizedTarget);
    }

    private static IEnumerable<LyricsLanguageEvidence> ProviderSegmentEvidence(LyricsLine line)
    {
        var original = PhysicalLines(line.Text);
        var secondary = PhysicalLines(line.Secondary ?? string.Empty);
        for (var i = 0; i < secondary.Length; i++)
        {
            if (original.Length == secondary.Length && IsCreditLine(original[i])) continue;
            yield return Identify(secondary[i]);
        }
    }

    public static LyricsDocument IdentifyProviderTranslations(LyricsDocument document)
    {
        var candidates = Enumerable.Range(0, document.Lines.Count).Where(i =>
            document.Lines[i].TranslationOrigin == LyricsTranslationOrigin.Provider &&
            !string.IsNullOrWhiteSpace(document.Lines[i].Secondary) && !IsCredit(document.Lines[i].Text)).ToArray();
        if (candidates.Length == 0) return document;
        // Evidence belongs to the individual translated line. A retained name, foreign
        // phrase or unknown line cannot erase a different line's positive evidence.
        // Any matching line then triggers the existing whole-document no-gap-fill rule.
        LyricsLine[]? lines = null;
        for (var c = 0; c < candidates.Length; c++)
        {
            var i = candidates[c];
            if (!string.IsNullOrWhiteSpace(document.Lines[i].TranslationLanguage)) continue;
            var languages = ProviderSegmentEvidence(document.Lines[i]).Where(evidence => evidence.IsConfident)
                .Select(evidence => evidence.Language).Distinct(StringComparer.Ordinal).ToArray();
            // Multiple positive languages stay untagged; the matching policy above
            // evaluates their independent evidence instead of erasing it.
            if (languages.Length != 1) continue;
            lines ??= document.Lines.ToArray();
            lines[i] = lines[i] with { TranslationLanguage = languages[0] };
        }
        return lines is null ? document : document with { Lines = lines };
    }

    private static bool CompatibleScript(string text, string language)
    {
        var letters = text.EnumerateRunes().Where(Rune.IsLetter).ToArray();
        return language switch
        {
            "zh-Hans" or "zh-Hant" => letters.All(IsHan),
            "en" => letters.All(r => r.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z'),
            "ja" => letters.All(r => IsHan(r) || r.Value is >= 0x3040 and <= 0x30ff),
            "ko" => letters.All(r => r.Value is >= 0xac00 and <= 0xd7af or >= 0x1100 and <= 0x11ff),
            _ => false,
        };
    }

    private static bool IsHan(Rune rune) => rune.Value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af;
}
