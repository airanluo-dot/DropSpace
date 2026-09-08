using System.Collections.Concurrent;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Diagnostics;
using DropSpace.Core.Models;
using DropSpace.Core.Undo;
using DropSpace.Core.Preview;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed class UndoCoordinator(
    IItemRepository repository,
    IPayloadStore payloadStore,
    IPreviewCache previews,
    ILogger<UndoCoordinator> logger,
    IPayloadCleanupCoordinator? cleanupCoordinator = null) : IAsyncDisposable
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(8);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ActiveUndo? _active;
    private CancellationTokenSource? _expirationCancellation;
    private readonly ConcurrentDictionary<string, Task> _expirationTasks = new(StringComparer.Ordinal);
    private bool _disposed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public UndoState? State { get; private set; }

    public event EventHandler? StateChanged;

    public async Task<UndoState?> BeginRemovalAsync(
        IReadOnlyCollection<Guid> ids,
        UndoOperationKind kind,
        string messageResourceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageResourceKey);
        if (ids.Count == 0)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await FinalizeActiveCoreAsync(cancellationToken).ConfigureAwait(false);
            var token = OperationCorrelation.New();
            var expiresAtUtc = DateTimeOffset.UtcNow.Add(UndoWindow);
            var markedCount = await repository.BeginPendingRemovalAsync(
                    ids,
                    token,
                    expiresAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (markedCount == 0)
            {
                return null;
            }

            var state = new UndoState(token, kind, expiresAtUtc, messageResourceKey, markedCount);
            logger.LogInformation("Delete operation {OperationId} entered its undo window for {ItemCount} item(s).", token, markedCount);
            _active = ActiveUndo.ForRemoval(state);
            PublishState(state);
            StartExpiration(_active);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UndoState?> RegisterPinChangeAsync(
        IReadOnlyDictionary<Guid, bool> previousStates,
        string messageResourceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previousStates);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageResourceKey);
        if (previousStates.Count == 0)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await FinalizeActiveCoreAsync(cancellationToken).ConfigureAwait(false);
            var state = new UndoState(
                OperationCorrelation.New(),
                UndoOperationKind.PinChange,
                DateTimeOffset.UtcNow.Add(UndoWindow),
                messageResourceKey,
                previousStates.Count);
            _active = ActiveUndo.ForPinChange(state, previousStates);
            PublishState(state);
            StartExpiration(_active);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UndoState?> BeginClipboardClearAsync(
        DateTimeOffset? fromUtc,
        bool includePinned,
        string messageResourceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageResourceKey);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await FinalizeActiveCoreAsync(cancellationToken).ConfigureAwait(false);
            var token = OperationCorrelation.New();
            var expiresAtUtc = DateTimeOffset.UtcNow.Add(UndoWindow);
            var markedCount = await repository.BeginPendingClipboardClearAsync(
                    fromUtc,
                    includePinned,
                    token,
                    expiresAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (markedCount == 0)
            {
                return null;
            }

            var state = new UndoState(token, UndoOperationKind.ClearClipboard, expiresAtUtc, messageResourceKey, markedCount);
            logger.LogInformation("Delete operation {OperationId} entered its clipboard-clear undo window for {ItemCount} item(s).", token, markedCount);
            _active = ActiveUndo.ForRemoval(state);
            PublishState(state);
            StartExpiration(_active);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UndoAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var active = _active;
            if (active is null)
            {
                return false;
            }

            CancelExpiration();
            if (active.Removal is not null)
            {
                await repository.UndoPendingRemovalAsync(active.State.Token, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Delete operation {OperationId} was undone.", active.State.Token);
            }
            else if (active.PreviousPinStates is not null)
            {
                await repository.RestorePinnedStatesAsync(active.PreviousPinStates, cancellationToken).ConfigureAwait(false);
            }

            _active = null;
            PublishState(null);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FinalizeActiveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await FinalizeActiveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecoverStaleAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var result = await repository.FinalizeExpiredPendingRemovalsAsync(
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cleanupCoordinator is not null)
            {
                await cleanupCoordinator.RecoverAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DeletePayloadsAsync(result.PayloadRelativePaths, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            try { await FinalizeActiveCoreAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception)
            {
                // Persisted pending-delete tokens remain recoverable on the next startup.
                logger.LogError(exception, "Undo finalization could not finish during shutdown.");
            }
            CancelExpiration();
            try { await Task.WhenAll(_expirationTasks.Values.ToArray()).ConfigureAwait(false); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Undo expiration task shutdown observed an unexpected failure.");
            }
            _expirationTasks.Clear();
        }
        finally
        {
            // Queued callers still need to acquire/release this managed semaphore to
            // observe the disposed state. It has no native wait handle to release.
            _gate.Release();
        }
    }

    private void StartExpiration(ActiveUndo active)
    {
        var cancellation = new CancellationTokenSource();
        _expirationCancellation = cancellation;
        var task = ExpireAsync(active, cancellation);
        _expirationTasks[active.State.Token] = task;
    }

    private async Task ExpireAsync(ActiveUndo active, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(UndoWindow, cancellation.Token).ConfigureAwait(false);
            await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_active, active))
                {
                    await FinalizeActiveCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A replacement, explicit undo, or shutdown cancelled the timer.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Undo expiration finalization failed; startup recovery will retry it.");
        }
        finally
        {
            cancellation.Dispose();
            _expirationTasks.TryRemove(active.State.Token, out _);
        }
    }

    private async Task FinalizeActiveCoreAsync(CancellationToken cancellationToken)
    {
        var active = _active;
        if (active is null)
        {
            return;
        }

        CancelExpiration();
        if (active.Removal is not null)
        {
            var result = await repository.FinalizePendingRemovalAsync(active.State.Token, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Delete operation {OperationId} committed {RemovedCount} item(s); payload cleanup is now retryable.", active.State.Token, result.RemovedCount);
            await DeletePayloadsAsync(result.PayloadRelativePaths, cancellationToken).ConfigureAwait(false);
        }

        _active = null;
        PublishState(null);
    }

    private async Task DeletePayloadsAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        if (cleanupCoordinator is not null)
        {
            logger.LogDebug("Delete operation payload cleanup is draining through the durable outbox.");
            await cleanupCoordinator.DrainAsync(cancellationToken).ConfigureAwait(false);
            try { await previews.ClearAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Preview cache cleanup will be retried on startup or the next cache write.");
            }

            return;
        }

        try
        {
            foreach (var relativePath in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    await payloadStore.DeleteAsync(relativePath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning(exception, "Owned payload cleanup failed after a completed removal.");
                }
            }
        }
        finally
        {
            try { await previews.ClearAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Preview cache cleanup will be retried on startup or the next cache write.");
            }
        }
    }

    private void CancelExpiration()
    {
        _expirationCancellation?.Cancel();
        _expirationCancellation = null;
    }

    private void PublishState(UndoState? state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ActiveUndo
    {
        private ActiveUndo(UndoState state, string? removal, IReadOnlyDictionary<Guid, bool>? previousPinStates)
        {
            State = state;
            Removal = removal;
            PreviousPinStates = previousPinStates;
        }

        public UndoState State { get; }

        public string? Removal { get; }

        public IReadOnlyDictionary<Guid, bool>? PreviousPinStates { get; }

        public static ActiveUndo ForRemoval(UndoState state) => new(state, state.Token, null);

        public static ActiveUndo ForPinChange(UndoState state, IReadOnlyDictionary<Guid, bool> previousPinStates) =>
            new(state, null, new Dictionary<Guid, bool>(previousPinStates));
    }
}
