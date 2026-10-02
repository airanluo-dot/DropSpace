using DropSpace.Core.Media;

namespace DropSpace.Core.Lyrics;

/// <summary>A one-use, same-monitor and same-track visual continuation during a surface rebuild.</summary>
public sealed class LyricsGlowHandoff(string monitorId, MediaSessionSnapshot source, LyricsGlowVisualState state)
{
    private bool _consumed;

    public LyricsGlowVisualState? Evaluate(string currentMonitorId, MediaSessionSnapshot current,
        bool allowed, bool layoutReady, bool eligible, TimeSpan age)
    {
        if (_consumed) return null;
        if (!allowed || current.PlaybackState != MediaPlaybackState.Playing ||
            !string.Equals(monitorId, currentMonitorId, StringComparison.Ordinal) || !source.IsSameTrack(current) ||
            age < TimeSpan.Zero || age > TimeSpan.FromSeconds(2))
        {
            _consumed = true;
            return null;
        }
        if (!layoutReady) return null;
        _consumed = true;
        return eligible ? state : null;
    }
}
