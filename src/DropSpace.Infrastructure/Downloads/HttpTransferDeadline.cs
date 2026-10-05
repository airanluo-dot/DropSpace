// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
namespace DropSpace.Infrastructure.Downloads;

internal sealed class HttpTransferDeadline
{
    public TimeSpan Timeout { get; }
    public HttpTransferDeadline(TimeSpan? timeout = null)
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try { return await operation(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The download server stopped responding.", exception); }
    }
}
