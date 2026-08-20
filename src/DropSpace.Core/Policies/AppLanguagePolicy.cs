using DropSpace.Core.Models;

namespace DropSpace.Core.Policies;

/// <summary>
/// Resolves DropSpace's two shipped resource sets without treating unsupported Windows languages
/// as if they had translations.
/// </summary>
public static class AppLanguagePolicy
{
    public const string EnglishLanguageTag = "en-US";
    public const string SimplifiedChineseLanguageTag = "zh-CN";

    public static string ResolveEffectiveLanguageTag(
        AppLanguagePreference preference,
        IEnumerable<string?> systemLanguages)
    {
        ArgumentNullException.ThrowIfNull(systemLanguages);

        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        if (preference == AppLanguagePreference.English)
        {
            return EnglishLanguageTag;
        }

        if (preference == AppLanguagePreference.SimplifiedChinese)
        {
            return SimplifiedChineseLanguageTag;
        }

        var primarySystemLanguage = systemLanguages.FirstOrDefault();
        return primarySystemLanguage?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true
            ? SimplifiedChineseLanguageTag
            : EnglishLanguageTag;
    }
}
