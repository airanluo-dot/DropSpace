namespace DropSpace.Core.Lyrics;

/// <summary>Independent full-text marquee in DIPs, with a readable hold at both ends.</summary>
public static class LyricsMarqueePolicy
{
    public static double Offset(double textWidth, double viewportWidth, double elapsedSeconds, bool enabled, bool reducedMotion)
    {
        if (!enabled || reducedMotion || !double.IsFinite(textWidth) || !double.IsFinite(viewportWidth) ||
            !double.IsFinite(elapsedSeconds) || viewportWidth <= 0 || textWidth <= viewportWidth) return 0;
        var overflow = textWidth - viewportWidth;
        const double speed = 24, hold = 1.2;
        var cycle = overflow / speed + 2 * hold;
        var time = Math.Max(0, elapsedSeconds) % cycle;
        return Math.Clamp((time - hold) * speed, 0, overflow);
    }
}

/// <summary>The rendered line's identity and DIP measurements, independent of the other lyric row.</summary>
public readonly record struct LyricsMarqueeLayout(string TrackIdentity, long LineStartTicks, string Text,
    double FontSize, double RasterizationScale, double TextWidth, double ViewportWidth, bool Enabled, bool ReducedMotion);

/// <summary>Restart the readable leading hold when content or layout changes; a stopped clock freezes motion.</summary>
public sealed class LyricsMarqueeSession
{
    private LyricsMarqueeLayout? _layout;
    private TimeSpan _started;
    private TimeSpan _lastPosition;

    public double Update(LyricsMarqueeLayout layout, TimeSpan position)
    {
        if (_layout != layout || position < _lastPosition)
        {
            _layout = layout;
            _started = position;
        }
        _lastPosition = position;
        return LyricsMarqueePolicy.Offset(layout.TextWidth, layout.ViewportWidth,
            (position - _started).TotalSeconds, layout.Enabled, layout.ReducedMotion);
    }

    public void Reset() => _layout = null;
}
