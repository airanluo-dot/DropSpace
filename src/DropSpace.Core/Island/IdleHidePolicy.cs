using DropSpace.Core.Media;

namespace DropSpace.Core.Island;

public sealed class IdleHidePolicy
{
    public const int DefaultDelayMilliseconds = 3_000;
    public const int MinimumDelayMilliseconds = 500;
    public const int MaximumDelayMilliseconds = 30_000;

    public IdleHidePolicy(bool enabled = true, int delayMilliseconds = DefaultDelayMilliseconds)
    {
        Enabled = enabled;
        DelayMilliseconds = NormalizeDelay(delayMilliseconds);
    }

    public bool Enabled { get; }

    public int DelayMilliseconds { get; }

    public bool ShouldHide(
        MediaPlaybackState playbackState,
        TimeSpan idleDuration,
        bool isExpanded,
        bool hasManualActivity = false)
    {
        if (!Enabled || isExpanded || hasManualActivity || playbackState == MediaPlaybackState.Playing)
        {
            return false;
        }

        return idleDuration >= TimeSpan.FromMilliseconds(DelayMilliseconds);
    }

    public static int NormalizeDelay(int delayMilliseconds) =>
        Math.Clamp(delayMilliseconds, MinimumDelayMilliseconds, MaximumDelayMilliseconds);
}
