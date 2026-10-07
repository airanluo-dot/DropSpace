namespace DropSpace.App.Views.Island;

/// <summary>Coalesces notification bursts into one frame and retains hidden-view invalidation.</summary>
internal sealed class MediaRenderQueue
{
    private bool _dirty;
    private bool _queued;
    private long _generation;

    public bool Request(bool visible) => Request(visible, out _);

    public bool Request(bool visible, out long generation)
    {
        _dirty = true;
        generation = _generation;
        if (!visible || _queued) return false;
        _queued = true;
        generation = ++_generation;
        return true;
    }

    public bool BeginRender(bool visible) => BeginRender(visible, _generation);

    public bool BeginRender(bool visible, long generation)
    {
        if (generation != _generation) return false;
        var queued = _queued;
        _queued = false;
        if (!queued || !visible || !_dirty) return false;
        _dirty = false;
        return true;
    }

    /// <summary>Consumes current invalidation before a host reads its first-frame geometry.</summary>
    public bool BeginImmediateRender()
    {
        Cancel();
        if (!_dirty) return false;
        _dirty = false;
        return true;
    }

    public void Cancel()
    {
        _queued = false;
        _generation++;
    }
}
