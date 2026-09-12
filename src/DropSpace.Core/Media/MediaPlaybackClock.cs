namespace DropSpace.Core.Media;

/// <summary>Monotonic playback interpolation. Missing native timelines are explicitly estimated.</summary>
public sealed class MediaPlaybackClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private MediaSessionSnapshot _session = MediaSessionSnapshot.Empty;
    private double _position;
    private long _anchor;
    private bool _hasSnapshot;
    public bool IsEstimated { get; private set; } = true;
    public TimeSpan Position
    {
        get
        {
            var rate = double.IsFinite(_session.Timeline.PlaybackRate) && _session.Timeline.PlaybackRate is > 0 and <= 8 ? _session.Timeline.PlaybackRate : 1;
            var elapsed = _hasSnapshot && _session.PlaybackState == MediaPlaybackState.Playing ? Math.Max(0, _time.GetElapsedTime(_anchor).TotalSeconds) * rate : 0;
            var minimum = Math.Max(0, _session.Timeline.Start.TotalSeconds);
            var maximum = _session.Timeline.End > _session.Timeline.Start ? _session.Timeline.End.TotalSeconds : 86_400;
            return TimeSpan.FromSeconds(Math.Clamp(_position + elapsed, minimum, Math.Max(minimum, maximum)));
        }
    }

    public void Update(MediaSessionSnapshot session)
    {
        var now = _time.GetUtcNow();
        var sameTrack = _hasSnapshot && _session.SourceAppUserModelId == session.SourceAppUserModelId &&
            _session.TrackTitle == session.TrackTitle && _session.Artist == session.Artist;
        var validTimestamp = session.Timeline.LastUpdated.Year >= 2000 && session.Timeline.LastUpdated <= now.AddMinutes(5);
        var native = validTimestamp && (session.Timeline.End > session.Timeline.Start || session.Timeline.Position > TimeSpan.Zero);
        var nativeChanged = !sameTrack || session.Timeline.Position != _session.Timeline.Position || session.Timeline.LastUpdated != _session.Timeline.LastUpdated;
        var position = sameTrack ? Position.TotalSeconds : Math.Max(0, session.Timeline.Position.TotalSeconds);
        if (native && nativeChanged)
        {
            position = Math.Max(0, session.Timeline.Position.TotalSeconds);
            if (session.PlaybackState == MediaPlaybackState.Playing)
            {
                var rate = double.IsFinite(session.Timeline.PlaybackRate) && session.Timeline.PlaybackRate is > 0 and <= 8 ? session.Timeline.PlaybackRate : 1;
                position += Math.Clamp((now - session.Timeline.LastUpdated).TotalSeconds, 0, 86_400) * rate;
            }
        }
        _position = position; _anchor = _time.GetTimestamp(); _session = session; _hasSnapshot = true;
        IsEstimated = !native;
    }
}
