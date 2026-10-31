// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
using System.Collections.Concurrent;
using DropSpace.Core.Downloads;

namespace DropSpace.Infrastructure.Downloads;

public sealed class DownloadPersistenceWorker : IAsyncDisposable
{
    private static readonly TimeSpan ProgressCheckpointInterval = TimeSpan.FromMilliseconds(500);
    private const long ProgressCheckpointByteDelta = 1_048_576;

    private readonly IDownloadTaskRepository _repository;
    private readonly ConcurrentQueue<CriticalWrite> _critical = new();
    private readonly ConcurrentDictionary<Guid, DownloadTaskSnapshot> _progress = new();
    private readonly Dictionary<Guid, ProgressCheckpoint> _checkpoints = [];
    private readonly Dictionary<Guid, DateTimeOffset> _retryNotBefore = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stopSource = new();
    private readonly Task _worker;
    private readonly object _admissionGate = new();
    private Task? _disposeTask;
    private int _accepting = 1;

    public DownloadPersistenceWorker(IDownloadTaskRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _worker = Task.Run(WorkerAsync);
    }

    public bool IsAccepting => Volatile.Read(ref _accepting) == 1;
    public Task DeleteAsync(Guid id)
    {
        lock (_admissionGate)
        {
            if (!IsAccepting) throw new ObjectDisposedException(nameof(DownloadPersistenceWorker));
            _progress.TryRemove(id, out _);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _critical.Enqueue(new CriticalWrite(null, completion, id));
            ReleaseSignal();
            return completion.Task;
        }
    }

    public void EnqueueProgress(DownloadTaskSnapshot snapshot)
    {
        lock (_admissionGate)
        {
            if (!IsAccepting) return;
            var wasEmpty = _progress.IsEmpty;
            _progress.AddOrUpdate(
                snapshot.Id,
                snapshot,
                (_, existing) => snapshot.UpdatedAt >= existing.UpdatedAt ? snapshot : existing);
            if (wasEmpty) ReleaseSignal();
        }
    }

