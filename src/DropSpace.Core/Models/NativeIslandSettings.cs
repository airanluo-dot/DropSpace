namespace DropSpace.Core.Models;

public enum LyricsMode { Online, LocalLrc }
public enum LyricsProviderKind { NetEase, QqMusic, Kugou, Lrclib, Amll, LocalLrc }
public enum LyricsGpuBackend { Automatic, Vulkan, Cuda }
public enum LyricsSelectionMode { Rules, AiAssisted, AiRanked }

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
    public long CacheMaximumBytes { get; init; } = 1_000_000_000;
    public bool AiTranslationEnabled { get; init; }
    // AI itself remains opt-in. When enabled, prefer verified GPU acceleration with CPU fallback.
    public bool AiLyricsGpuAccelerationEnabled { get; init; } = true;
    public LyricsGpuBackend AiLyricsGpuBackend { get; init; } = LyricsGpuBackend.Automatic;
    public string AiModelId { get; init; } = "hy-mt2-18-q8-plain-beta";
    // Retired diagnostic compatibility slot; never read or written in settings JSON.
    [System.Text.Json.Serialization.JsonIgnore]
    public string AiSelectionModelId { get; init; } = "hy-mt2-18-q8-plain-beta";
    public Lyrics.LyricsGlowMode GlowMode { get; init; }
    // Missing in older settings: keep the full surrounding halo.
    public bool SimplifiedGlow { get; init; }
    public double FontSize { get; init; } = 16;
    [System.Text.Json.Serialization.JsonIgnore]
    public double OriginalFontSize => FontSize;
    [System.Text.Json.Serialization.JsonIgnore]
    public double TranslationFontSize => FontSize * 0.875;
    public bool Enabled { get; init; } = true;
    public LyricsMode Mode { get; init; }
    public LyricsProviderKind Provider { get; init; } = LyricsProviderKind.NetEase;
    public LyricsProviderKind? BackupProvider { get; init; }
    public bool SearchRemainingProviders { get; init; } = true;
    // Retired diagnostic compatibility slot; the application always uses rules.
    [System.Text.Json.Serialization.JsonIgnore]
    public LyricsSelectionMode SelectionMode { get; init; } = LyricsSelectionMode.Rules;
    public bool SecondaryLyrics { get; init; }
    public bool ShowAiLyricsLabel { get; init; } = true;
    public bool WordSyncedHighlighting { get; init; } = true;
    public int DelayMilliseconds { get; init; }
    public bool Scrolling { get; init; } = true;
    public int ScrollingMaxWidth { get; init; } = 260;
    public bool UnlimitedScrollingWidth { get; init; }
    public string LocalLrcDirectory { get; init; } = string.Empty;
}

public sealed record IslandAppearanceSettings
{
    // Opt-in only. Missing fields in existing settings remain false; the old
    // fullscreen suppression preference is retained and resumes when this is off.
    public bool Resident { get; init; }
    public bool ShowLogoWhenIdle { get; init; }
    public bool ForceShowOverFullscreen { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
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
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SuppressOverFullscreen { get; init; } = true;
}

public sealed record WidgetSettings
{
    public bool Enabled { get; init; } = true;
    public Widgets.WidgetLayout Layout { get; init; } = Widgets.WidgetLayout.Default;
}
