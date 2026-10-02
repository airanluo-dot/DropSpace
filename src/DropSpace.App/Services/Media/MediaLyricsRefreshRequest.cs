namespace DropSpace.App.Services.Media;

/// <summary>Retains an explicit source refresh across a full worker budget or cache maintenance.</summary>
internal sealed class MediaLyricsRefreshRequest
{
    private long _requested, _started;
    public void Request() => Interlocked.Increment(ref _requested);
    public long Capture() => Interlocked.Read(ref _requested);
    public bool IsPending(long request) => request > Interlocked.Read(ref _started);
    public void MarkStarted(long request)
    {
        long started;
        do
        {
            started = Interlocked.Read(ref _started);
            if (request <= started) return;
        } while (Interlocked.CompareExchange(ref _started, request, started) != started);
    }
}
