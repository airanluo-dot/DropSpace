using DropSpace.Core.Updates;
using DropSpace.Core.Transfer;

namespace DropSpace.Core.Models;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum AppLanguagePreference
{
    System,
    English,
    SimplifiedChinese,
}

public enum CloseBehavior
{
    HideToTray,
    Exit,
}

public enum OverlayMotionPreference
{
    System,
    Full,
    Reduced,
}

public enum OverlayMonitorPreference
{
    Automatic,
    Primary,
}

public enum FileDragWakeMode
{
    SmartExperimental,
    ClassicTopEdge,
    Disabled,
}

public enum OverlayPlacementMode
{
    Automatic,
    Custom,
}

public sealed record OverlayCustomPlacement(double X, double Y);

public sealed record OverlayMonitorPlacement(
    OverlayPlacementMode Mode,
    double X,
    double Y)
{
    public OverlayCustomPlacement CustomCoordinates => new(X, Y);
}

public sealed record AppSettings
{
    public const int CurrentVersion = 10;

    public int Version { get; init; } = CurrentVersion;

    public bool ClipboardPaused { get; init; }

    public bool CaptureImages { get; init; } = true;

    public bool CaptureFiles { get; init; } = true;

    public bool CaptureFolders { get; init; } = true;

    public bool StartWithWindows { get; init; } = true;

    public int RetentionDays { get; init; } = 30;

    public int RetentionItemCount { get; init; } = 1_000;

    public long MaxImageBytes { get; init; } = 25L * 1024 * 1024;

    public long MaxImagePixels { get; init; } = 50_000_000;

    public long MaxClipboardFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public long MaxClipboardFileTotalBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public int MaxClipboardFileItems { get; init; } = 100;

    public int MaxTextCharacters { get; init; } = 2 * 1024 * 1024;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public AppLanguagePreference Language { get; init; } = AppLanguagePreference.System;

    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.HideToTray;

    public bool CloseExplanationShown { get; init; }

    public string LaunchPage { get; init; } = "Space";

    public OverlayMotionPreference OverlayMotion { get; init; } = OverlayMotionPreference.System;

    public OverlayMonitorPreference OverlayMonitor { get; init; } = OverlayMonitorPreference.Automatic;

    public FileDragWakeMode FileDragWakeMode { get; init; } = FileDragWakeMode.SmartExperimental;

    // These two fields remain deserializable for the one-time schema 8 migration. They are
    // cleared after migration and are not used as an active source of truth.
    public OverlayPlacementMode OverlayPlacementMode { get; init; } = OverlayPlacementMode.Automatic;

    public Dictionary<string, OverlayCustomPlacement> CustomOverlayPlacements { get; init; } = [];

    public Dictionary<string, OverlayMonitorPlacement> OverlayPlacements { get; init; } = [];

    public string QuickPanelHotkey { get; init; } = "Win+Shift+Space";

    public string[] SmartDragExcludedProcesses { get; init; } = [];

    public bool AutoCheckForUpdates { get; init; } = true;

    public bool AutoDownloadUpdates { get; init; } = true;

    public bool AutoInstallUpdates { get; init; }

    public UpdateChannel UpdateChannel { get; init; } = UpdateChannel.Stable;

    public DateTimeOffset? LastUpdateCheckUtc { get; init; }

    public bool EnableDeviceHandoff { get; init; }

    public bool EnableCrossDeviceClipboard { get; init; }

    public bool EnableNearbySharing { get; init; }

    public bool EnableInternetSharing { get; init; }

    public ClipboardSyncMode DefaultClipboardSyncMode { get; init; } = ClipboardSyncMode.Off;

    public AppSettings WithSafeUiPreferences() => this with
    {
        Theme = ThemePreference.System,
        Language = AppLanguagePreference.System,
        OverlayMotion = OverlayMotionPreference.System,
        OverlayMonitor = OverlayMonitorPreference.Automatic,
        FileDragWakeMode = FileDragWakeMode.SmartExperimental,
        OverlayPlacementMode = OverlayPlacementMode.Automatic,
        CustomOverlayPlacements = [],
        OverlayPlacements = [],
    };

    public AppSettings Validate()
    {
        if (Version is < 1 or > CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported settings version: {Version}.");
        }

        if (RetentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(RetentionDays));
        }

