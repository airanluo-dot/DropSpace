using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DropSpace.Core.Lyrics;

public enum LyricsLanguageEvidenceKind { Unknown, Lexical, Context, Explicit }
public enum LyricsTranslationAdmission { SameLanguage, Translate, Abstain }

/// <summary>Rule confidence, not a calibrated probability. Admission separately handles ambiguous Han.</summary>
public readonly record struct LyricsLanguageEvidence(string? Language, double Confidence,
    LyricsLanguageEvidenceKind Kind = LyricsLanguageEvidenceKind.Unknown)
{
    public bool IsConfident => Language is not null && Confidence >= 0.9;
}

/// <summary>
/// Bounded language evidence for translation admission. Script alone never identifies Han or Latin
/// text. Unknown names and romanization stay unknown. For a Chinese target, ambiguous pure Han
/// abstains without acquiring a language identity. Credits have no inference ID.
/// </summary>
public static class LyricsLanguagePolicy
{
    public const string Version = "han-abstention-eligibility-v8";
    private static readonly Regex ArtistNames = new(@"[,，、;&＆；]|\s+[/／]\s+", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Credit = new(@"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|词|詞|曲|制作人|製作人|制作|製作|监制|監製|混音|母带|母帶|录音|錄音|演唱|原唱|和声|和聲|吉他|贝斯|貝斯|鼓|钢琴|鋼琴|出品|发行|發行|版权|版權|翻译|翻譯|译者|譯者|词作者|曲作者|lyrics(?: by)?|words(?: by)?|music(?: by)?|written by|composed by|composer|arranged by|arranger|producer|produced by|mixed by|mastered by|vocal(?:s)?|guitar|bass|drums)\s*[:：/／]|^\s*(?:written|composed|arranged|produced|mixed|mastered|lyrics|words|music)\s+by\s+\S", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Words = new(@"[a-z]+(?:['’][a-z]+)?", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    // Keep clause boundaries until each clause has supplied its own evidence.
    // Apostrophes and word-internal hyphens remain part of the same lyric unit.
    private static readonly Regex Clauses = new("[,，;；:：.!?。！？…—–/／|｜•·()（）\\[\\]{}\"“”«»\\r\\n\\u0085\\u2028\\u2029]+|(?<!\\p{L})['‘’]|['‘’](?!\\p{L})",
        RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    { "i", "the", "you", "your", "you're", "i'm", "i've", "don't", "doesn't", "isn't", "it's", "we're", "they", "their", "with", "without", "this", "that", "and", "are", "was", "were", "will", "would", "could", "should", "have", "never", "for", "from", "my", "me" };
    // Chinese grammatical phrases, excluding nouns shared with Japanese Han text.
    private static readonly string[] Simplified = ["我会", "你会", "我想", "你想", "我在", "你在", "我要", "你要", "我们", "你们", "他们", "她们", "什么", "这个", "那个", "这样", "没有", "不会", "还在", "在这", "你的", "我的", "他的", "她的", "是你", "是我", "不是", "为了", "因为", "怎么", "让我", "让你", "着你", "过了", "里的", "无论", "爱你", "爱我"];
    private static readonly string[] Traditional = ["我們", "你們", "他們", "她們", "什麼", "這個", "那個", "這樣", "沒有", "不會", "還在", "在這", "為了", "因為", "怎麼", "讓我", "讓你", "著你", "過了", "裡的", "無論", "愛你", "愛我"];

    private static readonly Regex SpacedCredit = new(@"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|制作人|製作人|编曲|混音|母带|母帶|录音|錄音|演唱|原唱)\s+\S", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> DistinctiveEnglish = new(StringComparer.OrdinalIgnoreCase)
    { "the", "you", "your", "you're", "i'm", "i've", "don't", "doesn't", "isn't", "it's", "we're", "they", "their", "with", "without", "this", "that", "would", "could", "should" };
    // Positive English function-word evidence permits open content vocabulary.
    // Only positive foreign phrase evidence can contradict it; a new noun/verb
    // is not itself evidence of another language. These bounded multi-word cues
    // retain mixed/romanized clauses without treating every Latin word as English.
    private static readonly Regex ForeignLatinPhrase = new(
        @"\b(?:je\s+(?:t'aime|suis|veux|te|ne)|tu\s+(?:es|vas)|nous\s+(?:sommes|avons)|vous\s+(?:etes|avez)|(?:mon|ton)\s+amour|la\s+vie\s+est|" +
        @"mi\s+amor|te\s+(?:amo|quiero)|yo\s+(?:soy|quiero)|sin\s+ti|ich\s+(?:bin|liebe|will)|wir\s+(?:sind|haben)|eu\s+te\s+amo|" +
        @"(?:wo|ni|ta|nimen|tamen)\s+(?:(?:hen|bu|mei|hai|ye|dou|zheng|zhen\s+de|yi\s+zhi)\s+){0,3}(?:ai|yao|zai|shi|xiang|deng|hui|neng|kan|ting|xi\s+huan)\b|" +
        @"(?:kimi|anata|watashi|boku)\s+(?:no|wa|o|ga|ni|to|de|mo)\s+[a-z]+|no\s+na\s+wa|(?:ai|koi)\s+no|aishiteru|saranghae(?:yo)?)\b",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

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
        text = string.Join("\n", PhysicalLines(text).Where(line => !IsCreditLine(line)));
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
            var distinctive = "这们说觉远让听爱风梦时过轻满阳为与语记认顾谁该难边欢飞头经红纸乡渐离约".Count(text.Contains);
            if (distinctive >= 2) return new("zh-Hans", 0.92, LyricsLanguageEvidenceKind.Lexical);
            if (distinctive == 1 && text.Contains('的')) return new("zh-Hans", 0.65, LyricsLanguageEvidenceKind.Lexical);
        }
        if (latin == letters.Length)
        {
            var clauses = Clauses.Split(text).Where(part => part.Any(char.IsLetter)).ToArray();
            if (clauses.Length > 0 && clauses.All(HasEnglishClauseEvidence))
                return new("en", 0.95, LyricsLanguageEvidenceKind.Lexical);
        }
        return default;
    }

    private static bool HasEnglishClauseEvidence(string text)
    {
        var words = Words.Matches(text).Select(m => m.Value.Replace('’', '\'')).ToArray();
        // Ordered foreign grammatical components also contradict English in an
        // unpunctuated mixed clause. A lone unknown content word does not.
        if (ForeignLatinPhrase.IsMatch(string.Join(" ", words))) return false;
        var shortImperative = words.Length == 3 && words[0].Equals("let", StringComparison.OrdinalIgnoreCase) &&
            new[] { "it", "me", "us", "him", "her", "them" }.Contains(words[1], StringComparer.OrdinalIgnoreCase) &&
            new[] { "be", "go" }.Contains(words[2], StringComparer.OrdinalIgnoreCase);
        return shortImperative || words.Length >= 3 && words.Any(DistinctiveEnglish.Contains) &&
            words.Where(English.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
    }

    public static IReadOnlyList<LyricsLanguageEvidence> SourceEvidence(LyricsDocument document)
    {
        var evidence = document.Lines.Select(line => Identify(line.Text, line.SourceLanguage)).ToArray();
        return AddNeighbourContext(document.Lines.Select(line => line.Text).ToArray(), evidence,
            (before, after) => document.Lines[after].Start - document.Lines[before].End <= TimeSpan.FromSeconds(5));
    }

    private static string[][] SourceParts(LyricsDocument document)
    {
        var parts = document.Lines.Select(line => line.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries)).ToArray();
        // Some providers retain an artist/title header as the first original row. Require
        // accepted track metadata; an arbitrary Latin phrase must never become a header.
        if (document.Match is { Score: >= 4 } match)
        {
            var title = LyricsMatcher.Normalize(match.Title);
            var artist = LyricsMatcher.Normalize(match.Artist);
            if (parts.Length > 0 && parts[0].Length > 0 && parts[0][0].Length <= 2048 && title.Length > 0 && artist.Length > 0)
            {
                var header = parts[0][0];
                for (var i = 0; i < header.Length; i++)
                {
                    if (header[i] is not ('-' or '–' or '—')) continue;
                    var left = LyricsMatcher.Normalize(header[..i]);
                    var right = LyricsMatcher.Normalize(header[(i + 1)..]);
                    if ((left == artist && right == title) || (left == title && right == artist))
                    { parts[0][0] = string.Empty; break; }
                }
            }
            // A colon alone is not performer evidence (e.g. a sung "Why:"). Require
            // the complete label to name an artist in the accepted provider identity.
            var names = ArtistNames.Split(match.Artist).Select(name => name.Trim())
                .Append(match.Artist.Trim()).Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in parts)
                for (var i = 0; i < row.Length; i++)
                    if (row[i].Length > 1 && (row[i][^1] is ':' or '：') && names.Contains(row[i][..^1].Trim()))
                        row[i] = string.Empty;
        }
        return parts;
    }

    private static LyricsLanguageEvidence[] AddNeighbourContext(IReadOnlyList<string> text,
        LyricsLanguageEvidence[] evidence, Func<int, int, bool> adjacent)
    {
        var result = evidence.ToArray();
        for (var i = 0; i < evidence.Length; i++)
        {
            if (evidence[i].IsConfident) continue;
            var before = i - 1;
            while (before >= 0 && IsCredit(text[before])) before--;
            var after = i + 1;
            while (after < evidence.Length && IsCredit(text[after])) after++;
            // Do not bridge a section gap, an unknown/foreign line or a language change.
            if (before < 0 || after >= evidence.Length || !evidence[before].IsConfident || !evidence[after].IsConfident ||
                !adjacent(before, i) || !adjacent(i, after)) continue;
            if (evidence[i].Confidence >= 0.6 && SameSourceLanguage(evidence[i].Language, evidence[before].Language) &&
                SameSourceLanguage(evidence[i].Language, evidence[after].Language))
                result[i] = evidence[i] with { Confidence = 0.9, Kind = LyricsLanguageEvidenceKind.Context };
            else if (evidence[i].Language is null && IsPureHan(text[i]) &&
                evidence[before].Language == "ja" && evidence[after].Language == "ja")
                // Two independent immediate Japanese neighbours support only this Han unit.
                // Never propagate one foreign verse through a song or across a blank/gap.
                result[i] = new("ja", 0.9, LyricsLanguageEvidenceKind.Context);
        }
        return result;
    }

    public static bool SameSourceLanguage(string? source, string? target)
    {
        var left = LyricsTranslationPolicy.NormalizeLanguage(source);
        var right = LyricsTranslationPolicy.NormalizeLanguage(target);
        return left.Length > 0 && (left == right || left.StartsWith("zh-", StringComparison.Ordinal) && right.StartsWith("zh-", StringComparison.Ordinal));
    }

    public static LyricsTranslationAdmission GetAdmission(string text, string targetLanguage, LyricsLanguageEvidence evidence)
    {
        if (evidence.IsConfident && SameSourceLanguage(evidence.Language, targetLanguage))
            return LyricsTranslationAdmission.SameLanguage;
        // Script is a reason to abstain, not to assert that ambiguous Han is Chinese.
        // Explicit Japanese and bounded positive Japanese context still permit translation.
        if ((!evidence.IsConfident || evidence.Language == "mul") &&
            LyricsTranslationPolicy.NormalizeLanguage(targetLanguage).StartsWith("zh-", StringComparison.Ordinal) &&
            IsPureHan(text)) return LyricsTranslationAdmission.Abstain;
        return LyricsTranslationAdmission.Translate;
    }

    public static int[] EligibleIndices(LyricsDocument document, string targetLanguage)
    {
        if (document.Lines.Count is 0 or > 500) return [];
        var segments = EligibleSegments(document, targetLanguage);
        var indices = Enumerable.Range(0, document.Lines.Count).Where(i =>
            !string.IsNullOrWhiteSpace(document.Lines[i].Text) && !IsCredit(document.Lines[i].Text) &&
            segments[i].Length > 0).ToArray();
        return indices.Sum(i => segments[i].Length) <= 500 ? indices : [];
    }

    // Keep the original display row/ID/timing intact. Only physical lyric segments
    // go to the fixed per-line model template; credits and same-target segments do not.
    public static string[] EligibleSegments(LyricsLine line, string targetLanguage) =>
        EligibleSegments(new LyricsDocument([line], Models.LyricsProviderKind.LocalLrc), targetLanguage)[0];

    public static IReadOnlyList<string[]> EligibleSegments(LyricsDocument document, string targetLanguage)
    {
        // Preserve blank section boundaries while admitting physical segments. A CRLF
        // is one delimiter, not an artificial blank section between every two lines.
        var parts = SourceParts(document);
        var rows = SourceEvidence(document);
        return parts.Select((text, id) =>
        {
            var evidence = AddNeighbourContext(text, text.Select(part => part.Length == 0 ? default :
                Identify(part, document.Lines[id].SourceLanguage)).ToArray(), static (_, _) => true);
            if (text.Length == 1) evidence[0] = rows[id];
            return text.Where((part, i) => part.Length > 0 && !IsCreditLine(part) &&
                GetAdmission(part, targetLanguage, evidence[i]) == LyricsTranslationAdmission.Translate).ToArray();
        }).ToArray();
    }

    public static LyricsDocument RemoveIneligibleLocalTranslations(LyricsDocument document, string targetLanguage)
    {
        if (!document.Lines.Any(line => line.TranslationOrigin == LyricsTranslationOrigin.LocalAi)) return document;
        var segments = EligibleSegments(document, targetLanguage);
        LyricsLine[]? lines = null;
        for (var i = 0; i < document.Lines.Count; i++)
        {
            if (document.Lines[i].TranslationOrigin != LyricsTranslationOrigin.LocalAi) continue;
            // Existing derived AI text has no segment ownership. If any original segment
            // is now excluded, discard the whole secondary instead of cropping/reviving it.
            if (segments[i].Length > 0 && (segments[i].Length == PhysicalLines(document.Lines[i].Text).Length ||
                document.Lines[i].LocalAiAdmissionKey == LocalAiAdmissionKey(document.Lines[i], targetLanguage, segments[i]))) continue;
            lines ??= document.Lines.ToArray();
            lines[i] = lines[i] with { Secondary = null, TranslationOrigin = LyricsTranslationOrigin.None,
                TranslationLanguage = null, TranslationLanguageIsExplicit = null, LocalAiAdmissionKey = null };
        }
        return lines is null ? document : document with { Lines = lines };
    }

    public static string LocalAiAdmissionKey(LyricsLine line, string targetLanguage, IReadOnlyList<string> segments) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            policy = Version, target = LyricsTranslationPolicy.NormalizeLanguage(targetLanguage),
            line.Text, line.SourceLanguage, segments,
        })));

    internal static bool ProviderTranslationMatches(LyricsLine line, string normalizedTarget)
    {
        // A provider's explicit language tag remains authoritative (including short
        // TTML translations). Untagged multi-line display rows can carry evidence
        // for more than one language without forcing it into the single tag field.
        if (line.TranslationLanguageIsExplicit != false && !string.IsNullOrWhiteSpace(line.TranslationLanguage))
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
            if (document.Lines[i].TranslationLanguageIsExplicit != false &&
                !string.IsNullOrWhiteSpace(document.Lines[i].TranslationLanguage)) continue;
            var languages = ProviderSegmentEvidence(document.Lines[i]).Where(evidence => evidence.IsConfident)
                .Select(evidence => evidence.Language).Distinct(StringComparer.Ordinal).ToArray();
            // Multiple positive languages stay untagged; the matching policy above
            // evaluates their independent evidence instead of erasing it.
            var language = languages.Length == 1 ? languages[0] : null;
            if (document.Lines[i].TranslationLanguage == language && document.Lines[i].TranslationLanguageIsExplicit == false) continue;
            lines ??= document.Lines.ToArray();
            lines[i] = lines[i] with { TranslationLanguage = language, TranslationLanguageIsExplicit = false };
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
    private static bool IsPureHan(string text)
    {
        var letters = text.EnumerateRunes().Where(Rune.IsLetter).ToArray();
        return letters.Length > 0 && letters.All(IsHan);
    }
}
