using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using Microsoft.Windows.Globalization;

namespace DropSpace.App.Services;

/// <summary>
/// Applies the persisted display-language choice before WinUI creates any localized surface.
/// System maps the current Windows display language to the localized resource set DropSpace ships.
/// </summary>
public sealed class AppLanguageService
{
    public const string EnglishLanguageTag = AppLanguagePolicy.EnglishLanguageTag;
    public const string SimplifiedChineseLanguageTag = AppLanguagePolicy.SimplifiedChineseLanguageTag;

    public AppLanguagePreference Preference { get; private set; } = AppLanguagePreference.System;

    public string EffectiveLanguageTag { get; private set; } = EnglishLanguageTag;

    /// <summary>
    /// The culture used for imperative strings. With the system choice selected, DropSpace has
    /// Chinese resources for a Chinese Windows display language and falls back to English for
    /// every other Windows display language.
    /// </summary>
    public void Apply(AppLanguagePreference preference)
    {
        Preference = preference;
        // Clear an earlier process's explicit choice before querying the OS preference list.
        ApplicationLanguages.PrimaryLanguageOverride = string.Empty;
        EffectiveLanguageTag = AppLanguagePolicy.ResolveEffectiveLanguageTag(
            preference,
            ApplicationLanguages.Languages);
        ApplicationLanguages.PrimaryLanguageOverride = EffectiveLanguageTag;
    }

    public static bool TryParseSupportedLanguage(string? value, out AppLanguagePreference preference)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        preference = normalized switch
        {
            "system" or "default" => AppLanguagePreference.System,
            "en" or "en-us" => AppLanguagePreference.English,
            "zh" or "zh-cn" or "zh-hans" => AppLanguagePreference.SimplifiedChinese,
            _ => AppLanguagePreference.System,
        };

        return normalized is "system" or "default" or "en" or "en-us" or "zh" or "zh-cn" or "zh-hans";
    }
}
