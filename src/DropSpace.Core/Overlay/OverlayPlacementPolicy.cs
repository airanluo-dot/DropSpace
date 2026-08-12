using DropSpace.Core.Models;

namespace DropSpace.Core.Overlay;

/// <summary>
/// Defines the single visual anchor used by every state in one Overlay lifecycle. Smart drag
/// mode is intentionally displaced below the Windows 11 Drop Tray area; that displacement must
/// not disappear when DragReady transitions to Compact, Expanded, or Dismissing.
/// </summary>
public static class OverlayPlacementPolicy
{
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
}
