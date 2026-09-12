namespace DropSpace.Core.Models;

/// <summary>Native Island rebuild migration; the item database is not modified.</summary>
public static class SettingsMigration14
{
    public static AppSettings Apply(AppSettings settings)
    {
        if (settings.Version is < 1 or >= 14) return settings;
        // Pre-native versions have no native preferences. New record defaults supply them.
        // 12/13 deserialize the compatible sections and drop obsolete UI-only properties.
        return NativeIslandSettingsPolicy.Normalize(settings) with { Version = 14 };
    }
}
