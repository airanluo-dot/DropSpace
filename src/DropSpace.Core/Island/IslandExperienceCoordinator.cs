using DropSpace.Core.Overlay;
using DropSpace.Core.SystemActivities;

namespace DropSpace.Core.Island;

/// <summary>Presentation policy above the unchanged file state machine. Call from the owning dispatcher.</summary>
public sealed class IslandExperienceCoordinator(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private OverlaySnapshot _files = new(OverlayState.Hidden, 0, false, 0);
    private bool _playing, _manual, _expanded;
    private IslandPage _page;
    private DateTimeOffset? _grace, _notification, _volume;
    private long _revision;
    public event EventHandler<IslandExperienceSnapshot>? Changed;
    public IslandExperienceSnapshot Current { get; private set; } = new(OverlayState.Hidden, IslandContentKind.None, IslandPage.Files, false, null, 0);

    public void UpdateFiles(OverlaySnapshot files)
    {
        _files = files;
        if (files.Transition?.Cause == OverlayTransitionCause.Restore) { _manual = false; _expanded = files.State == OverlayState.Expanded; }
        else if (files.Transition?.Cause == OverlayTransitionCause.QuickPanelOpened) { _manual = true; _expanded = true; }
        else if (files.Transition?.Cause == OverlayTransitionCause.Expanded) { _expanded = true; _page = IslandPage.Files; }
        else if (files.Transition?.Cause is OverlayTransitionCause.Collapsed or OverlayTransitionCause.Dismissed)
        { _expanded = false; _manual = false; }
        Reconcile();
    }

    public void UpdateMedia(bool playing, bool enabled, int hideDelayMilliseconds)
    {
        var now = _time.GetUtcNow();
        if (!enabled) _grace = null;
        else if (_playing && !playing) _grace = now.AddMilliseconds(Math.Clamp(hideDelayMilliseconds, 500, 30_000));
        if (playing) _grace = null;
        _playing = enabled && playing;
        Reconcile();
    }

    public void Open(IslandPage? page = null)
    {
        _page = page ?? (Current.MediaPresent ? IslandPage.Music : IslandPage.Files);
        _manual = true; _expanded = true; Reconcile();
    }
    public void SelectPage(IslandPage page)
    {
        if (!Enum.IsDefined(page)) throw new ArgumentOutOfRangeException(nameof(page));
        _page = page; Reconcile();
    }
    public void Collapse() { _manual = false; _expanded = false; Reconcile(); }
    public void Notify() { _notification = _time.GetUtcNow() + SystemActivityPolicy.NotificationLifetime; Reconcile(); }
    public void VolumeChanged() { _volume = _time.GetUtcNow() + SystemActivityPolicy.VolumeLifetime; Reconcile(); }
    public void ClearNotifications() { _notification = null; Reconcile(); }
    public void ClearVolume() { _volume = null; Reconcile(); }
    public void Reconcile()
    {
        var snapshot = IslandPresencePolicy.Resolve(new(_files, _playing, _grace, _manual, _expanded,
            _page, _notification, _volume), _time.GetUtcNow(), _revision);
        if (snapshot == Current) return;
        Current = snapshot with { Revision = ++_revision };
        Changed?.Invoke(this, Current);
    }
}
