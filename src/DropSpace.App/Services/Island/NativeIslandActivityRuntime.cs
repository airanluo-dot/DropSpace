using DropSpace.App.Services.Audio;
using DropSpace.App.Services.Lyrics;
using DropSpace.App.Services.Media;
using DropSpace.App.Services.Notifications;
using DropSpace.App.Services.Volume;
using DropSpace.App.Services.Widgets;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using DropSpace.Core.SystemActivities;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Island;

public sealed class NativeIslandActivityRuntime : IAsyncDisposable
{
    private const string MediaSourceId = "media";
    private readonly IIslandActivityRouter _router;
    private readonly WindowsMediaSessionService _media;
    private readonly WindowsSpectrumService _spectrum;
    private readonly WindowsNotificationActivityService _notifications;
    private readonly WindowsVolumeActivityService _volume;
    private readonly NativeWidgetActivityService _widgets;
    private readonly LyricsService _lyrics;
    private readonly ILogger<NativeIslandActivityRuntime> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IslandActivitySettings _mediaSettings = new();
    private LyricsSettings _lyricsSettings = new();
    private LyricsDocument? _lyricsDocument;
    private string? _lyricsTrackKey;
    private CancellationTokenSource? _lyricsCancellation;
    private bool _disposed;
    private Guid _mediaActivityId;

    public NativeIslandActivityRuntime(
