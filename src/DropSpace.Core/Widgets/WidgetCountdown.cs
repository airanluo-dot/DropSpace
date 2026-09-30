namespace DropSpace.Core.Widgets;

/// <summary>Monotonic elapsed-time countdown; hiding a widget does not pause its time.</summary>
public sealed class WidgetCountdown(TimeSpan duration)
{
    private readonly System.Diagnostics.Stopwatch _elapsed = new();
    public bool IsRunning => _elapsed.IsRunning && Remaining > TimeSpan.Zero;
    public TimeSpan Remaining
    {
        get
        {
            var elapsed = _elapsed.Elapsed;
            return duration > elapsed ? duration - elapsed : TimeSpan.Zero;
        }
    }
    public void Toggle()
    {
        if (Remaining == TimeSpan.Zero) _elapsed.Restart();
        else if (_elapsed.IsRunning) _elapsed.Stop();
        else _elapsed.Start();
    }
    public void Reset() => _elapsed.Reset();
}