    public Task EnqueueCriticalAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        lock (_admissionGate)
        {
            if (!IsAccepting) return Task.FromException(new ObjectDisposedException(nameof(DownloadPersistenceWorker)));
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _critical.Enqueue(new CriticalWrite(snapshot, completion));
            ReleaseSignal();
            return completion.Task;
        }
    }

    public async Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Publication and closing admission are one boundary: every accepted
        // critical write must be visible before the worker can decide to exit.
        lock (_admissionGate)
        {
            Volatile.Write(ref _accepting, 0);
            ReleaseSignal();
        }
        await _worker.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (Volatile.Read(ref _accepting) == 1 || HasPending())
            {
                TimeSpan? wait;
                lock (_admissionGate)
                {
                    // A producer may coalesce without signalling when progress was
                    // already queued. Do not choose an idle wait between that
                    // producer's emptiness check and its replacement publication.
                    wait = GetProgressWait();
                }
                if (wait is null)
                {
                    await _signal.WaitAsync(_stopSource.Token).ConfigureAwait(false);
                }
                else
                {
                    _ = await _signal.WaitAsync(wait.Value, _stopSource.Token).ConfigureAwait(false);
                }

                while (_critical.TryDequeue(out var critical))
                {
                    try
                    {
                        if (critical.Snapshot is { } snapshot)
                        {
                            await _repository.UpsertAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
                            RecordCheckpoint(snapshot);
                        }
                        else
                        {
                            await _repository.DeleteAsync(critical.DeleteId, CancellationToken.None).ConfigureAwait(false);
                            // An in-flight progress write can fail and requeue itself after
                            // DeleteAsync's admission-time purge. Retire that retry at the
                            // serialized deletion boundary so it cannot recreate history.
                            _progress.TryRemove(critical.DeleteId, out _);
                            _checkpoints.Remove(critical.DeleteId);
                            _retryNotBefore.Remove(critical.DeleteId);
                        }
                        critical.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        critical.Completion.TrySetException(exception);
                    }
                }

                await FlushDueProgressAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
        {
            while (_critical.TryDequeue(out var critical)) critical.Completion.TrySetCanceled();
        }
    }

    private async Task FlushDueProgressAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _progress.ToArray())
        {
            if (_retryNotBefore.TryGetValue(pair.Key, out var retryAt) && retryAt > now) continue;
            if (!ShouldPersist(pair.Value, now)) continue;
            if (!_progress.TryRemove(pair.Key, out var snapshot)) continue;
            if (_checkpoints.TryGetValue(snapshot.Id, out var latest) &&
                (snapshot.RunId < latest.RunId ||
                 (snapshot.RunId == latest.RunId && snapshot.UpdatedAt <= latest.UpdatedAt))) continue;

            try
            {
                await _repository.UpsertAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
                RecordCheckpoint(snapshot);
                _retryNotBefore.Remove(snapshot.Id);
            }
            catch
            {
                // A locked or unavailable database must not become a zero-delay
                // retry loop that pins a CPU core while the UI appears frozen.
                _retryNotBefore[snapshot.Id] = DateTimeOffset.UtcNow + ProgressCheckpointInterval;
                _progress.AddOrUpdate(
                    snapshot.Id,
                    snapshot,
                    (_, existing) => existing.UpdatedAt >= snapshot.UpdatedAt ? existing : snapshot);
            }
        }
    }

    private TimeSpan? GetProgressWait()
    {
        if (_progress.IsEmpty) return null;

        var now = DateTimeOffset.UtcNow;
        TimeSpan? shortest = null;
        foreach (var pair in _progress)
        {
            if (_retryNotBefore.TryGetValue(pair.Key, out var retryAt) && retryAt > now)
            {
                var retryWait = retryAt - now;
                if (shortest is null || retryWait < shortest.Value) shortest = retryWait;
                continue;
            }
            if (ShouldPersist(pair.Value, now)) return TimeSpan.Zero;
            if (!_checkpoints.TryGetValue(pair.Key, out var checkpoint)) return TimeSpan.Zero;

            var remaining = checkpoint.PersistedAt + ProgressCheckpointInterval - now;
            if (shortest is null || remaining < shortest.Value) shortest = remaining;
        }

        return shortest is null || shortest.Value < TimeSpan.Zero ? TimeSpan.Zero : shortest;
    }

    private bool ShouldPersist(DownloadTaskSnapshot snapshot, DateTimeOffset now)
    {
        if (!_checkpoints.TryGetValue(snapshot.Id, out var checkpoint)) return true;
        if (snapshot.RunId != checkpoint.RunId ||
            snapshot.State != checkpoint.State ||
            snapshot.OperationState != checkpoint.OperationState)
        {
            return true;
        }

        return snapshot.DownloadedBytes >= checkpoint.DownloadedBytes + ProgressCheckpointByteDelta ||
            now - checkpoint.PersistedAt >= ProgressCheckpointInterval;
    }

    private void RecordCheckpoint(DownloadTaskSnapshot snapshot)
    {
        _checkpoints[snapshot.Id] = new ProgressCheckpoint(
            snapshot.RunId,
            snapshot.State,
            snapshot.OperationState,
            snapshot.DownloadedBytes,
            snapshot.UpdatedAt,
            DateTimeOffset.UtcNow);
    }

    private bool HasPending() => !_critical.IsEmpty || !_progress.IsEmpty;

    private void ReleaseSignal()
    {
        try { _signal.Release(); } catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        Task disposal;
        lock (_admissionGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _accepting, 0);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
            }
            disposal = _disposeTask;
        }
        // Cancellation and repository retirement never run under admission ownership.
        if (completion is not null) _ = DisposeCoreAsync(completion);
        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            _stopSource.Cancel();
            try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _stopSource.Dispose();
            _signal.Dispose();
            GC.SuppressFinalize(this);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private sealed record CriticalWrite(DownloadTaskSnapshot? Snapshot, TaskCompletionSource Completion, Guid DeleteId = default);

    private sealed record ProgressCheckpoint(
        long RunId,
        DownloadTaskState State,
        DurableOperationState OperationState,
        long DownloadedBytes,
        DateTimeOffset UpdatedAt,
        DateTimeOffset PersistedAt);
}
