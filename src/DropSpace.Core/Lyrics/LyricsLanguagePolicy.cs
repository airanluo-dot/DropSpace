using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DropSpace.Core.Lyrics;

// Retained result types are used by independent reviewed translation routes.
// They no longer provide a second lexical detector in production.
public enum LyricsLanguageEvidenceKind { Unknown, Lexical, Context, Explicit }
public enum LyricsTranslationAdmission { SameLanguage, Translate, Abstain }
public readonly record struct LyricsLanguageEvidence(string? Language, double Confidence,
    LyricsLanguageEvidenceKind Kind = LyricsLanguageEvidenceKind.Unknown)
{
    public bool IsConfident => Language is not null && Confidence >= 0.6;
}

public static class LyricsLanguagePolicy
{
    public const string Version = "beta16-whole-track-admission-v1";
    public const string PreprocessingVersion = "original-space-sample-v1";
    // Dominance acceptance, not calibrated accuracy: require substantive input,
    // a majority top score and a clear lead over the runner-up in the same call.
    // Short/uncertain samples remain Unknown and proceed to the native check.
    public const int MinimumLetters = 8;
    public const double MinimumTopScore = 0.60;
    public const double MinimumScoreMargin = 0.20;
    private static readonly Regex Credit = new(@"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|词|詞|曲|制作人|製作人|制作|製作|监制|監製|混音|母带|母帶|录音|錄音|演唱|原唱|和声|和聲|吉他|贝斯|貝斯|鼓|钢琴|鋼琴|出品|发行|發行|版权|版權|翻译|翻譯|译者|譯者|词作者|曲作者|lyrics(?: by)?|words(?: by)?|music(?: by)?|written by|composed by|composer|arranged by|arranger|producer|produced by|mixed by|mastered by|vocal(?:s)?|guitar|bass|drums)\s*[:：/／]|^\s*(?:written|composed|arranged|produced|mixed|mastered|lyrics|words|music)\s+by\s+\S", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SpacedCredit = new(@"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|填词|填詞|词曲|詞曲|制作人|製作人|编曲|混音|母带|母帶|录音|錄音|演唱|原唱)\s+\S", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Regex StructuredCredit = new(@"^\s*(?:作\s*词|作\s*詞|作\s*曲|编\s*曲|編\s*曲|制谱|製譜|音乐誊谱|音樂謄譜|乐队|樂隊|管弦乐队|管弦樂隊|演唱|电吉他|電吉他|民谣吉他|民謠吉他|木吉他|录音棚|錄音棚|录音师|錄音師|混音师|混音師|母带制作|母帶製作|出品)\s*(?:(?:lyricist|composer|arranger|music\s+copyist|orchestra|artist|electric\s+guitar|acoustic\s+guitar|recording\s+(?:studio|engineer)|mixing\s+engineer|mastering\s+engineer|produced\s+by)\s*)?[:：]\s*\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Timestamp = new(@"(?:\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]|<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>)", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Regex WhiteSpace = new(@"\s+", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private sealed record Projection(string[][] Parts, string OriginalRevision, string TranslationRevision,
        string OriginalSample, string ProviderSample, IReadOnlySet<string> TrustedTargets);
    private static readonly ConditionalWeakTable<LyricsDocument, Projection> Projections = new();
    private sealed record Identity(string Value);
    private static readonly ConditionalWeakTable<LyricsDocument, Identity> Identities = new();

    public static string NormalizeTarget(string? language)
    {
        var normalized = LyricsTranslationPolicy.NormalizeLanguage(language);
        return normalized.StartsWith("zh-", StringComparison.Ordinal) ? "zh" : normalized;
    }

    private static string[] PhysicalLines(string text) => text.Split(['\r', '\n', '\u0085', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool IsCreditLine(string text) => Credit.IsMatch(text) || SpacedCredit.IsMatch(text) || StructuredCredit.IsMatch(text);
    public static bool IsCredit(string text)
    {
        var lines = PhysicalLines(text);
        return lines.Length > 0 && lines.All(IsCreditLine);
    }
    // Sung interjections remain content. This compatibility method intentionally
    // supplies no exclusion list and is not a language recognizer.
    public static bool IsVocable(string text) => false;

    private static Projection Project(LyricsDocument document) => Projections.GetValue(document, _ =>
    {
        var parts = document.Lines.Select(line => PhysicalLines(line.Text)
            .Select(text => Timestamp.Replace(text, string.Empty).Trim())
            .Where(text => text.Length > 0 && !IsCreditLine(text)).ToArray()).ToArray();
        // Retain the baseline's narrowly verified provider metadata-header removal.
        // Ordinary sung names/interjections are never word-list exclusions.
        if (document.Match is { Score: >= 4 } match && parts.Length > 0 && parts[0].Length > 0)
        {
            var header = parts[0][0];
            var title = LyricsMatcher.Normalize(match.Title);
            var artist = LyricsMatcher.Normalize(match.Artist);
            if (header.Length <= 2048 && title.Length > 0 && artist.Length > 0)
                for (var index = 0; index < header.Length; index++)
                {
                    if (header[index] is not ('-' or '–' or '—')) continue;
                    var left = LyricsMatcher.Normalize(header[..index]);
                    var right = LyricsMatcher.Normalize(header[(index + 1)..]);
                    if ((left == artist && right == title) || (left == title && right == artist))
                    { parts[0] = parts[0].Skip(1).ToArray(); break; }
                }
        }
        var original = Hash(document.Lines.Select((line, occurrence) => new
        { occurrence, line.Text, start = line.Start.Ticks, end = line.End.Ticks, line.SourceLanguage }));
        var translation = Hash(document.Lines.Select((line, occurrence) => (translation: ProviderTranslation(line), occurrence))
            .Where(item => item.translation is not null)
            .Select(item => new { item.occurrence, item.translation!.Text, item.translation.Language,
                item.translation.LanguageIsExplicit }));
        var providers = document.Lines.Where(line => !IsCredit(line.Text)).Select(ProviderTranslation)
            .Where(value => value is not null).Select(value => value!).ToArray();
        var providerSample = WhiteSpace.Replace(string.Join(" ", providers.SelectMany(value => PhysicalLines(value.Text))), " ").Trim();
        var trustedTargets = providers.Where(value => value.LanguageIsExplicit == true && !string.IsNullOrWhiteSpace(value.Language))
            .GroupBy(value => NormalizeTarget(value.Language), StringComparer.Ordinal)
            .Where(track => MetadataMatchesContent(string.Join(" ", track.Select(value => value.Text)), track.Key))
            .Select(track => track.Key).ToHashSet(StringComparer.Ordinal);
        return new(parts, original, translation,
            WhiteSpace.Replace(string.Join(" ", parts.SelectMany(row => row)), " ").Trim(), providerSample, trustedTargets);
    });
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    public static string OriginalRevision(LyricsDocument document) => Project(document).OriginalRevision;
    public static string TranslationRevision(LyricsDocument document) => Project(document).TranslationRevision;
    public static string SourceIdentity(LyricsDocument document) => Identities.GetValue(document, value => new(Hash(new
    { value.Provider, value.Match?.CandidateId, value.Match?.TrackIdentity, value.Match?.Title,
        value.Match?.Artist, value.Match?.Album, value.Match?.DurationSeconds, value.Match?.CanonicalTitle, value.ProviderDataRevision }))).Value;

    /// <summary>AI display/state edits reuse source preprocessing only after proving
    /// that every source occurrence and preserved provider payload stayed identical.</summary>
    public static LyricsDocument WithPresentationLines(LyricsDocument document, IReadOnlyList<LyricsLine> lines)
    {
        var result = document with { Lines = lines };
        if (lines.Count != document.Lines.Count) return result;
        for (var index = 0; index < lines.Count; index++)
        {
            var before = document.Lines[index]; var after = lines[index];
            if (before.Text != after.Text || before.Start != after.Start || before.End != after.End ||
                before.SourceLanguage != after.SourceLanguage || ProviderTranslation(before) != ProviderTranslation(after)) return result;
        }
        Projections.Add(result, Project(document));
        Identities.Add(result, new(SourceIdentity(document)));
        return result;
    }

    public static LyricsProviderTranslation? ProviderTranslation(LyricsLine line) =>
        line.TranslationOrigin == LyricsTranslationOrigin.Provider && !string.IsNullOrWhiteSpace(line.Secondary)
            ? new(line.Secondary, line.TranslationLanguage, line.TranslationLanguageIsExplicit)
            : line.OriginalProviderTranslation;

    public static string BuildSample(LyricsDocument document, LyricsLanguageRole role)
    {
        var projection = Project(document);
        return role == LyricsLanguageRole.Original ? projection.OriginalSample : projection.ProviderSample;
    }

    public static bool PredictionMatches(LyricsLanguagePrediction prediction, string sample, string target) =>
        sample.EnumerateRunes().Count(Rune.IsLetter) >= MinimumLetters &&
        double.IsFinite(prediction.Score) && double.IsFinite(prediction.RunnerUpScore) &&
        prediction.Score >= MinimumTopScore && prediction.Score - prediction.RunnerUpScore >= MinimumScoreMargin &&
        NormalizeTarget(prediction.Label?.Replace("__label__", string.Empty, StringComparison.Ordinal)) == NormalizeTarget(target);

    public static bool HasTrustedTargetProviderTranslation(LyricsDocument document, string target) =>
        Project(document).TrustedTargets.Contains(NormalizeTarget(target));

    // Script consistency validates explicit provider metadata, never classifies
    // an original. Names and interjections can coexist with a native translation.
    private static bool MetadataMatchesContent(string text, string language)
    {
        var letters = text.EnumerateRunes().Where(Rune.IsLetter).ToArray();
        if (letters.Length == 0) return false;
        var han = letters.Count(rune => rune.Value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af);
        var kana = letters.Count(rune => rune.Value is >= 0x3040 and <= 0x30ff);
        var latin = letters.Count(rune => rune.Value is >= 0x0041 and <= 0x024f);
        return language switch { "zh" => han > 0 && kana == 0, "en" => latin > 0 && latin >= letters.Length / 2.0, _ => true };
    }

    public static LyricsDocument Prepare(LyricsDocument document, string targetLanguage,
        LyricsLanguagePrediction original, LyricsLanguagePrediction? providerTranslation = null, long generation = 0)
    {
        var target = NormalizeTarget(targetLanguage);
        var sample = BuildSample(document, LyricsLanguageRole.Original);
        var predictedProviderTarget = providerTranslation is not null &&
            PredictionMatches(providerTranslation, BuildSample(document, LyricsLanguageRole.ProviderTranslation), target);
        var decision = LyricsBodyQualityPolicy.Classify(document) != LyricsBodyQuality.Usable || sample.Length == 0
            ? LyricsWholeTrackDecision.NoContent
            : PredictionMatches(original, sample, target) ? LyricsWholeTrackDecision.OriginalTarget
            : HasTrustedTargetProviderTranslation(document, target) || predictedProviderTarget
                ? LyricsWholeTrackDecision.ProviderTarget : LyricsWholeTrackDecision.AllowAi;
        var reason = decision switch { LyricsWholeTrackDecision.NoContent => "no-lyric-body",
            LyricsWholeTrackDecision.OriginalTarget => "original-target-language",
            LyricsWholeTrackDecision.ProviderTarget => "provider-target-translation",
            _ => original.Diagnostic is null ? "unknown-original-no-target-provider" : "unknown-original-" + original.Diagnostic };
        if (decision == LyricsWholeTrackDecision.ProviderTarget && predictedProviderTarget)
            document = document with { Lines = document.Lines.Select(line =>
                line.TranslationOrigin == LyricsTranslationOrigin.Provider && !string.IsNullOrWhiteSpace(line.Secondary) &&
                (line.TranslationLanguageIsExplicit == false || string.IsNullOrWhiteSpace(line.TranslationLanguage))
                ? line with { TranslationLanguage = target, TranslationLanguageIsExplicit = false } : line).ToArray() };
        var result = document with { TranslationAdmission = new(decision, target, SourceIdentity(document), OriginalRevision(document),
            TranslationRevision(document), Version, reason, original, providerTranslation, generation) };
        Projections.Add(result, Project(document));
        Identities.Add(result, new(SourceIdentity(document)));
        return result;
    }

    public static bool IsCurrent(LyricsDocument document, string targetLanguage) => document.TranslationAdmission is { } admission &&
        admission.RuleVersion == Version && admission.Target == NormalizeTarget(targetLanguage) &&
        admission.SourceIdentity == SourceIdentity(document) && admission.OriginalRevision == OriginalRevision(document) &&
        admission.TranslationRevision == TranslationRevision(document);
    public static bool IsAdmissionCurrent(LyricsDocument document, string targetLanguage) => IsCurrent(document, targetLanguage);
    public static string SourceRevision(LyricsDocument document) => OriginalRevision(document);
    public static bool CanTranslate(LyricsDocument document, string targetLanguage) =>
        IsCurrent(document, targetLanguage) && document.TranslationAdmission!.AllowsAi &&
        !HasTrustedTargetProviderTranslation(document, targetLanguage);

    public static bool SameSourceLanguage(string? source, string? target) =>
        NormalizeTarget(source) is { Length: > 0 } left && left == NormalizeTarget(target);
    public static LyricsLanguageEvidence Identify(string text, string? explicitLanguage = null) =>
        !string.IsNullOrWhiteSpace(explicitLanguage) && !string.IsNullOrWhiteSpace(text)
            ? new(LyricsTranslationPolicy.NormalizeLanguage(explicitLanguage), 1, LyricsLanguageEvidenceKind.Explicit) : default;
    public static IReadOnlyList<LyricsLanguageEvidence> SourceEvidence(LyricsDocument document) =>
        Enumerable.Repeat(document.TranslationAdmission is { OriginalPrediction.Label: { } label } admission
            ? new LyricsLanguageEvidence(label.Replace("__label__", string.Empty, StringComparison.Ordinal), admission.OriginalPrediction.Score)
            : default, document.Lines.Count).ToArray();
    public static LyricsTranslationAdmission GetAdmission(string text, string targetLanguage, LyricsLanguageEvidence evidence) =>
        evidence.IsConfident && SameSourceLanguage(evidence.Language, targetLanguage)
            ? LyricsTranslationAdmission.SameLanguage : LyricsTranslationAdmission.Translate;

    public static int[] EligibleIndices(LyricsDocument document, string targetLanguage)
    {
        if (!CanTranslate(document, targetLanguage) || document.Lines.Count is 0 or > 500) return [];
        var parts = Project(document).Parts;
        return parts.Sum(row => row.Length) <= 500 ? Enumerable.Range(0, parts.Length).Where(index => parts[index].Length > 0).ToArray() : [];
    }
    public static IReadOnlyList<string[]> EligibleSegments(LyricsDocument document, string targetLanguage) =>
        CanTranslate(document, targetLanguage) ? Project(document).Parts : document.Lines.Select(_ => Array.Empty<string>()).ToArray();
    public static string[] EligibleSegments(LyricsLine line, string targetLanguage) => [];

    public static LyricsDocument MarkTranslationStates(LyricsDocument document, string targetLanguage)
    {
        var segments = EligibleSegments(document, targetLanguage);
        var eligible = EligibleIndices(document, targetLanguage).ToHashSet();
        return WithPresentationLines(document, document.Lines.Select((line, index) =>
        {
            var provider = HasTargetProviderTranslation(line, targetLanguage) ||
                IsCurrent(document, targetLanguage) && document.TranslationAdmission!.Decision == LyricsWholeTrackDecision.ProviderTarget &&
                line.TranslationOrigin == LyricsTranslationOrigin.Provider && !string.IsNullOrWhiteSpace(line.Secondary);
            var local = eligible.Contains(index) && line.TranslationOrigin == LyricsTranslationOrigin.LocalAi &&
                !string.IsNullOrWhiteSpace(line.Secondary) && SameSourceLanguage(line.TranslationLanguage, targetLanguage) &&
                line.LocalAiAdmissionKey == LocalAiAdmissionKey(document, index, targetLanguage, segments[index]);
            return line with { TranslationState = provider || local ? LyricsLineTranslationState.Translated :
                eligible.Contains(index) ? LyricsLineTranslationState.Pending : LyricsLineTranslationState.Skipped,
                TranslationReason = provider ? "provider-target-translation" : local ? "local-ai-complete" :
                    eligible.Contains(index) ? "awaiting-translation" : IsCredit(line.Text) ? "credit" :
                    document.TranslationAdmission?.Reason ?? "whole-track-not-prepared" };
        }).ToArray());
    }
    public static LyricsDocument FailPending(LyricsDocument document, string reason) => WithPresentationLines(document,
        document.Lines.Select(line => line.TranslationState == LyricsLineTranslationState.Pending
        ? line with { TranslationState = LyricsLineTranslationState.Failed, TranslationReason = reason } : line).ToArray());
    public static LyricsDocument RemoveIneligibleLocalTranslations(LyricsDocument document, string targetLanguage)
    {
        var segments = EligibleSegments(document, targetLanguage);
        if (!document.Lines.Any(line => line.TranslationOrigin == LyricsTranslationOrigin.LocalAi)) return document;
        return WithPresentationLines(document, document.Lines.Select((line, index) =>
            line.TranslationOrigin == LyricsTranslationOrigin.LocalAi &&
            (segments[index].Length == 0 || !SameSourceLanguage(line.TranslationLanguage, targetLanguage) ||
                line.LocalAiAdmissionKey != LocalAiAdmissionKey(document, index, targetLanguage, segments[index]))
            ? line with { Secondary = line.OriginalProviderTranslation?.Text,
                TranslationOrigin = line.OriginalProviderTranslation is null ? LyricsTranslationOrigin.None : LyricsTranslationOrigin.Provider,
                TranslationLanguage = line.OriginalProviderTranslation?.Language,
                TranslationLanguageIsExplicit = line.OriginalProviderTranslation?.LanguageIsExplicit,
                OriginalProviderTranslation = null, LocalAiAdmissionKey = null } : line).ToArray());
    }
    public static string LocalAiAdmissionKey(LyricsLine line, string targetLanguage, IReadOnlyList<string> segments) => Hash(new
    { policy = Version, target = NormalizeTarget(targetLanguage), line.Text, line.SourceLanguage, segments });
    public static string LocalAiAdmissionKey(LyricsDocument document, int occurrence, string targetLanguage,
        IReadOnlyList<string> segments) => Hash(new { policy = Version, target = NormalizeTarget(targetLanguage),
            source = SourceIdentity(document), original = OriginalRevision(document), native = TranslationRevision(document),
            occurrence, start = document.Lines[occurrence].Start.Ticks, end = document.Lines[occurrence].End.Ticks,
            document.Lines[occurrence].Text, document.Lines[occurrence].SourceLanguage, segments });
    internal static bool ProviderTranslationMatches(LyricsLine line, string normalizedTarget) =>
        line.TranslationLanguageIsExplicit == true && !string.IsNullOrWhiteSpace(line.TranslationLanguage) &&
        SameSourceLanguage(line.TranslationLanguage, normalizedTarget) && MetadataMatchesContent(line.Secondary ?? string.Empty, NormalizeTarget(normalizedTarget));
    public static bool HasTargetProviderTranslation(LyricsLine line, string targetLanguage) =>
        line.TranslationOrigin == LyricsTranslationOrigin.Provider && !string.IsNullOrWhiteSpace(line.Secondary) && ProviderTranslationMatches(line, targetLanguage);
    // Parser paths preserve provenance. Only the asynchronous whole-track adapter
    // may infer an untagged translation language; no per-line lexical pass remains.
    public static LyricsDocument IdentifyProviderTranslations(LyricsDocument document) => document;
}
