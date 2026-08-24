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
        OverlayPlacementMode mode,
        OverlayCustomPlacement? custom)
    {
        if (!double.IsFinite(request.Scale) || request.Scale <= 0 ||
            request.WorkWidthPixels <= 0 || request.WorkHeightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (mode != OverlayPlacementMode.Custom || custom is null)
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
        var clampedCenterX = Math.Clamp(custom.X, halfSurface, Math.Max(halfSurface, workWidthDips - halfSurface));
        var maxTop = Math.Max(0, workHeightDips - MaximumSurfaceHeightDips);
        var clampedTop = Math.Clamp(custom.Y, 0, maxTop);
        var hostLeftDips = clampedCenterX - HostWidthDips / 2;
        return new OverlayResolvedPlacement(
            request.WorkLeftPixels + (int)Math.Round(hostLeftDips * request.Scale),
            request.WorkTopPixels + (int)Math.Round(clampedTop * request.Scale),
            0,
            clampedCenterX != custom.X || clampedTop != custom.Y);
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
