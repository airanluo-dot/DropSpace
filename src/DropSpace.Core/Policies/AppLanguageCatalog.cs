using System.Text.Json;
using DropSpace.Core.Models;

namespace DropSpace.Core.Policies;

public sealed record AppLanguageDefinition(AppLanguagePreference Preference, string Tag, string NativeName);

/// <summary>The website and App consume the same offline localization/languages.json definition.</summary>
public static class AppLanguageCatalog
{
    private sealed record CatalogData(IReadOnlyList<AppLanguageDefinition> Languages,
        IReadOnlyDictionary<string, string> Fallbacks, string[] Traditional, string[] Simplified, string ChineseDefault);
    private static readonly CatalogData Data = Load();
    public static IReadOnlyList<AppLanguageDefinition> All => Data.Languages;

    private static CatalogData Load()
    {
        using var stream = typeof(AppLanguageCatalog).Assembly.GetManifestResourceStream("DropSpace.Core.Localization.Languages.json")
            ?? throw new InvalidOperationException("The shared interface language catalog is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var languages = root.GetProperty("languages").EnumerateArray().Select(item => new AppLanguageDefinition(
            (AppLanguagePreference)item.GetProperty("enumValue").GetInt32(),
            item.GetProperty("code").GetString()!, item.GetProperty("nativeName").GetString()!)).ToArray();
        var matching = root.GetProperty("matching");
        var chinese = matching.GetProperty("chinese");
        return new(Array.AsReadOnly(languages), matching.GetProperty("languageFallbacks").EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.OrdinalIgnoreCase),
            chinese.GetProperty("traditional").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            chinese.GetProperty("simplified").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            chinese.GetProperty("default").GetString()!);
    }

    public static bool TryMatch(string? language, out AppLanguageDefinition definition)
    {
        var tag = language?.Trim().Replace('_', '-') ?? string.Empty;
        var match = All.FirstOrDefault(item => item.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var parts = tag.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
            var baseLanguage = parts.FirstOrDefault() ?? string.Empty;
            string? supported = null;
            if (baseLanguage == "zh")
            {
                // Explicit scripts precede regions. All supported aliases and target codes
                // come from the shared catalog, including the Simplified Chinese default.
                var scriptTraditional = HasAlias(parts, Data.Traditional, script: true);
                var scriptSimplified = HasAlias(parts, Data.Simplified, script: true);
                var traditionalCode = Data.Traditional.First(alias => All.Any(item => item.Tag == alias));
                var simplifiedCode = Data.Simplified.First(alias => All.Any(item => item.Tag == alias));
                supported = scriptTraditional ? traditionalCode : scriptSimplified ? simplifiedCode :
                    HasAlias(parts, Data.Traditional, script: false) ? traditionalCode : Data.ChineseDefault;
            }
            else if (Data.Fallbacks.TryGetValue(baseLanguage, out var fallback)) supported = fallback;
            match = All.FirstOrDefault(item => item.Tag == supported);
        }
        definition = match!;
        return match is not null;
    }

    private static bool HasAlias(string[] parts, IEnumerable<string> aliases, bool script) =>
        aliases.Select(alias => alias.ToLowerInvariant().Split('-').Skip(1).FirstOrDefault())
            .Any(variant => variant is not null && (variant.Length == 4) == script && parts.Contains(variant));
}
