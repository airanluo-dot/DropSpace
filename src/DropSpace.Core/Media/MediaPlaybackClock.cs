namespace DropSpace.Core.Media;

/// <summary>Monotonic playback interpolation. Missing native timelines are explicitly estimated.</summary>
public sealed class MediaPlaybackClock(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MaximumEstimatedObservationGap = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private MediaSessionSnapshot _session = MediaSessionSnapshot.Empty;
    private MediaTimelineSnapshot? _lastNativeTimeline;
    private double _position;
    private long _anchor;
    private long _lastObservation;
    private double _lastObservedPosition;
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
            _lastObservedPosition = Math.Clamp(_position + elapsed, minimum, Math.Max(minimum, maximum));
            _lastObservation = _time.GetTimestamp();
            return TimeSpan.FromSeconds(_lastObservedPosition);
        }
    }

    public void Update(MediaSessionSnapshot session)
    {
        var now = _time.GetUtcNow();
        var sameTrack = _hasSnapshot && _session.IsSameTrack(session);
        var timeline = session.Timeline;
        var previousNative = sameTrack ? _lastNativeTimeline : null;
        var previousReport = previousNative ?? (sameTrack ? _session.Timeline : null);
        var positionChangedWithoutTimestamp = previousReport?.LastUpdated == timeline.LastUpdated &&
            previousReport.Position != timeline.Position;
        // A paused sample does not age out. Some SMTC publishers also keep their timestamp
        // fixed while publishing new positions, including after an explicit seek.
        var validTimelineTimestamp = timeline.LastUpdated.Year >= 2000 && timeline.LastUpdated <= now.AddMinutes(5) &&
            (timeline.LastUpdated >= now.AddMinutes(-5) || session.PlaybackState == MediaPlaybackState.Paused ||
             previousNative?.LastUpdated == timeline.LastUpdated || positionChangedWithoutTimestamp);
        var validSessionTimestamp = session.LastUpdated.Year >= 2000 &&
            session.LastUpdated >= now.AddMinutes(-5) &&
            session.LastUpdated <= now.AddMinutes(5);
        var native = validTimelineTimestamp && timeline.HasReportedPosition;
        // Apple Music refreshes timestamps several times within the same whole
        // second. Timestamp-only updates are not new position observations and
        // must not repeatedly rewind the interpolated word highlight.
        var nativeChanged = previousNative is null || timeline.Position != previousNative.Position ||
            session.PlaybackState != _session.PlaybackState || (IsEstimated && timeline.LastUpdated != previousNative.LastUpdated);
        var elapsedSinceObservation = _hasSnapshot ? _time.GetElapsedTime(_lastObservation) : TimeSpan.Zero;
        var lastObservedPosition = _lastObservedPosition;
        var position = sameTrack ? Position.TotalSeconds : Math.Max(0, session.Timeline.Position.TotalSeconds);
        if (sameTrack && !native && elapsedSinceObservation > MaximumEstimatedObservationGap)
        {
            // An invalid Apple timeline cannot tell us where playback resumed after a lock,
            // sleep, or a long hidden-window interval. Do not jump lyrics by the entire wall
            // clock gap; hold the last trusted estimate until SMTC supplies a real position or
            // the track changes.
            position = lastObservedPosition;
        }
        if (!sameTrack && !native && session.PlaybackState == MediaPlaybackState.Playing && validSessionTimestamp)
            position += Math.Clamp((now - session.LastUpdated).TotalSeconds, 0, 30);
        if (native && nativeChanged)
        {
            var interpolated = position;
            // A state-only pause/resume event may carry the unchanged native
            // observation. Reusing that old position rewinds the lyric clock.
            var staleStateTransition = previousNative is not null &&
                session.PlaybackState != _session.PlaybackState &&
                timeline.Position == previousNative.Position && timeline.LastUpdated == previousNative.LastUpdated;
            var nativeSeconds = Math.Max(0, session.Timeline.Position.TotalSeconds);
            var appleSeconds = MediaProcessIdentityPolicy.IsAppleMusic(session.SourceAppUserModelId) &&
                session.Timeline.PlaybackRate == 1 && session.Timeline.Position.Ticks % TimeSpan.TicksPerSecond == 0;
            var advance = (timeline.Position - (previousNative?.Position ?? TimeSpan.Zero)).TotalSeconds;
            var lag = interpolated - nativeSeconds;
            var trailingAppleTick = appleSeconds && previousNative is not null &&
                advance is >= 0 and <= 2 && lag is >= 0 and < 3;
            position = staleStateTransition ? interpolated : nativeSeconds;
            if (appleSeconds && previousNative is not null && session.PlaybackState == MediaPlaybackState.Paused &&
                lag >= 0)
            {
                var confirmingStop = _session.PlaybackState == MediaPlaybackState.Playing ||
                    interpolated >= previousNative.Position.TotalSeconds + 1;
                // State, a final playing tick and the confirmed stop may arrive separately.
                if (lag < 1 || (confirmingStop && trailingAppleTick)) position = interpolated;
            }
            if (session.PlaybackState == MediaPlaybackState.Playing)
            {
                var rate = double.IsFinite(session.Timeline.PlaybackRate) && session.Timeline.PlaybackRate is > 0 and <= 8 ? session.Timeline.PlaybackRate : 1;
                var readAt = validSessionTimestamp ? session.LastUpdated : now;
                var observation = positionChangedWithoutTimestamp ? readAt : timeline.LastUpdated;
                if (sameTrack && _session.PlaybackState != MediaPlaybackState.Playing)
                {
                    // Apple Music can resume with the paused position and its old timestamp,
                    // then publish a fresh position a second later. The age of that timestamp
                    // includes the paused interval and must not be counted as playback.
                    if (observation < readAt) observation = readAt;
                    if (trailingAppleTick) position = interpolated;
                }
                position += Math.Clamp((now - observation).TotalSeconds, 0, 86_400) * rate;
                if (appleSeconds && previousNative is null && nativeSeconds > 2)
                {
                    // Joining an already playing Apple Music session has no transport anchor.
                    // Its integer observation is published one reporting period late, with
                    // an unknown fractional second. Use the midpoint only on initial attach.
                    position += 1.5;
                }
                else if (appleSeconds && previousNative is not null &&
                    _session.PlaybackState == MediaPlaybackState.Playing && advance is > 0 and <= 2 &&
                    interpolated >= position && interpolated - position < 3)
                {
                    // Apple Music samples whole seconds roughly once a second. A normal
                    // report can skip an integer or trail an observed resume by two seconds.
                    // Keep the running transport anchor. Seeks and long stalls re-anchor.
                    position = interpolated;
                }
                else if (previousNative is not null && _session.PlaybackState == MediaPlaybackState.Playing &&
                    session.Timeline.Position.Ticks % TimeSpan.TicksPerSecond == 0 && advance is >= 0 and <= 1 &&
                    Math.Abs(position - interpolated) < 1)
                    position = Math.Max(interpolated, position);
            }
        }
        _position = position; _anchor = _time.GetTimestamp(); _session = session; _hasSnapshot = true;
        _lastObservedPosition = position; _lastObservation = _anchor;
        IsEstimated = !native;
        _lastNativeTimeline = native ? timeline : previousNative;
    }
}
