namespace DropSpace.Core.Models;

public enum LyricsMode { Online, LocalLrc }
public enum LyricsProviderKind { NetEase, QqMusic, Kugou, Lrclib, Amll, LocalLrc }

public sealed record IslandActivitySettings
{
    public bool EnableMediaActivity { get; init; } = true;
    public bool ShowArtwork { get; init; } = true;
    public bool ShowProgress { get; init; } = true;
    public bool ShowSpectrum { get; init; } = true;
    public bool ShowLyricsInCompact { get; init; } = true;
    public bool CompactDynamicWidth { get; init; } = true;
    public string[] AllowedMediaSourceAppIds { get; init; } = [];
    public bool UseMediaSourceAllowList { get; init; }
}

public sealed record LyricsSettings
{
    public bool Enabled { get; init; } = true;
    public LyricsMode Mode { get; init; }
    public LyricsProviderKind Provider { get; init; } = LyricsProviderKind.NetEase;
    public LyricsProviderKind? BackupProvider { get; init; }
    public bool SearchRemainingProviders { get; init; }
    public bool SecondaryLyrics { get; init; }
    public bool WordSyncedHighlighting { get; init; } = true;
    public int DelayMilliseconds { get; init; }
    public bool Scrolling { get; init; } = true;
    public int ScrollingMaxWidth { get; init; } = 260;
    public string LocalLrcDirectory { get; init; } = string.Empty;
}

public sealed record IslandAppearanceSettings
{
    public bool AutoHide { get; init; } = true;
    public int HideDelayMilliseconds { get; init; } = 3_000;
    public double CompactScale { get; init; } = 1;
    public double ExpandedScale { get; init; } = 1;
    public bool RightClickHoldToMove { get; init; } = true;
}

public sealed record SystemActivitySettings
{
    public bool ShowWindowsNotifications { get; init; }
    public bool ShowVolumeChanges { get; init; }
    public bool SuppressOverFullscreen { get; init; } = true;
}

public sealed record WidgetSettings
{
    public bool Enabled { get; init; } = true;
    public Widgets.WidgetLayout Layout { get; init; } = Widgets.WidgetLayout.Default;
}
