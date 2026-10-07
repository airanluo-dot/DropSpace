using System.Collections.Generic;

namespace DropSpace.Core.Models;

/// <summary>Applies only the fields edited by a form to the latest persisted snapshot.</summary>
public static class SettingsChangePolicy
{
    /// <summary>Applies update-check metadata to the current snapshot without replacing preferences.</summary>
    public static AppSettings ApplyLastUpdateCheck(AppSettings latest, DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(latest);
        return latest.LastUpdateCheckUtc is { } previous && previous >= checkedAt
            ? latest
            : latest with { LastUpdateCheckUtc = checkedAt.ToUniversalTime() };
    }

    public static AppSettings Merge(AppSettings baseline, AppSettings requested, AppSettings latest) => latest with
    {
        MaxDownloadConnections = Pick(baseline.MaxDownloadConnections, requested.MaxDownloadConnections, latest.MaxDownloadConnections),
        MaxConcurrentDownloads = Pick(baseline.MaxConcurrentDownloads, requested.MaxConcurrentDownloads, latest.MaxConcurrentDownloads),
        DownloadSpeedLimitBytesPerSecond = Pick(baseline.DownloadSpeedLimitBytesPerSecond, requested.DownloadSpeedLimitBytesPerSecond, latest.DownloadSpeedLimitBytesPerSecond),
        DefaultDownloadDirectory = Pick(baseline.DefaultDownloadDirectory, requested.DefaultDownloadDirectory, latest.DefaultDownloadDirectory),
        IslandActivity = Pick(baseline.IslandActivity, requested.IslandActivity, latest.IslandActivity),
        Lyrics = MergeLyrics(baseline.Lyrics, requested.Lyrics, latest.Lyrics),
        IslandAppearance = MergeIsland(baseline.IslandAppearance, requested.IslandAppearance, latest.IslandAppearance),
        IslandContentPriority = Pick(baseline.IslandContentPriority, requested.IslandContentPriority, latest.IslandContentPriority),
        SystemActivities = Pick(baseline.SystemActivities, requested.SystemActivities, latest.SystemActivities),
        Widgets = Pick(baseline.Widgets, requested.Widgets, latest.Widgets),
        CaptureImages = Pick(baseline.CaptureImages, requested.CaptureImages, latest.CaptureImages),
        CaptureFiles = Pick(baseline.CaptureFiles, requested.CaptureFiles, latest.CaptureFiles),
        CaptureFolders = Pick(baseline.CaptureFolders, requested.CaptureFolders, latest.CaptureFolders),
        StartWithWindows = Pick(baseline.StartWithWindows, requested.StartWithWindows, latest.StartWithWindows),
        RetentionDays = Pick(baseline.RetentionDays, requested.RetentionDays, latest.RetentionDays),
        RetentionItemCount = Pick(baseline.RetentionItemCount, requested.RetentionItemCount, latest.RetentionItemCount),
        MaxImageBytes = Pick(baseline.MaxImageBytes, requested.MaxImageBytes, latest.MaxImageBytes),
        MaxImagePixels = Pick(baseline.MaxImagePixels, requested.MaxImagePixels, latest.MaxImagePixels),
        MaxClipboardFileBytes = Pick(baseline.MaxClipboardFileBytes, requested.MaxClipboardFileBytes, latest.MaxClipboardFileBytes),
        MaxClipboardFileTotalBytes = Pick(baseline.MaxClipboardFileTotalBytes, requested.MaxClipboardFileTotalBytes, latest.MaxClipboardFileTotalBytes),
        MaxClipboardFileItems = Pick(baseline.MaxClipboardFileItems, requested.MaxClipboardFileItems, latest.MaxClipboardFileItems),
        MaxTextCharacters = Pick(baseline.MaxTextCharacters, requested.MaxTextCharacters, latest.MaxTextCharacters),
        Theme = Pick(baseline.Theme, requested.Theme, latest.Theme),
        Language = Pick(baseline.Language, requested.Language, latest.Language),
        CloseBehavior = Pick(baseline.CloseBehavior, requested.CloseBehavior, latest.CloseBehavior),
        CloseExplanationShown = Pick(baseline.CloseExplanationShown, requested.CloseExplanationShown, latest.CloseExplanationShown),
        LaunchPage = Pick(baseline.LaunchPage, requested.LaunchPage, latest.LaunchPage),
        OverlayMotion = Pick(baseline.OverlayMotion, requested.OverlayMotion, latest.OverlayMotion),
        OverlayMonitor = Pick(baseline.OverlayMonitor, requested.OverlayMonitor, latest.OverlayMonitor),
        FileDragWakeMode = Pick(baseline.FileDragWakeMode, requested.FileDragWakeMode, latest.FileDragWakeMode),
        OverlayPlacementMode = Pick(baseline.OverlayPlacementMode, requested.OverlayPlacementMode, latest.OverlayPlacementMode),
        CustomOverlayPlacements = Pick(baseline.CustomOverlayPlacements, requested.CustomOverlayPlacements, latest.CustomOverlayPlacements),
        OverlayPlacements = Pick(baseline.OverlayPlacements, requested.OverlayPlacements, latest.OverlayPlacements),
        QuickActionPreferences = Pick(baseline.QuickActionPreferences, requested.QuickActionPreferences, latest.QuickActionPreferences),
        QuickPanelHotkey = Pick(baseline.QuickPanelHotkey, requested.QuickPanelHotkey, latest.QuickPanelHotkey),
        SmartDragExcludedProcesses = Pick(baseline.SmartDragExcludedProcesses, requested.SmartDragExcludedProcesses, latest.SmartDragExcludedProcesses),
        AutoCheckForUpdates = Pick(baseline.AutoCheckForUpdates, requested.AutoCheckForUpdates, latest.AutoCheckForUpdates),
        AutoDownloadUpdates = Pick(baseline.AutoDownloadUpdates, requested.AutoDownloadUpdates, latest.AutoDownloadUpdates),
        AutoInstallUpdates = Pick(baseline.AutoInstallUpdates, requested.AutoInstallUpdates, latest.AutoInstallUpdates),
        UpdateChannel = Pick(baseline.UpdateChannel, requested.UpdateChannel, latest.UpdateChannel),
        EnableDeviceHandoff = Pick(baseline.EnableDeviceHandoff, requested.EnableDeviceHandoff, latest.EnableDeviceHandoff),
        EnableCrossDeviceClipboard = Pick(baseline.EnableCrossDeviceClipboard, requested.EnableCrossDeviceClipboard, latest.EnableCrossDeviceClipboard),
        EnableNearbySharing = Pick(baseline.EnableNearbySharing, requested.EnableNearbySharing, latest.EnableNearbySharing),
        EnableInternetSharing = Pick(baseline.EnableInternetSharing, requested.EnableInternetSharing, latest.EnableInternetSharing),
        DefaultClipboardSyncMode = Pick(baseline.DefaultClipboardSyncMode, requested.DefaultClipboardSyncMode, latest.DefaultClipboardSyncMode),
    };

