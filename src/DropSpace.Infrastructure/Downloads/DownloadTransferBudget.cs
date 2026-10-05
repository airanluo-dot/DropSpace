namespace DropSpace.Infrastructure.Downloads;

/// <summary>Shared task admission, independent of the HTTP connection budget.</summary>
public sealed class DownloadTransferBudget
{
    // Reuse the cancellable lease mechanism with a separate counter for whole transfers.
    private readonly DownloadConnectionBudget _slots = new(2);
    public int Limit => _slots.Limit;
    public int Active => _slots.Active;

    public void SetLimit(int limit)
    {
        if (limit is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(limit));
        // Reducing the ceiling lets existing transfers settle; only new admission waits.
        _slots.SetLimit(limit);
    }

    public ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken) =>
        _slots.AcquireAsync(cancellationToken);
}
