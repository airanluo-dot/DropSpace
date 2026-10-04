using DropSpace.Core.Models;
using DropSpace.Core.Policies;

namespace DropSpace.Core.Lyrics;

public enum LyricsTranslationDecision
{
    OriginalOnly,
    UseProvider,
    TranslateLocally,
    UnsupportedLanguage,
}

public static class LyricsTranslationPolicy
{
    /// <summary>A usable original can enter AI admission even when supplemental lookup timed out.
    /// Language, existing translation, user settings and model checks remain in AI admission.</summary>
    public static bool CanOfferLocalFallback(LyricsQueryResult result) =>
        result.Status == LyricsQueryStatus.Found && result.Document.Lines.Count > 0;

    public static string ResolveTarget(AppLanguagePreference preference, IEnumerable<string?> systemLanguages) =>
        AppLanguagePolicy.ResolveEffectiveLanguageTag(preference, systemLanguages);

    /// <summary>Model capabilities are supplied by the verified model catalog, never inferred from the file size.</summary>
    public static LyricsTranslationDecision Decide(bool enabled, bool hasOriginalLyrics,
        string targetLanguage, string? sourceLanguage, string? providerLanguage, bool hasProviderTranslation,
        IReadOnlySet<string> supportedLanguages)
    {
        ArgumentNullException.ThrowIfNull(supportedLanguages);
        var target = NormalizeLanguage(targetLanguage);
        if (!hasOriginalLyrics || target.Length == 0) return LyricsTranslationDecision.OriginalOnly;
        if (hasProviderTranslation && NormalizeLanguage(providerLanguage) == target)
            return LyricsTranslationDecision.UseProvider;
        if (!enabled) return LyricsTranslationDecision.OriginalOnly;
        var source = NormalizeLanguage(sourceLanguage);
        if (LyricsLanguagePolicy.SameSourceLanguage(source, target)) return LyricsTranslationDecision.OriginalOnly;
        if (!supportedLanguages.Any(language => NormalizeLanguage(language) == target) ||
            (source.Length > 0 && !supportedLanguages.Any(language => NormalizeLanguage(language) == source)))
            return LyricsTranslationDecision.UnsupportedLanguage;
        return LyricsTranslationDecision.TranslateLocally;
    }

    public static bool HasMatchingProviderTranslation(LyricsDocument document, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(document);
        var target = NormalizeLanguage(targetLanguage);
        return target.Length > 0 && document.Lines.Any(line =>
            line.TranslationOrigin == LyricsTranslationOrigin.Provider && !LyricsLanguagePolicy.IsCredit(line.Text) &&
            !string.IsNullOrWhiteSpace(line.Secondary) &&
            LyricsLanguagePolicy.ProviderTranslationMatches(line, target));
    }

    public static string NormalizeLanguage(string? language)
    {
        var tag = language?.Trim().Replace('_', '-').ToLowerInvariant() ?? string.Empty;
        return tag switch
        {
            "zh" or "zh-cn" or "zh-sg" or "zh-hans" or "zh-hans-cn" => "zh-Hans",
            "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" or "zh-hant-tw" => "zh-Hant",
            "" => string.Empty,
            _ => tag.Split('-')[0],
        };
    }
}
