namespace DropSpace.Core.Media;

public enum MediaPlaybackState
{
    Unknown,
    Playing,
    Paused,
    Stopped,
}

public sealed record MediaTimelineSnapshot(
    TimeSpan Position,
    TimeSpan Start,
    TimeSpan End,
    double PlaybackRate,
    DateTimeOffset LastUpdated)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

public sealed record MediaSessionSnapshot(
    string SessionId,
    string SourceAppUserModelId,
    string SourceDisplayName,
    string TrackTitle,
    string Artist,
    string AlbumTitle,
    byte[]? Artwork,
    MediaPlaybackState PlaybackState,
    bool CanPlay,
    bool CanPause,
    bool CanSkipNext,
    bool CanSkipPrevious,
    bool CanSeek,
    MediaTimelineSnapshot Timeline,
    DateTimeOffset LastUpdated)
{
    public bool IsActive => !string.IsNullOrWhiteSpace(TrackTitle) && PlaybackState is not MediaPlaybackState.Stopped;

    public static MediaSessionSnapshot Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        MediaPlaybackState.Unknown,
        false,
        false,
        false,
        false,
        false,
        new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.UtcNow),
        DateTimeOffset.UtcNow);
}

public interface IMediaSessionService : IAsyncDisposable
{
    event EventHandler<MediaSessionSnapshot>? Changed;

    MediaSessionSnapshot Current { get; }

    bool IsAvailable { get; }

    string AvailabilityReason { get; }

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task PlayPauseAsync(CancellationToken cancellationToken = default);

    Task SkipNextAsync(CancellationToken cancellationToken = default);

    Task SkipPreviousAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
}
