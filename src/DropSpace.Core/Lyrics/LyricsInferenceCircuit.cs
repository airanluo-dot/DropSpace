namespace DropSpace.Core.Lyrics;

/// <summary>Session-local protection; only an explicit user recovery reopens a paused circuit.</summary>
public sealed class LyricsInferenceCircuit
{
    private readonly object _gate = new();
    private int _failures;
    private long _generation;
    public bool IsPaused { get { lock (_gate) return _failures >= 3; } }

    public bool TryBegin(out long generation)
    {
        lock (_gate) { generation = _generation; return _failures < 3; }
    }

    public void Complete(long generation, bool success)
    {
        lock (_gate)
        {
            if (generation != _generation || _failures >= 3) return;
            _failures = success ? 0 : _failures + 1;
        }
    }

    public void Resume()
    {
        lock (_gate) { ++_generation; _failures = 0; }
    }
}
