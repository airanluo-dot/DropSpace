using DropSpace.Core.Models;

namespace DropSpace.Core.Policies;

/// <summary>Settings boundary for the permanent English/Simplified Chinese lyric targets.</summary>
public static class LyricsTranslationTargetPolicy
{
    public static LyricsTranslationTarget DefaultForInterface(AppLanguagePreference preference, IEnumerable<string?> systemLanguages) =>
        AppLanguagePolicy.ResolveEffectiveLanguageTag(preference, systemLanguages).StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? LyricsTranslationTarget.SimplifiedChinese : LyricsTranslationTarget.English;

    public static AppSettings ApplyDefault(AppSettings settings, IEnumerable<string?> systemLanguages) =>
        settings.LyricsTranslationTargetExplicit ? settings : settings with
        {
            LyricsTranslationTarget = DefaultForInterface(settings.Language, systemLanguages),
        };

    public static string ToLanguageTag(LyricsTranslationTarget target) =>
        target == LyricsTranslationTarget.SimplifiedChinese ? "zh" : "en";

    // The old implementation used only the primary UI culture and treated every Chinese
    // Windows culture as Simplified Chinese. Preserve that actual direction once on upgrade.
    public static LyricsTranslationTarget FromLegacyInterface(AppLanguagePreference preference, string? primarySystemLanguage) =>
        preference == AppLanguagePreference.SimplifiedChinese ||
        preference == AppLanguagePreference.System && primarySystemLanguage?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true
            ? LyricsTranslationTarget.SimplifiedChinese : LyricsTranslationTarget.English;
}
