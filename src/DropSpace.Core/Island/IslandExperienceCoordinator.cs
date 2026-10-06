using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using DropSpace.Core.SystemActivities;

namespace DropSpace.Core.Island;

/// <summary>Dispatcher-owned visibility authority. The overlay service schedules NextDeadline,
/// independently of media observation. Repeated hidden targets retain the original deadline.</summary>
public sealed class IslandExperienceCoordinator(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private OverlaySnapshot _files = new(OverlayState.Hidden, 0, false, 0);
    private bool _mediaAvailable, _mediaPlaying, _manual, _expanded, _resident, _allows = true, _dismissed, _hideCompleted;
    private string? _mediaIdentity;
    private IslandPage _page = IslandPage.Files;
    private DateTimeOffset? _notification, _volume, _hideStarted;
    private int _delay = 3000;
    private long _revision, _generation;
    public event EventHandler<IslandExperienceSnapshot>? Changed;
    public IslandExperienceSnapshot Current { get; private set; } = new(OverlayState.Hidden, IslandContentKind.None, IslandPage.Files, false, null, 0);
    public long HideGeneration => _generation;
    public bool IsManuallyOpen => _manual && _expanded;

    public void UpdateFiles(OverlaySnapshot files)
    {
        if (files.TemporaryItemCount > _files.TemporaryItemCount ||
            files.Transition?.Cause is OverlayTransitionCause.DropCompleted or OverlayTransitionCause.VisibleDropCompleted or OverlayTransitionCause.DragApproach)
            _dismissed = false;
        _files = files;
        if (files.Transition?.Cause == OverlayTransitionCause.Restore && !_manual) { _expanded = files.State == OverlayState.Expanded; }
        else if (files.Transition?.Cause == OverlayTransitionCause.QuickPanelOpened) { _dismissed = false; _manual = true; _expanded = true; }
        else if (files.Transition?.Cause == OverlayTransitionCause.Expanded) { _expanded = true; _page = IslandPage.Files; }
        else if (!_manual && (files.Transition?.Cause is OverlayTransitionCause.Collapsed or OverlayTransitionCause.Dismissed))
        { _expanded = false; _manual = false; }
        Reconcile();
    }
    public void UpdateSettings(IslandAppearanceSettings settings, bool fullscreenAllows)
    {
        _resident = settings.Resident; _delay = NativeIslandSettingsPolicy.NormalizeHideDelay(settings.HideDelayMilliseconds);
        _allows = fullscreenAllows; Reconcile();
    }
    public void UpdateMedia(bool present, bool enabled, int hideDelayMilliseconds, bool autoHide = true, string? contentIdentity = null)
    {
        // Compatibility for callers whose presence already means playing.
        UpdateMedia(present, present, enabled, hideDelayMilliseconds, contentIdentity);
    }
    public void UpdateMedia(bool contentAvailable, bool playing, bool enabled, int hideDelayMilliseconds, string? contentIdentity = null)
    {
        var available = enabled && contentAvailable;
        var active = available && playing;
        if (available && contentIdentity != _mediaIdentity || active && !_mediaPlaying) _dismissed = false;
        if (available) _mediaIdentity = contentIdentity;
        _mediaAvailable = available;
        _mediaPlaying = active;
        _delay = NativeIslandSettingsPolicy.NormalizeHideDelay(hideDelayMilliseconds);
        Reconcile();
    }
    public void Open(IslandPage? page = null)
    {
        _generation++; _hideStarted = null;
        _page = page ?? (_mediaAvailable ? IslandPage.Music : IslandPage.Files);
        _dismissed = false; _manual = true; _expanded = true; Reconcile();
    }
    public void SelectPage(IslandPage page)
    { if (!Enum.IsDefined(page)) throw new ArgumentOutOfRangeException(nameof(page)); _page = page; Reconcile(); }
    public void Collapse() { _manual = false; _expanded = false; Reconcile(); }
    public void DismissNow()
    { _dismissed = true; _manual = false; _expanded = false; _hideStarted = null; _hideCompleted = false; _generation++; Reconcile(); }
    public bool CompleteDismissal(long generation)
    {
        if (generation != _generation || Current.State != OverlayState.Dismissing) return false;
        _hideCompleted = true;
        Reconcile();
        return true;
    }
    public bool CanCompleteFileDismissal(long revision) =>
        _files.Revision == revision && _files.State == OverlayState.Dismissing &&
        _files.TemporaryItemCount == 0 && !_files.ExpandedDropActive && !IsManuallyOpen;
    public void Notify() { _dismissed = false; _notification = _time.GetUtcNow() + SystemActivityPolicy.NotificationLifetime; Reconcile(); }
    public void VolumeChanged() { _dismissed = false; _volume = _time.GetUtcNow() + SystemActivityPolicy.VolumeLifetime; Reconcile(); }
    public void ClearNotifications() { _notification = null; Reconcile(); }
    public void ClearVolume() { _volume = null; Reconcile(); }
    public void OnDeadline(long generation, DateTimeOffset deadline)
    { if (generation == _generation && Current.NextDeadline == deadline) Reconcile(); }
    public void Reconcile()
    {
        var now = _time.GetUtcNow();
        // Content remains available while paused. Only playing is a persistent
        // visibility reason; content selection must not double as a hide clock.
        var baseState = IslandPresencePolicy.Resolve(new(_files, _mediaPlaying, null, _manual, _expanded, _page,
            _notification, _volume, RetainMedia: _mediaAvailable), now, _revision);
        var hasReason = _resident || _mediaPlaying || IsManuallyOpen || _files.TemporaryItemCount > 0 ||
            _files.State is OverlayState.DragApproaching or OverlayState.DragReady || _files.ExpandedDropActive ||
            _notification > now || _volume > now;
        var desired = hasReason && (_allows || IsManuallyOpen) && !_dismissed;
        var state = baseState.State;
        var content = baseState.CompactContent;
        var variant = content switch
        {
            IslandContentKind.Music => IslandPresentationVariant.Music,
            IslandContentKind.Notification or IslandContentKind.Volume => IslandPresentationVariant.Activity,
            IslandContentKind.Files when _files.TemporaryItemCount == 1 => IslandPresentationVariant.SingleFile,
            IslandContentKind.Files when _files.TemporaryItemCount > 1 => IslandPresentationVariant.MultipleFiles,
            IslandContentKind.Files => IslandPresentationVariant.EmptyWake,
            _ => IslandPresentationVariant.Idle,
        };
        if (desired)
        {
            if (_hideStarted is not null) { _hideStarted = null; _generation++; }
            _hideCompleted = false;
            if (state == OverlayState.Hidden) state = OverlayState.Compact;
        }
        else
        {
            if (_hideCompleted) state = OverlayState.Hidden;
            else if (_dismissed)
                state = Current.State == OverlayState.Hidden ? OverlayState.Hidden : OverlayState.Dismissing;
            else if (_hideStarted is not null || Current.State is not (OverlayState.Hidden or OverlayState.Dismissing))
            {
                if (_hideStarted is null) { _hideStarted = now; _generation++; }
                state = Current.State == OverlayState.Dismissing || now >= _hideStarted.Value.AddMilliseconds(_delay)
                    ? OverlayState.Dismissing : OverlayState.Compact;
            }
            else state = OverlayState.Hidden;
            if (state == OverlayState.Hidden) { content = IslandContentKind.None; variant = IslandPresentationVariant.Idle; }
        }
        var hideDeadline = !desired && !_dismissed && state != OverlayState.Dismissing && !_hideCompleted &&
            _hideStarted is { } start && now < start.AddMilliseconds(_delay)
            ? start.AddMilliseconds(_delay) : (DateTimeOffset?)null;
        var next = new[] { hideDeadline, baseState.NextDeadline }.Where(value => value > now).Min();
        var snapshot = new IslandExperienceSnapshot(state, content, baseState.Page, state != OverlayState.Hidden && baseState.MediaPresent, next, _revision,
            variant, hideDeadline is not null);
        if (snapshot == Current) return;
        Current = snapshot with { Revision = ++_revision };
        Changed?.Invoke(this, Current);
    }
}
