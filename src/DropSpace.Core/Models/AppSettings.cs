using DropSpace.Core.Updates;

namespace DropSpace.Core.Models;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum CloseBehavior
{
    HideToTray,
    Exit,
}

public enum OverlayDisplayMode
{
    DynamicIsland,
    Notch,
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

public sealed record AppSettings
{
    public const int CurrentVersion = 4;

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

    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.HideToTray;

    public bool CloseExplanationShown { get; init; }

    public string LaunchPage { get; init; } = "Space";

    public OverlayDisplayMode OverlayDisplayMode { get; init; } = OverlayDisplayMode.DynamicIsland;

    public OverlayMotionPreference OverlayMotion { get; init; } = OverlayMotionPreference.System;

    public OverlayMonitorPreference OverlayMonitor { get; init; } = OverlayMonitorPreference.Automatic;

    public bool AutoCheckForUpdates { get; init; } = true;

    public bool AutoDownloadUpdates { get; init; } = true;

    public bool AutoInstallUpdates { get; init; }

    public UpdateChannel UpdateChannel { get; init; } = UpdateChannel.Stable;

    public DateTimeOffset? LastUpdateCheckUtc { get; init; }

    public AppSettings WithSafeUiPreferences() => this with
    {
        Theme = ThemePreference.System,
        OverlayDisplayMode = OverlayDisplayMode.DynamicIsland,
        OverlayMotion = OverlayMotionPreference.System,
        OverlayMonitor = OverlayMonitorPreference.Automatic,
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

        if (!Enum.IsDefined(CloseBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(CloseBehavior));
        }

        if (!Enum.IsDefined(OverlayDisplayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(OverlayDisplayMode));
        }

        if (!Enum.IsDefined(OverlayMotion))
        {
            throw new ArgumentOutOfRangeException(nameof(OverlayMotion));
        }

        if (!Enum.IsDefined(OverlayMonitor))
        {
            throw new ArgumentOutOfRangeException(nameof(OverlayMonitor));
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
}
