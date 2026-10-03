using DropSpace.Core.Widgets;

namespace DropSpace.Core.Models;

/// <summary>Normalizes only native-island fields; never resets unrelated user preferences.</summary>
public static class NativeIslandSettingsPolicy
{
    public const int MaximumSources = 128;
    public const int MaximumSourceLength = 512;
    public const int MaximumDelayMilliseconds = 30_000;
    public const int MinimumWidth = 80;
    public const int MaximumWidth = 600;
    public const long MinimumLyricsCacheBytes = 100L * 1024 * 1024;
    public const long MaximumLyricsCacheBytes = 5L * 1024 * 1024 * 1024;

    public static AppSettings Normalize(AppSettings settings)
    {
        var activity = settings.IslandActivity ?? new();
        var lyrics = settings.Lyrics ?? new();
        var appearance = settings.IslandAppearance ?? new();
        var widgets = settings.Widgets ?? new();
        var provider = Enum.IsDefined(lyrics.Provider) && lyrics.Provider != LyricsProviderKind.LocalLrc
            ? lyrics.Provider : LyricsProviderKind.NetEase;
        LyricsProviderKind? backupProvider = lyrics.BackupProvider is { } backup && Enum.IsDefined(backup) &&
            backup != LyricsProviderKind.LocalLrc && backup != provider ? backup : null;
        var sources = (activity.AllowedMediaSourceAppIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= MaximumSourceLength)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumSources).ToArray();
        var modelId = lyrics.AiModelId == "lightweight" ? Lyrics.AiLyricsModelCatalog.Compact.Id : lyrics.AiModelId;
        var normalized = settings with
        {
            IslandActivity = activity with
            {
                AllowedMediaSourceAppIds = activity.AllowedMediaSourceAppIds is { } existing && existing.SequenceEqual(sources)
                    ? existing : sources,
            },
            Lyrics = lyrics with
            {
                CacheMaximumBytes = Math.Clamp(lyrics.CacheMaximumBytes, MinimumLyricsCacheBytes, MaximumLyricsCacheBytes),
                AiModelId = Lyrics.AiLyricsModelCatalog.FindSelectable(modelId)?.Id ?? Lyrics.AiLyricsModelCatalog.ExperimentalPlain.Id,
                GlowMode = Enum.IsDefined(lyrics.GlowMode) ? lyrics.GlowMode : Lyrics.LyricsGlowMode.Off,
                FontSize = double.IsFinite(lyrics.FontSize) ? Math.Clamp(lyrics.FontSize, 12, 28) : 16,
                Mode = Enum.IsDefined(lyrics.Mode) ? lyrics.Mode : LyricsMode.Online,
                Provider = provider,
                BackupProvider = backupProvider,
                DelayMilliseconds = Math.Clamp(lyrics.DelayMilliseconds, -MaximumDelayMilliseconds, MaximumDelayMilliseconds),
                ScrollingMaxWidth = Math.Clamp(lyrics.ScrollingMaxWidth, MinimumWidth, MaximumWidth),
                LocalLrcDirectory = lyrics.LocalLrcDirectory is { Length: <= 32767 } ? lyrics.LocalLrcDirectory : string.Empty,
            },
            IslandAppearance = appearance with
            {
                HideDelayMilliseconds = Math.Clamp(appearance.HideDelayMilliseconds, 500, MaximumDelayMilliseconds),
                CompactScale = NormalizeScale(appearance.CompactScale),
                ExpandedScale = NormalizeScale(appearance.ExpandedScale),
            },
            SystemActivities = settings.SystemActivities ?? new(),
            Widgets = widgets with { Layout = WidgetLayoutPolicy.Normalize(widgets.Layout) },
        };
        return normalized == settings ? settings : normalized;
    }

    private static double NormalizeScale(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.5, 2) : 1;
}
