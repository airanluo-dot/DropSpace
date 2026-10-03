using DropSpace.Core.Models;

namespace DropSpace.Core.Overlay;

public readonly record struct FullscreenOverlayPresentation(
    OverlayState State,
    bool Suppress,
    bool KeepTopmost,
    bool AllowActivation);

/// <summary>
/// An opt-in presentation override, never a mutation of the saved auto-hide,
/// activity/privacy settings or underlying file/media state. It applies only
/// after the caller has accepted this monitor and checked native window safety.
/// </summary>
public static class FullscreenOverlayPolicy
{
    public static FullscreenOverlayPresentation Resolve(
        OverlayState state,
        bool forceShowOverFullscreen,
        bool suppressOverFullscreen,
        bool isForegroundFullscreen)
    {
        var forced = forceShowOverFullscreen && isForegroundFullscreen;
        var dragging = state is OverlayState.DragApproaching or OverlayState.DragReady;
        var suppress = !forced && suppressOverFullscreen && isForegroundFullscreen && !dragging;
        // Even an idle island must have a visible compact surface in the opted-in
        // fullscreen mode. Do not unpause media, enable activities or retain this
        // projection after exiting fullscreen or turning the override off.
        var presentedState = forced && state is OverlayState.Hidden or OverlayState.Dismissing
            ? OverlayState.Compact
            : state;
        return new(presentedState, suppress, forced,
            presentedState == OverlayState.Expanded && !forced && !suppress);
    }

    public static string? ResolveMonitorId(
        string? normalMonitorId,
        OverlayMonitorPreference preference,
        bool forceShowOverFullscreen,
        string? fullscreenMonitorId,
        bool isDragging) =>
        forceShowOverFullscreen && preference == OverlayMonitorPreference.Automatic &&
        !isDragging && fullscreenMonitorId is not null
            ? fullscreenMonitorId
            : normalMonitorId;
}
