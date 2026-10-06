using DropSpace.Core.Models;

namespace DropSpace.Core.Overlay;

public sealed record ForegroundPresentationSnapshot(nint Window, uint ProcessId, long ProcessStarted, string? MonitorId, bool IsFullscreen);
public readonly record struct FullscreenOverlayPresentation(OverlayState State, bool Suppress, bool KeepTopmost, bool AllowActivation);

public static class FullscreenOverlayPolicy
{
    public static bool Allows(bool force, bool fullscreen) => !fullscreen || force;
    public static FullscreenOverlayPresentation Resolve(OverlayState state, bool force, bool fullscreen)
    {
        var allow = Allows(force, fullscreen);
        return new(state, !allow, fullscreen && allow, state == OverlayState.Expanded && !fullscreen);
    }
    // Source compatibility for diagnostics; no shipping path reads legacy settings.
    public static FullscreenOverlayPresentation Resolve(OverlayState state, bool force, bool suppress, bool fullscreen) =>
        Resolve(state, force || !suppress, fullscreen);
    public static string? ResolveMonitorId(string? normalMonitorId, OverlayMonitorPreference preference,
        bool permittedWithDisplayReason, string? fullscreenMonitorId, bool isDragging) =>
        permittedWithDisplayReason && preference == OverlayMonitorPreference.Automatic && !isDragging && fullscreenMonitorId is not null
            ? fullscreenMonitorId : normalMonitorId;
}
