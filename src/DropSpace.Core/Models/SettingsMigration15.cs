using System.Text.Json;

namespace DropSpace.Core.Models;

/// <summary>Reads field presence before applying legacy fullscreen defaults.</summary>
public static class SettingsMigration15
{
    public static AppSettings Apply(AppSettings settings, JsonElement raw)
    {
        if (settings.Version is < 1 or >= 15) return settings;
        var appearance = Property(raw, nameof(AppSettings.IslandAppearance));
        var activity = Property(raw, nameof(AppSettings.SystemActivities));
        var mapped = Boolean(appearance, "ForceShowOverFullscreen", false) || !Boolean(activity, "SuppressOverFullscreen", true);
        return settings with
        {
            IslandAppearance = (settings.IslandAppearance ?? new()) with
            {
                Resident = Boolean(appearance, nameof(IslandAppearanceSettings.Resident), false),
                ForceShowOverFullscreen = mapped,
                HideDelayMilliseconds = NativeIslandSettingsPolicy.NormalizeHideDelay((settings.IslandAppearance ?? new()).HideDelayMilliseconds),
            },
            // Earlier migrations still run from the original version in the loader.
        };
    }
    private static bool Boolean(JsonElement element, string name, bool fallback) => Property(element, name).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };
    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        ? element.EnumerateObject().FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
}
