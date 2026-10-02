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
    public const string Version = "lexical-context-eligibility-v2";
    private static readonly Regex Credit = new(@"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|词|詞|曲|制作人|製作人|制作|製作|监制|監製|混音|母带|母帶|录音|錄音|演唱|原唱|和声|和聲|吉他|贝斯|貝斯|鼓|钢琴|鋼琴|出品|发行|發行|版权|版權|翻译|翻譯|译者|譯者|词作者|曲作者|lyrics(?: by)?|words(?: by)?|music(?: by)?|written by|composed by|composer|arranged by|arranger|producer|produced by|mixed by|mastered by|vocal(?:s)?|guitar|bass|drums)\s*[:：/／]|^\s*(?:written|composed|arranged|produced|mixed|mastered|lyrics|words|music)\s+by\s+\S", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Words = new(@"[a-z]+(?:['’][a-z]+)?", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    { "the", "you", "your", "you're", "i'm", "i've", "don't", "doesn't", "isn't", "it's", "we're", "they", "their", "with", "without", "this", "that", "and", "are", "was", "were", "will", "would", "could", "should", "have", "never", "for", "from", "my", "me" };
    // Chinese grammatical phrases, excluding nouns shared with Japanese Han text.
    private static readonly string[] Simplified = ["我会", "你会", "我想", "你想", "我在", "你在", "我要", "你要", "我们", "你们", "他们", "她们", "什么", "这个", "那个", "这样", "没有", "不会", "还在", "在这", "你的", "我的", "他的", "她的", "是你", "是我", "不是", "为了", "因为", "怎么", "让我", "让你", "着你", "过了", "里的", "无论", "爱你", "爱我"];
    private static readonly string[] Traditional = ["我們", "你們", "他們", "她們", "什麼", "這個", "那個", "這樣", "沒有", "不會", "還在", "在這", "為了", "因為", "怎麼", "讓我", "讓你", "著你", "過了", "裡的", "無論", "愛你", "愛我"];

    private static readonly Regex SpacedCredit = new(@"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|制作人|製作人|编曲|混音|母带|母帶|录音|錄音|演唱|原唱)\s+\S", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> DistinctiveEnglish = new(StringComparer.OrdinalIgnoreCase)
    { "the", "you", "your", "you're", "i'm", "i've", "don't", "doesn't", "isn't", "it's", "we're", "they", "their", "with", "without", "this", "that", "would", "could", "should" };

    public static bool IsCredit(string text) => Credit.IsMatch(text) || SpacedCredit.IsMatch(text);

    public static LyricsLanguageEvidence Identify(string text, string? explicitLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(text) || IsCredit(text)) return default;
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
            if (words.Length >= 3 && words.Any(DistinctiveEnglish.Contains) &&
                words.Where(English.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
                return new("en", 0.95, LyricsLanguageEvidenceKind.Lexical);
        }
        return default;
    }

    public static IReadOnlyList<LyricsLanguageEvidence> SourceEvidence(LyricsDocument document)
    {
        var evidence = document.Lines.Select(l => Identify(l.Text, l.SourceLanguage)).ToArray();
        var result = evidence.ToArray();
        for (var i = 0; i < evidence.Length; i++)
        {
            if (evidence[i].IsConfident || evidence[i].Confidence < 0.6) continue;
            var before = i - 1;
            while (before >= 0 && IsCredit(document.Lines[before].Text)) before--;
            var after = i + 1;
            while (after < evidence.Length && IsCredit(document.Lines[after].Text)) after++;
            // Do not bridge a section gap, an unknown/foreign line or a language change.
            if (before >= 0 && after < evidence.Length && evidence[before].IsConfident && evidence[after].IsConfident &&
                SameSourceLanguage(evidence[i].Language, evidence[before].Language) &&
                SameSourceLanguage(evidence[i].Language, evidence[after].Language) &&
                document.Lines[i].Start - document.Lines[before].End <= TimeSpan.FromSeconds(5) &&
                document.Lines[after].Start - document.Lines[i].End <= TimeSpan.FromSeconds(5))
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
        return Enumerable.Range(0, document.Lines.Count).Where(i =>
            !string.IsNullOrWhiteSpace(document.Lines[i].Text) && !IsCredit(document.Lines[i].Text) &&
            (!evidence[i].IsConfident || !SameSourceLanguage(evidence[i].Language, targetLanguage))).ToArray();
    }

    public static LyricsDocument IdentifyProviderTranslations(LyricsDocument document)
    {
        var candidates = Enumerable.Range(0, document.Lines.Count).Where(i =>
            document.Lines[i].TranslationOrigin == LyricsTranslationOrigin.Provider &&
            !string.IsNullOrWhiteSpace(document.Lines[i].Secondary) && !IsCredit(document.Lines[i].Text)).ToArray();
        if (candidates.Length == 0) return document;
        var evidence = candidates.Select(i => Identify(document.Lines[i].Secondary!, document.Lines[i].TranslationLanguage)).ToArray();
        var languages = evidence.Where(e => e.IsConfident).Select(e => e.Language).Distinct().ToArray();
        // Mixed inferred translation languages cannot create a whole-song bypass. Existing
        // explicit TTML tags remain authoritative. Unknown short lines do not acquire a tag;
        // a positively identified translation elsewhere still enables the existing no-gap-fill rule.
        if (languages.Length != 1) return document;
        var language = languages[0]!;
        if (candidates.Any(i => !CompatibleScript(document.Lines[i].Secondary!, language))) return document;
        LyricsLine[]? lines = null;
        for (var c = 0; c < candidates.Length; c++)
        {
            var i = candidates[c];
            if (!evidence[c].IsConfident || !string.IsNullOrWhiteSpace(document.Lines[i].TranslationLanguage)) continue;
            lines ??= document.Lines.ToArray();
            lines[i] = lines[i] with { TranslationLanguage = language };
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
