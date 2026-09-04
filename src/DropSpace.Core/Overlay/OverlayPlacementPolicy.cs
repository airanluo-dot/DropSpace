using DropSpace.Core.Models;

namespace DropSpace.Core.Overlay;

/// <summary>
/// Defines the single visual anchor used by every state in one Overlay lifecycle. Smart drag
/// mode is intentionally displaced below the Windows 11 Drop Tray area; that displacement must
/// not disappear when DragReady transitions to Compact, Expanded, or Dismissing.
/// </summary>
public static class OverlayPlacementPolicy
{
    public const double HostWidthDips = 600;
    public const double MaximumSurfaceWidthDips = 560;
    public const double SmartTopOffsetPhysicalPixels = 76;
    public const double DynamicIslandTopGapDips = 8;
    public const double MaximumSurfaceHeightDips = 340;
    public const double HostBottomMarginDips = 16;
    public const double MinimumHostHeightDips =
        SmartTopOffsetPhysicalPixels + DynamicIslandTopGapDips +
        MaximumSurfaceHeightDips + HostBottomMarginDips;

    public static double GetMinimumHostHeightDips(double monitorScale)
    {
        if (!double.IsFinite(monitorScale) || monitorScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorScale));
        }

        // The Dynamic Island compatibility offset is a physical-pixel requirement, while the
        // remaining host dimensions are DIPs. Convert only that fixed physical segment back to
        // DIPs so the native client surface remains large enough at every display scale.
        return SmartTopOffsetPhysicalPixels / monitorScale +
               DynamicIslandTopGapDips +
               MaximumSurfaceHeightDips +
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

        var compatibilityOffset = wakeMode == FileDragWakeMode.SmartExperimental
            ? SmartTopOffsetPhysicalPixels / monitorScale
            : 0;
        return DynamicIslandTopGapDips + compatibilityOffset;
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

        if (placement.Mode != OverlayPlacementMode.Custom)
        {
            var width = ToPixels(HostWidthDips, request.Scale);
            return new OverlayResolvedPlacement(
                request.WorkLeftPixels + (request.WorkWidthPixels - width) / 2,
                request.WorkTopPixels,
                GetTopOffsetDips(request.WakeMode, request.Scale),
                false);
        }

        var workWidthDips = request.WorkWidthPixels / request.Scale;
        var workHeightDips = request.WorkHeightPixels / request.Scale;
        var halfSurface = Math.Min(MaximumSurfaceWidthDips, workWidthDips) / 2;
        var clampedCenterX = Math.Clamp(placement.X, halfSurface, Math.Max(halfSurface, workWidthDips - halfSurface));
        var maxTop = Math.Max(0, workHeightDips - MaximumSurfaceHeightDips);
        var clampedTop = Math.Clamp(placement.Y, 0, maxTop);
        var hostLeftDips = clampedCenterX - HostWidthDips / 2;
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
        double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        return new OverlayCustomPlacement(
            (resolved.HostLeftPixels - workLeftPixels) / scale + HostWidthDips / 2,
            (resolved.HostTopPixels - workTopPixels) / scale + resolved.SurfaceTopOffsetDips);
    }

    private static int ToPixels(double dips, double scale) =>
        Math.Max(0, (int)Math.Round(dips * scale));
}

public readonly record struct OverlayPlacementRequest(
    int WorkLeftPixels,
    int WorkTopPixels,
    int WorkWidthPixels,
    int WorkHeightPixels,
    double Scale,
    FileDragWakeMode WakeMode);

public readonly record struct OverlayResolvedPlacement(
    int HostLeftPixels,
    int HostTopPixels,
    double SurfaceTopOffsetDips,
    bool WasClamped);
