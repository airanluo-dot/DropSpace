using System.Collections.Generic;

namespace DropSpace.Core.Models;

/// <summary>Applies only the fields edited by a form to the latest persisted snapshot.</summary>
public static class SettingsChangePolicy
{
    public static AppSettings Merge(AppSettings baseline, AppSettings requested, AppSettings latest) => latest with
    {
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
        IslandActivity = Pick(baseline.IslandActivity, requested.IslandActivity, latest.IslandActivity),
        Lyrics = Pick(baseline.Lyrics, requested.Lyrics, latest.Lyrics),
        SystemActivities = Pick(baseline.SystemActivities, requested.SystemActivities, latest.SystemActivities),
        IslandAppearance = Pick(baseline.IslandAppearance, requested.IslandAppearance, latest.IslandAppearance),
        Widgets = Pick(baseline.Widgets, requested.Widgets, latest.Widgets),
    };

    private static T Pick<T>(T baseline, T requested, T latest) =>
        EqualityComparer<T>.Default.Equals(baseline, requested) ? latest : requested;
}
