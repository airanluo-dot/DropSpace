namespace DropSpace.Core.Media;

/// <summary>Monotonic playback interpolation. Missing native timelines are explicitly estimated.</summary>
public sealed class MediaPlaybackClock(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MaximumEstimatedObservationGap = TimeSpan.FromSeconds(5);
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
        var sameTrack = _hasSnapshot && _session.IsSameTrack(session);
        var validTimelineTimestamp = session.Timeline.LastUpdated.Year >= 2000 &&
            session.Timeline.LastUpdated >= now.AddMinutes(-5) &&
            session.Timeline.LastUpdated <= now.AddMinutes(5);
        var validSessionTimestamp = session.LastUpdated.Year >= 2000 &&
            session.LastUpdated >= now.AddMinutes(-5) &&
            session.LastUpdated <= now.AddMinutes(5);
        var native = validTimelineTimestamp && (session.Timeline.End > session.Timeline.Start || session.Timeline.Position > TimeSpan.Zero);
        // Apple Music refreshes timestamps several times within the same whole
        // second. Timestamp-only updates are not new position observations and
        // must not repeatedly rewind the interpolated word highlight.
        var nativeChanged = !sameTrack || IsEstimated || session.Timeline.Position != _session.Timeline.Position || session.PlaybackState != _session.PlaybackState;
        var elapsedSinceObservation = _hasSnapshot ? _time.GetElapsedTime(_anchor) : TimeSpan.Zero;
        var position = sameTrack ? Position.TotalSeconds : Math.Max(0, session.Timeline.Position.TotalSeconds);
        if (sameTrack && !native && elapsedSinceObservation > MaximumEstimatedObservationGap)
        {
            // An invalid Apple timeline cannot tell us where playback resumed after a lock,
            // sleep, or a long hidden-window interval. Do not jump lyrics by the entire wall
            // clock gap; hold the last trusted estimate until SMTC supplies a real position or
            // the track changes.
            position = _position;
        }
        if (!sameTrack && !native && session.PlaybackState == MediaPlaybackState.Playing && validSessionTimestamp)
            position += Math.Clamp((now - session.LastUpdated).TotalSeconds, 0, 30);
        if (native && nativeChanged)
        {
            var interpolated = position;
            position = Math.Max(0, session.Timeline.Position.TotalSeconds);
            if (session.PlaybackState == MediaPlaybackState.Playing)
            {
                var rate = double.IsFinite(session.Timeline.PlaybackRate) && session.Timeline.PlaybackRate is > 0 and <= 8 ? session.Timeline.PlaybackRate : 1;
                position += Math.Clamp((now - session.Timeline.LastUpdated).TotalSeconds, 0, 86_400) * rate;
                var advance = (session.Timeline.Position - _session.Timeline.Position).TotalSeconds;
                if (sameTrack && _session.PlaybackState == MediaPlaybackState.Playing &&
                    session.Timeline.Position.Ticks % TimeSpan.TicksPerSecond == 0 && advance is >= 0 and <= 1 &&
                    Math.Abs(position - interpolated) < 1)
                    position = Math.Max(interpolated, position);
            }
        }
        _position = position; _anchor = _time.GetTimestamp(); _session = session; _hasSnapshot = true;
        IsEstimated = !native;
    }
}
