namespace DropSpace.Core.Policies;

public sealed record ConsecutiveClipboardCaptureResult<T>(bool Suppressed, T Value);

/// <summary>
/// Serializes clipboard commits and suppresses only an immediately repeated
/// snapshot that was already persisted successfully during this process.
/// </summary>
public sealed class ConsecutiveClipboardCaptureCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastObservedFingerprint;
    private string? _lastPersistedFingerprint;
    private int _disposed;

    public async Task<ConsecutiveClipboardCaptureResult<T>> ExecuteAsync<T>(
        string fingerprint,
        Func<CancellationToken, Task<T>> commit,
        Func<T, bool> wasPersisted,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(wasPersisted);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var suppress = string.Equals(fingerprint, _lastObservedFingerprint, StringComparison.Ordinal) &&
                string.Equals(fingerprint, _lastPersistedFingerprint, StringComparison.Ordinal);
            _lastObservedFingerprint = fingerprint;
            if (suppress)
            {
                return new ConsecutiveClipboardCaptureResult<T>(true, default!);
            }

            var value = await commit(cancellationToken).ConfigureAwait(false);
            if (wasPersisted(value))
            {
                _lastPersistedFingerprint = fingerprint;
            }

            return new ConsecutiveClipboardCaptureResult<T>(false, value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastObservedFingerprint = null;
            _lastPersistedFingerprint = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }
    }
}
