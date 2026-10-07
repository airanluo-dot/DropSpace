using System.Text.Json;

namespace DropSpace.Core.Models;

/// <summary>Preserves the former main-window theme override on existing installations.
/// Fresh settings still default to following Windows; explicit island choices are never remapped.</summary>
public static class IslandAppearanceMigration
{
    public static AppSettings Apply(AppSettings settings, JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return settings;
        var appearance = raw.EnumerateObject().FirstOrDefault(property =>
            property.Name.Equals(nameof(AppSettings.IslandAppearance), StringComparison.OrdinalIgnoreCase)).Value;
        if (appearance.ValueKind == JsonValueKind.Object && appearance.EnumerateObject().Any(property =>
            property.Name.Equals(nameof(IslandAppearanceSettings.Theme), StringComparison.OrdinalIgnoreCase))) return settings;

        var theme = Enum.IsDefined(settings.Theme) ? settings.Theme : ThemePreference.System;
        return settings with { IslandAppearance = (settings.IslandAppearance ?? new()) with { Theme = theme } };
    }
}
