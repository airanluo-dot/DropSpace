using DropSpace.Core.Models;

namespace DropSpace.Core.Policies;

public static class AppLanguagePolicy
{
    public const string EnglishLanguageTag = "en-US";
    public const string SimplifiedChineseLanguageTag = "zh-CN";

    public static string ResolveEffectiveLanguageTag(
        AppLanguagePreference preference,
        IEnumerable<string?> systemLanguages)
    {
        ArgumentNullException.ThrowIfNull(systemLanguages);
        if (!Enum.IsDefined(preference)) preference = AppLanguagePreference.System;
        if (preference != AppLanguagePreference.System)
            return AppLanguageCatalog.All.First(item => item.Preference == preference).Tag;
        foreach (var language in systemLanguages)
            if (AppLanguageCatalog.TryMatch(language, out var match)) return match.Tag;
        return EnglishLanguageTag;
    }
}