    private static IslandAppearanceSettings MergeIsland(IslandAppearanceSettings baseline, IslandAppearanceSettings requested, IslandAppearanceSettings latest) => latest with
    {
        Theme = Pick(baseline.Theme, requested.Theme, latest.Theme),
        Resident = Pick(baseline.Resident, requested.Resident, latest.Resident),
        ShowLogoWhenIdle = Pick(baseline.ShowLogoWhenIdle, requested.ShowLogoWhenIdle, latest.ShowLogoWhenIdle),
        ForceShowOverFullscreen = Pick(baseline.ForceShowOverFullscreen, requested.ForceShowOverFullscreen, latest.ForceShowOverFullscreen),
        HideDelayMilliseconds = Pick(baseline.HideDelayMilliseconds, requested.HideDelayMilliseconds, latest.HideDelayMilliseconds),
        CompactScale = Pick(baseline.CompactScale, requested.CompactScale, latest.CompactScale),
        ExpandedScale = Pick(baseline.ExpandedScale, requested.ExpandedScale, latest.ExpandedScale),
        RightClickHoldToMove = Pick(baseline.RightClickHoldToMove, requested.RightClickHoldToMove, latest.RightClickHoldToMove),
    };

    private static LyricsSettings MergeLyrics(LyricsSettings baseline, LyricsSettings requested, LyricsSettings latest) => latest with
    {
        CacheMaximumBytes = Pick(baseline.CacheMaximumBytes, requested.CacheMaximumBytes, latest.CacheMaximumBytes),
        AiTranslationEnabled = Pick(baseline.AiTranslationEnabled, requested.AiTranslationEnabled, latest.AiTranslationEnabled),
        AiLyricsGpuAccelerationEnabled = Pick(baseline.AiLyricsGpuAccelerationEnabled, requested.AiLyricsGpuAccelerationEnabled, latest.AiLyricsGpuAccelerationEnabled),
        AiLyricsGpuBackend = Pick(baseline.AiLyricsGpuBackend, requested.AiLyricsGpuBackend, latest.AiLyricsGpuBackend),
        AiModelId = Pick(baseline.AiModelId, requested.AiModelId, latest.AiModelId),
        GlowMode = Pick(baseline.GlowMode, requested.GlowMode, latest.GlowMode),
        SimplifiedGlow = Pick(baseline.SimplifiedGlow, requested.SimplifiedGlow, latest.SimplifiedGlow),
        FontSize = Pick(baseline.FontSize, requested.FontSize, latest.FontSize),
        Enabled = Pick(baseline.Enabled, requested.Enabled, latest.Enabled),
        Mode = Pick(baseline.Mode, requested.Mode, latest.Mode),
        Provider = Pick(baseline.Provider, requested.Provider, latest.Provider),
        BackupProvider = Pick(baseline.BackupProvider, requested.BackupProvider, latest.BackupProvider),
        SearchRemainingProviders = Pick(baseline.SearchRemainingProviders, requested.SearchRemainingProviders, latest.SearchRemainingProviders),
        SecondaryLyrics = Pick(baseline.SecondaryLyrics, requested.SecondaryLyrics, latest.SecondaryLyrics),
        ShowAiLyricsLabel = Pick(baseline.ShowAiLyricsLabel, requested.ShowAiLyricsLabel, latest.ShowAiLyricsLabel),
        WordSyncedHighlighting = Pick(baseline.WordSyncedHighlighting, requested.WordSyncedHighlighting, latest.WordSyncedHighlighting),
        DelayMilliseconds = Pick(baseline.DelayMilliseconds, requested.DelayMilliseconds, latest.DelayMilliseconds),
        Scrolling = Pick(baseline.Scrolling, requested.Scrolling, latest.Scrolling),
        ScrollingMaxWidth = Pick(baseline.ScrollingMaxWidth, requested.ScrollingMaxWidth, latest.ScrollingMaxWidth),
        UnlimitedScrollingWidth = Pick(baseline.UnlimitedScrollingWidth, requested.UnlimitedScrollingWidth, latest.UnlimitedScrollingWidth),
        LocalLrcDirectory = Pick(baseline.LocalLrcDirectory, requested.LocalLrcDirectory, latest.LocalLrcDirectory),
    };

    private static T Pick<T>(T baseline, T requested, T latest) =>
        EqualityComparer<T>.Default.Equals(baseline, requested) ? latest : requested;
}
