namespace DropSpace.Core.Media;

public enum MediaPlaybackState
{
    Unknown,
    Playing,
    Paused,
    Stopped,
}

public enum MediaRepeatMode
{
    None,
    Track,
    List,
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
    DateTimeOffset LastUpdated,
    string AlbumArtist = "",
    int TrackNumber = 0,
    bool? ShuffleActive = null,
    MediaRepeatMode? RepeatMode = null,
    bool CanChangeShuffle = false,
    bool CanChangeRepeat = false)
{
    public bool IsActive => !string.IsNullOrWhiteSpace(TrackTitle) && PlaybackState is not MediaPlaybackState.Stopped;

    /// <summary>Runtime-only key that excludes position and volatile timeline metadata.</summary>
    public string TrackIdentity => string.Join('\u001f', SessionId, SourceAppUserModelId, TrackTitle, Artist, AlbumArtist, AlbumTitle, TrackNumber);

    /// <summary>
    /// Compares track metadata while treating a temporarily missing duration as unknown. Media
    /// sessions can briefly publish a zero timeline while Apple Music refreshes its metadata.
    /// </summary>
    public bool IsSameTrack(MediaSessionSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(TrackIdentity, other.TrackIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        var left = Timeline.Duration;
        var right = other.Timeline.Duration;
        return left <= TimeSpan.Zero || right <= TimeSpan.Zero || Math.Abs((left - right).TotalSeconds) <= 2;
    }

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

    Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default);

    Task SetRepeatModeAsync(MediaRepeatMode mode, CancellationToken cancellationToken = default);
}
