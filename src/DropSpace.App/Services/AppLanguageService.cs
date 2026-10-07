using System.Globalization;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;

namespace DropSpace.App.Services;

/// <summary>
/// Resolves the persisted display-language choice before the application creates localized surfaces.
/// System maps the current Windows display language to the localized resource set DropSpace ships.
/// </summary>
public sealed class AppLanguageService
{
    public const string EnglishLanguageTag = AppLanguagePolicy.EnglishLanguageTag;
    public const string SimplifiedChineseLanguageTag = AppLanguagePolicy.SimplifiedChineseLanguageTag;

    public AppLanguagePreference Preference { get; private set; } = AppLanguagePreference.System;

    public string EffectiveLanguageTag { get; private set; } = EnglishLanguageTag;

    public event EventHandler? Changed;

    public void Apply(AppLanguagePreference preference)
    {
        var previousLanguageTag = EffectiveLanguageTag;
        Preference = Enum.IsDefined(preference) ? preference : AppLanguagePreference.System;
        // Explicit resource contexts also work for the portable, unpackaged app.
        IReadOnlyList<string> languages;
        try { languages = Windows.System.UserProfile.GlobalizationPreferences.Languages; }
        catch (Exception) { languages = [CultureInfo.CurrentUICulture.Name]; }
        EffectiveLanguageTag = AppLanguagePolicy.ResolveEffectiveLanguageTag(Preference, languages);
        if (!string.Equals(previousLanguageTag, EffectiveLanguageTag, StringComparison.Ordinal))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public static bool TryParseSupportedLanguage(string? value, out AppLanguagePreference preference)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        preference = AppLanguagePreference.System;
        if (normalized is "system" or "default") return true;
        if (!AppLanguageCatalog.TryMatch(normalized, out var match)) return false;
        preference = match.Preference;
        return true;
    }
}
