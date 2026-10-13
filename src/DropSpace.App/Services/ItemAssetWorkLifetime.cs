namespace DropSpace.App.Services;

/// <summary>Owns one card's presentation wait, independently of native service ownership.</summary>
internal sealed class ItemAssetWorkLifetime : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;

    internal ItemAssetWorkLifetime(CancellationToken lifetime, CancellationToken caller = default)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller);
        Token = _cancellation.Token;
    }

    internal CancellationToken Token { get; }

    internal void Cancel()
    {
        // A finishing task can dispose its owner while the UI removes its card.
        // Serialize those two operations without holding the projection's lock.
        lock (_gate) _cancellation?.Cancel();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
