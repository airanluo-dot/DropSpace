namespace DropSpace.Core.Media;

/// <summary>Monotonic playback interpolation. Missing native timelines are explicitly estimated.</summary>
public sealed class MediaPlaybackClock(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MaximumEstimatedObservationGap = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private MediaSessionSnapshot _session = MediaSessionSnapshot.Empty;
    private MediaTimelineSnapshot? _lastNativeTimeline;
    private double _position;
    private double _nativeSecondCeiling = double.PositiveInfinity;
    private long _lastAppleObservation;
    private bool _hasNativePosition;
    private long _anchor;
    private long _lastObservation;
    private double _lastObservedPosition;
    private bool _hasSnapshot;
    // The Windows MediaPlayer used in the native trace publishes positions about every
    // six seconds. One report can arrive after the transport command already succeeded.
    public static readonly TimeSpan SeekObservationWindow = TimeSpan.FromSeconds(8);
    private sealed record AcceptedSeek(TimeSpan Target, double AnchorPosition, long Anchor, long AcceptedAt, DateTimeOffset SentAt);
    private AcceptedSeek? _acceptedSeek;
    private readonly List<AcceptedSeek> _supersededSeeks = [];
    public bool IsSeekEstimate => _acceptedSeek is { } seek && _time.GetElapsedTime(seek.AcceptedAt) >= SeekObservationWindow;
    public bool IsAwaitingSeek => _acceptedSeek is not null && !IsSeekEstimate;
    private double MissingAppleReportSeconds =>
        double.IsFinite(_nativeSecondCeiling) && _session.PlaybackState == MediaPlaybackState.Playing
            ? Math.Max(0, (_time.GetElapsedTime(_lastAppleObservation) - MaximumEstimatedObservationGap).TotalSeconds) : 0;
    public bool IsEstimated => !_hasNativePosition || MissingAppleReportSeconds > 0;
    public TimeSpan Position
    {
        get
        {
            var source = SourcePosition;
            if (_acceptedSeek is not { } pending) return source;
            // Expiry ends active confirmation and labels this position as an estimate.
            // An unchanged native sample cannot establish a return to the old position.
            return TimeSpan.FromSeconds(ReadPosition(pending.AnchorPosition, pending.Anchor));
        }
    }

    public void AcceptSeek(MediaSessionSnapshot before, TimeSpan target, DateTimeOffset sentAt, bool superseded = false)
    {
        if (!_hasSnapshot || !_session.IsSameTrack(before) || !_session.IsActive) return;
        _supersededSeeks.RemoveAll(seek => _time.GetElapsedTime(seek.AcceptedAt) >= SeekObservationWindow);
        var now = _time.GetTimestamp();
        var pending = new AcceptedSeek(target, target.TotalSeconds, now, now, sentAt);
        if (superseded) { _supersededSeeks.Add(pending); return; }
        if (_acceptedSeek is { } previous) _supersededSeeks.Add(previous);
        _acceptedSeek = pending;
        // A native event can win the race with the asynchronous command reply.
        if (IsNewSeekObservation(before.Timeline, _session.Timeline, pending)) ClearAcceptedSeek();
    }

    private bool IsNewSeekObservation(MediaTimelineSnapshot? before, MediaTimelineSnapshot reported, AcceptedSeek pending)
    {
        if (before is null || !reported.HasReportedPosition) return false;
        if (reported.Position == before.Position && (reported.LastUpdated == before.LastUpdated ||
            (_session.PlaybackState != MediaPlaybackState.Paused && reported.Position != pending.Target))) return false;
        // Fixed-stamp publishers signal a fresh observation by changing position.
        if (reported.LastUpdated != before.LastUpdated && reported.LastUpdated < pending.SentAt) return false;
        bool NearTarget(AcceptedSeek seek)
        {
            var elapsed = _session.PlaybackState == MediaPlaybackState.Playing
                ? Math.Clamp((reported.LastUpdated - seek.SentAt).TotalSeconds, 0, SeekObservationWindow.TotalSeconds) : 0;
            var delta = (reported.Position - seek.Target).TotalSeconds;
            return delta >= -1 && delta <= elapsed * _session.Timeline.PlaybackRate + 1;
        }
        return NearTarget(pending) || !_supersededSeeks.Exists(NearTarget);
    }

    private void ClearAcceptedSeek()
    {
        _acceptedSeek = null;
        _supersededSeeks.Clear();
    }

    private TimeSpan SourcePosition
    {
        get
        {
            _lastObservedPosition = Math.Min(_nativeSecondCeiling + MissingAppleReportSeconds, ReadPosition(_position, _anchor));
            _lastObservation = _time.GetTimestamp();
            return TimeSpan.FromSeconds(_lastObservedPosition);
        }
    }

    private double ReadPosition(double position, long anchor)
    {
        var rate = _session.Timeline.PlaybackRate is > 0 and <= 8 ? _session.Timeline.PlaybackRate : 1;
        var elapsed = _hasSnapshot && _session.PlaybackState == MediaPlaybackState.Playing
            ? Math.Max(0, _time.GetElapsedTime(anchor).TotalSeconds) * rate : 0;
        var minimum = Math.Max(0, _session.Timeline.Start.TotalSeconds);
        var maximum = _session.Timeline.End > _session.Timeline.Start ? _session.Timeline.End.TotalSeconds : 86_400;
        return Math.Clamp(position + elapsed, minimum, Math.Max(minimum, maximum));
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
        var position = sameTrack ? SourcePosition.TotalSeconds : Math.Max(0, session.Timeline.Position.TotalSeconds);
        var observedSeek = sameTrack && native && _acceptedSeek is { } seek && IsNewSeekObservation(previousReport, timeline, seek);
        if (!sameTrack || !session.IsActive || !session.CanSeek || observedSeek) ClearAcceptedSeek();
        else if (_acceptedSeek is { } awaiting && (_session.PlaybackState != session.PlaybackState ||
            _session.Timeline.PlaybackRate != timeline.PlaybackRate))
        {
            var resumed = _session.PlaybackState == MediaPlaybackState.Paused && session.PlaybackState == MediaPlaybackState.Playing;
            var tick = _time.GetTimestamp();
            _acceptedSeek = awaiting with { AnchorPosition = ReadPosition(awaiting.AnchorPosition, awaiting.Anchor), Anchor = tick,
                AcceptedAt = resumed ? tick : awaiting.AcceptedAt };
        }
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
        // Apple exposes the same whole second in SMTC and in its own scrubber. It
        // can keep reporting that second while buffering even with state Playing.
        // Allow the word clock to advance inside it until Apple publishes the next one.
        var apple = MediaProcessIdentityPolicy.IsAppleMusic(session.SourceAppUserModelId) && timeline.PlaybackRate == 1;
        var appleSeconds = apple && native && timeline.Position.Ticks % TimeSpan.TicksPerSecond == 0;
        // On a track change Apple can report Playing, zero position and zero length
        // before its scrubber starts. A new track has no earlier position to advance.
        var awaitingFirstApplePosition = apple && !native && previousNative is null &&
            timeline.Position == TimeSpan.Zero && timeline.Duration == TimeSpan.Zero;
        // Once the publisher goes silent, resume the existing missing-timeline estimate
        // from the last displayed second without skipping the observation window.
        if ((awaitingFirstApplePosition && (!sameTrack || _session.PlaybackState != session.PlaybackState ||
            validTimelineTimestamp && previousReport?.LastUpdated != timeline.LastUpdated)) ||
            (appleSeconds && (previousNative?.Position != timeline.Position ||
            previousNative.LastUpdated != timeline.LastUpdated || _session.PlaybackState != session.PlaybackState)))
            _lastAppleObservation = _time.GetTimestamp();
        _nativeSecondCeiling = appleSeconds ? timeline.Position.TotalSeconds + 1 - 1.0 / TimeSpan.TicksPerSecond :
            awaitingFirstApplePosition ? 0 : double.PositiveInfinity;
        if (native && (nativeChanged || observedSeek))
        {
            var interpolated = position;
            // A state-only pause/resume event may carry the unchanged native
            // observation. Reusing that old position rewinds the lyric clock.
            var staleStateTransition = previousNative is not null &&
                session.PlaybackState != _session.PlaybackState &&
                timeline.Position == previousNative.Position && timeline.LastUpdated == previousNative.LastUpdated;
            var nativeSeconds = Math.Max(0, session.Timeline.Position.TotalSeconds);
            position = staleStateTransition ? interpolated : nativeSeconds;
            // A pause keeps the subsecond progress already drawn inside this second.
            if (appleSeconds && !observedSeek && previousNative is not null &&
                session.PlaybackState == MediaPlaybackState.Paused && timeline.Position == previousNative.Position)
                position = Math.Max(position, interpolated);
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
                    if (appleSeconds && timeline.Position == previousNative?.Position) position = interpolated;
                }
                position += Math.Clamp((now - observation).TotalSeconds, 0, 86_400) * rate;
                if (!appleSeconds && !observedSeek && previousNative is not null && _session.PlaybackState == MediaPlaybackState.Playing &&
                    timeline.Position.Ticks % TimeSpan.TicksPerSecond == 0 &&
                    (timeline.Position - previousNative.Position).TotalSeconds is >= 0 and <= 1 &&
                    Math.Abs(position - interpolated) < 1)
                    position = Math.Max(interpolated, position);
            }
        }
        _session = session;
        _position = Math.Min(_nativeSecondCeiling + MissingAppleReportSeconds, position);
        _anchor = _time.GetTimestamp();
        _hasSnapshot = true;
        _lastObservedPosition = _position; _lastObservation = _anchor;
        _hasNativePosition = native;
        _lastNativeTimeline = native ? timeline : previousNative;
    }
}
