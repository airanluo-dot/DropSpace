using DropSpace.Core.Lyrics;
using DropSpace.Core.Island;
using DropSpace.Core.Widgets;

namespace DropSpace.Core.Models;

public enum IslandAppearanceStyle
{
    Default,
    Acrylic,
    Mica,
    DynamicArtwork,
}

public enum IslandDockPreset
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
    Custom,
}

public enum CoverShape
{
    Square,
    Circle,
}

public sealed record IslandActivitySettings
{
    public bool EnableMediaActivity { get; init; } = true;
    public bool ShowArtwork { get; init; } = true;
    public bool ShowTitle { get; init; } = true;
    public bool ShowArtist { get; init; } = true;
    public bool ShowProgress { get; init; } = true;
    public bool ShowCompactControls { get; init; } = true;
    public bool ShowSpectrum { get; init; } = true;
    public bool ShowLyricsInCompact { get; init; } = true;
    public bool CompactDynamicWidth { get; init; } = true;
    public string[] AllowedMediaSourceAppIds { get; init; } = [];
}

public sealed record LyricsSettings
{
    public bool Enabled { get; init; }
    public LyricsMode Mode { get; init; } = LyricsMode.Online;
    public LyricsProviderKind Provider { get; init; } = LyricsProviderKind.Lrclib;
    public bool SecondaryLyrics { get; init; }
    public bool WordSyncedHighlighting { get; init; } = true;
    public int DelayMilliseconds { get; init; }
    public bool Scrolling { get; init; } = true;
    public int ScrollingMaxWidth { get; init; } = 260;
    public string LocalLrcDirectory { get; init; } = string.Empty;
}

public sealed record SystemActivitySettings
{
    public bool ShowWindowsNotifications { get; init; }
    public bool ShowVolumeChanges { get; init; }
    public bool SuppressOverFullscreen { get; init; } = true;
}

public sealed record IslandAppearanceSettings
{
    public IslandAppearanceStyle Style { get; init; } = IslandAppearanceStyle.Default;
    public double CompactScale { get; init; } = 1;
    public double ExpandedScale { get; init; } = 1;
    public double CompactBaseWidth { get; init; } = 340;
    public double CompactBaseHeight { get; init; } = 64;
    public double ExpandedWidth { get; init; } = 560;
    public double ExpandedHeight { get; init; } = 340;
    public CoverShape CompactCoverShape { get; init; } = CoverShape.Circle;
    public CoverShape ExpandedCoverShape { get; init; } = CoverShape.Square;
    public bool RotateCover { get; init; }
    public bool MotionBlur { get; init; }
    public IslandDockPreset DockPreset { get; init; } = IslandDockPreset.TopCenter;
    public double HorizontalOffset { get; init; }
    public double VerticalOffset { get; init; }
    public bool RightClickHoldToMove { get; init; } = true;
    public bool AutoHide { get; init; } = true;
    public int HideDelayMilliseconds { get; init; } = IdleHidePolicy.DefaultDelayMilliseconds;
    public int HiddenWidth { get; init; } = 0;
    public ExpandedIslandPage LastExpandedPage { get; init; } = ExpandedIslandPage.Files;
}

public sealed record WidgetSettings
{
    public bool Enabled { get; init; } = true;
    public bool ClockEnabled { get; init; } = true;
    public bool CalendarEnabled { get; init; } = true;
    public bool ResourceUsageEnabled { get; init; } = true;
    public bool CompactTimeEnabled { get; init; } = true;
    public bool CompactResourceUsageEnabled { get; init; } = true;
    public WidgetLayout Layout { get; init; } = WidgetLayout.Default;
}
