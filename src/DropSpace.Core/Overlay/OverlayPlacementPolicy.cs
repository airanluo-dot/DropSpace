using DropSpace.Core.Models;

namespace DropSpace.Core.Overlay;

/// <summary>
/// Defines one DPI-aware visual anchor for every overlay state. The default leaves
/// a small gap within the monitor work area; no system share-tray offset is reserved.
/// </summary>
public static class OverlayPlacementPolicy
{
    public const double HostWidthDips = 600;
    public const double MaximumSurfaceWidthDips = 560;
    public const double DynamicIslandTopGapDips = 18;
    public const double MaximumSurfaceHeightDips = 340;
    public const double HostBottomMarginDips = 16;
    public const double MinimumHostHeightDips =
        DynamicIslandTopGapDips +
        MaximumSurfaceHeightDips + HostBottomMarginDips;

    public static double GetMinimumHostHeightDips(double monitorScale, double contentScale = 1)
    {
        if (!double.IsFinite(monitorScale) || monitorScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorScale));
        }

        return DynamicIslandTopGapDips +
               MaximumSurfaceHeightDips * NormalizeContentScale(contentScale) +
               HostBottomMarginDips;
    }

    public static double GetTopOffsetDips(
        FileDragWakeMode wakeMode,
        double monitorScale)
    {
        if (!double.IsFinite(monitorScale) || monitorScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorScale));
        }

        return DynamicIslandTopGapDips;
    }

    /// <summary>
    /// Position a still-invisible body at its resolved anchor before animating size
    /// and opacity. The generic Hidden pose has no monitor or custom-position context.
    /// A visible reversal retains its current pose and spring continuity.
    /// </summary>
    public static OverlayMotionValues AnchorInvisibleSurface(
        OverlayMotionValues current,
        OverlayResolvedPlacement placement) =>
        current.Opacity <= 0.001
            ? current with { TopOffset = placement.SurfaceTopOffsetDips }
            : current;

    public static double FitContentScale(int workWidthPixels, int workHeightPixels,
        double monitorScale, double widthDips, double heightDips, double requestedScale)
    {
        if (workWidthPixels <= 0 || workHeightPixels <= 0 || !double.IsFinite(monitorScale) || monitorScale <= 0 ||
            !double.IsFinite(widthDips) || widthDips <= 0 || !double.IsFinite(heightDips) || heightDips <= 0)
            throw new ArgumentOutOfRangeException(nameof(workWidthPixels));
        var requested = double.IsFinite(requestedScale) ? Math.Clamp(requestedScale, 0.01, 2) : 1;
        var availableWidth = Math.Max(1, workWidthPixels / monitorScale - DynamicIslandTopGapDips * 2);
        var availableHeight = Math.Max(1, workHeightPixels / monitorScale - DynamicIslandTopGapDips - HostBottomMarginDips);
        return Math.Min(requested, Math.Min(availableWidth / widthDips, availableHeight / heightDips));
    }

    public static OverlayResolvedPlacement Resolve(
        OverlayPlacementRequest request,
        OverlayMonitorPlacement placement)
    {
        if (!double.IsFinite(request.Scale) || request.Scale <= 0 ||
            request.WorkWidthPixels <= 0 || request.WorkHeightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        ArgumentNullException.ThrowIfNull(placement);
        var contentScale = NormalizeContentScale(request.ContentScale);
        var hostWidth = request.HostWidthDips > 0 ? request.HostWidthDips : HostWidthDips * contentScale;

        if (placement.Mode != OverlayPlacementMode.Custom)
        {
            var width = ToPixels(hostWidth, request.Scale);
            return new OverlayResolvedPlacement(
                request.WorkLeftPixels + (request.WorkWidthPixels - width) / 2,
                request.WorkTopPixels,
                GetTopOffsetDips(request.WakeMode, request.Scale),
                false);
        }

        var workWidthDips = request.WorkWidthPixels / request.Scale;
        var workHeightDips = request.WorkHeightPixels / request.Scale;
        var halfSurface = Math.Min(request.SurfaceWidthDips > 0 ? request.SurfaceWidthDips : MaximumSurfaceWidthDips * contentScale, workWidthDips) / 2;
        var clampedCenterX = Math.Clamp(placement.X, halfSurface, Math.Max(halfSurface, workWidthDips - halfSurface));
        var maxTop = Math.Max(0, workHeightDips - MaximumSurfaceHeightDips * contentScale);
        var clampedTop = Math.Clamp(placement.Y, 0, maxTop);
        var hostLeftDips = clampedCenterX - hostWidth / 2;
        return new OverlayResolvedPlacement(
            request.WorkLeftPixels + (int)Math.Round(hostLeftDips * request.Scale),
            request.WorkTopPixels + (int)Math.Round(clampedTop * request.Scale),
            0,
            clampedCenterX != placement.X || clampedTop != placement.Y);
    }

    public static OverlayResolvedPlacement Resolve(
        OverlayPlacementRequest request,
        OverlayPlacementMode mode,
        OverlayCustomPlacement? custom) =>
        Resolve(
            request,
            new OverlayMonitorPlacement(
                mode,
                custom?.X ?? 0,
                custom?.Y ?? 0));

    public static OverlayCustomPlacement ProjectResolvedPlacement(
        OverlayResolvedPlacement resolved,
        int workLeftPixels,
        int workTopPixels,
        double scale,
        double contentScale = 1, double hostWidthDips = 0)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        return new OverlayCustomPlacement(
            (resolved.HostLeftPixels - workLeftPixels) / scale + (hostWidthDips > 0 ? hostWidthDips : HostWidthDips * NormalizeContentScale(contentScale)) / 2,
            (resolved.HostTopPixels - workTopPixels) / scale + resolved.SurfaceTopOffsetDips);
    }

    private static int ToPixels(double dips, double scale) =>
        Math.Max(0, (int)Math.Round(dips * scale));

    private static double NormalizeContentScale(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, 1, 2) : 1;
}

public readonly record struct OverlayPlacementRequest(
    int WorkLeftPixels,
    int WorkTopPixels,
    int WorkWidthPixels,
    int WorkHeightPixels,
    double Scale,
    FileDragWakeMode WakeMode,
    double ContentScale = 1, double HostWidthDips = 0, double SurfaceWidthDips = 0);

public readonly record struct OverlayResolvedPlacement(
    int HostLeftPixels,
    int HostTopPixels,
    double SurfaceTopOffsetDips,
    bool WasClamped);
