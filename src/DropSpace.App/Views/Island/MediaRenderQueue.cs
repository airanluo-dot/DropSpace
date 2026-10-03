namespace DropSpace.App.Views.Island;

/// <summary>Coalesces notification bursts into one frame and retains hidden-view invalidation.</summary>
internal sealed class MediaRenderQueue
{
    private bool _dirty;
    private bool _queued;

    public bool Request(bool visible)
    {
        _dirty = true;
        if (!visible || _queued) return false;
        _queued = true;
        return true;
    }

    public bool BeginRender(bool visible)
    {
        var queued = _queued;
        _queued = false;
        if (!queued || !visible || !_dirty) return false;
        _dirty = false;
        return true;
    }

    public void Cancel() => _queued = false;
}