        if (RetentionItemCount is < 10 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(RetentionItemCount));
        }

        if (MaxImageBytes is < 1_048_576 or > 268_435_456)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxImageBytes));
        }

        if (MaxImagePixels is < 1_000_000 or > 200_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxImagePixels));
        }

        if (MaxClipboardFileBytes is < 1_048_576 or > 1_099_511_627_776)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxClipboardFileBytes));
        }

        if (MaxClipboardFileTotalBytes is < 1_048_576 or > 4_398_046_511_104 ||
            MaxClipboardFileTotalBytes < MaxClipboardFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxClipboardFileTotalBytes));
        }

        if (MaxClipboardFileItems is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxClipboardFileItems));
        }

        if (MaxTextCharacters is < 1_024 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTextCharacters));
        }

        if (!Enum.IsDefined(Theme))
        {
            throw new ArgumentOutOfRangeException(nameof(Theme));
        }

        if (!Enum.IsDefined(Language))
        {
            throw new ArgumentOutOfRangeException(nameof(Language));
        }

        if (!Enum.IsDefined(CloseBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(CloseBehavior));
        }

        if (!Enum.IsDefined(OverlayMotion))
        {
            throw new ArgumentOutOfRangeException(nameof(OverlayMotion));
        }

        if (!Enum.IsDefined(OverlayMonitor))
        {
            throw new ArgumentOutOfRangeException(nameof(OverlayMonitor));
        }

        if (!Enum.IsDefined(FileDragWakeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(FileDragWakeMode));
        }

        if (!Enum.IsDefined(DefaultClipboardSyncMode))
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultClipboardSyncMode));
        }

        if (!Enum.IsDefined(OverlayPlacementMode))
        {
            throw new ArgumentOutOfRangeException(nameof(OverlayPlacementMode));
        }

        if (CustomOverlayPlacements is null || CustomOverlayPlacements.Count > 64 || CustomOverlayPlacements.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 256 ||
                entry.Value is null ||
                !double.IsFinite(entry.Value.X) || !double.IsFinite(entry.Value.Y) ||
                Math.Abs(entry.Value.X) > 100_000 || Math.Abs(entry.Value.Y) > 100_000))
        {
            throw new ArgumentOutOfRangeException(nameof(CustomOverlayPlacements));
        }

        if (OverlayPlacements is null || OverlayPlacements.Count > 64 || OverlayPlacements.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 256 ||
                entry.Value is null ||
                !Enum.IsDefined(entry.Value.Mode) ||
                !double.IsFinite(entry.Value.X) || !double.IsFinite(entry.Value.Y) ||
                Math.Abs(entry.Value.X) > 100_000 || Math.Abs(entry.Value.Y) > 100_000))
        {
            throw new ArgumentOutOfRangeException(nameof(OverlayPlacements));
        }

        if (!IsSupportedHotkey(QuickPanelHotkey))
        {
            throw new ArgumentOutOfRangeException(nameof(QuickPanelHotkey));
        }

        if (SmartDragExcludedProcesses is null || SmartDragExcludedProcesses.Length > 128 || SmartDragExcludedProcesses.Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 260 ||
                value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(SmartDragExcludedProcesses));
        }

        if (!Enum.IsDefined(UpdateChannel))
        {
            throw new ArgumentOutOfRangeException(nameof(UpdateChannel));
        }

        if (LastUpdateCheckUtc is { } lastCheck && lastCheck.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LastUpdateCheckUtc), "Update timestamps must be stored in UTC.");
        }

        return this;
    }

    private static bool IsSupportedHotkey(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture) || gesture.Length > 128)
        {
            return false;
        }
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var hasModifier = parts.Any(part => part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                                            part.Equals("Shift", StringComparison.OrdinalIgnoreCase) ||
                                            part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                                            part.Equals("Alt", StringComparison.OrdinalIgnoreCase));
        var keys = parts.Count(part => part.Equals("Space", StringComparison.OrdinalIgnoreCase) ||
                                      part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]));
        return hasModifier && keys == 1 && parts.All(part =>
            part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("Shift", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("Space", StringComparison.OrdinalIgnoreCase) ||
            part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]));
    }
}
