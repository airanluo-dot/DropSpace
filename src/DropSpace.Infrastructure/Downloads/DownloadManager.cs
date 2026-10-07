// File-only adaptation of NovaClip Beta8 DownloadManager: durable admission, per-run ownership,
// cancellation settlement, reservations and checkpoint persistence. No media resolver or FFmpeg.
using System.Security.Cryptography;
using DropSpace.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Downloads;

public sealed class DownloadManager : IAsyncDisposable
{
    private readonly HttpRangeDownloader _engine;
    private readonly IDownloadTaskRepository _repository;
    private readonly DownloadPersistenceWorker _persistence;
    private readonly OutputReservationService _reservations = new();
    private readonly Dictionary<Guid, Work> _work = [];
    private readonly List<Guid> _order = [];
    private readonly IReadOnlyList<DownloadTaskSnapshot> _taskView;
    private readonly HashSet<Guid> _dirtyProgress = [];
    private readonly Timer _progressNotification;
    private readonly Timer _cleanupRetry;
    private Task _cleanupRun = Task.CompletedTask;
    private bool _notificationScheduled;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _lifetimeCallbacks;
    private readonly ILogger<DownloadManager>? _logger;
    private bool _stopping;
    private bool _restored;
    private bool _disposed;
    public DownloadManager(HttpRangeDownloader engine, IDownloadTaskRepository repository, ILogger<DownloadManager>? logger = null)
    {
        _engine = engine; _repository = repository; _persistence = new(repository); _logger = logger;
        _taskView = new TaskView(this);
        _progressNotification = new Timer(_ => PublishProgress(), null, Timeout.Infinite, Timeout.Infinite);
        _cleanupRetry = new Timer(_ => StartCleanupRetry(), null, Timeout.Infinite, Timeout.Infinite);
    }
    public string? RecoveryError { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler<DownloadTaskSnapshot>? TaskChanged;
    public IReadOnlyList<DownloadTaskSnapshot> Tasks => _taskView;
    public IReadOnlyList<DownloadTaskSnapshot> GetVisibleTasks(int historyLimit)
    {
        lock (_sync)
        {
            var result = new List<DownloadTaskSnapshot>();
            var history = 0;
            for (var i = _order.Count - 1; i >= 0; i--)
            {
                var item = _work[_order[i]].Snapshot;
                if (item.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled && ++history > historyLimit) continue;
                result.Add(item);
            }
            return result;
        }
    }
    public async Task RemoveHistoryAsync(Guid id)
    {
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            Work? item;
            lock (_sync) _work.TryGetValue(id, out item);
            if (item is null || item.Snapshot.State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled)) return;
            await ObserveRunAsync(item.Run).ConfigureAwait(false);
            // Record the user's removal before hiding the row. If a locked marker cannot
            // be deleted, this small tombstone owns cleanup across process restarts.
            var previous = item.Snapshot;
            lock (_sync) item.Snapshot = item.Snapshot with { HistoryRemovalPending = true, UpdatedAt = DateTimeOffset.UtcNow };
            try { await _persistence.EnqueueCriticalAsync(item.Snapshot).ConfigureAwait(false); }
            catch { lock (_sync) item.Snapshot = previous; throw; }
            lock (_sync) { _order.Remove(id); _dirtyProgress.Remove(id); }
            if (await TryCleanupAsync(item).ConfigureAwait(false)) await DeleteHistoryRecordAsync(item).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _actions.Release(); }
    }
    private void PublishProgress()
    {
        DownloadTaskSnapshot[] changed;
        lock (_sync)
        {
            changed = _dirtyProgress.Where(_work.ContainsKey).Select(id => _work[id].Snapshot).ToArray();
            _dirtyProgress.Clear(); _notificationScheduled = false;
        }
        foreach (var snapshot in changed) NotifyTask(snapshot);
    }
    private void NotifyTask(DownloadTaskSnapshot snapshot)
    {
        foreach (var observer in TaskChanged?.GetInvocationList() ?? [])
            try { ((EventHandler<DownloadTaskSnapshot>)observer)(this, snapshot); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { _logger?.LogWarning("Download observer failed: {Reason}", error.GetType().Name); }
    }
    private static string Staging(DownloadRequest request) => Path.Combine(request.OutputDirectory, ".dropspace-downloads", request.TaskId.ToString("N"), "file.part");

    public async Task EnqueueAsync(string url, string directory, string? fileName)
    {
        var request = DirectFileRequestFactory.Create(url, directory, fileName);
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var reservation = await _reservations.ReserveAsync(request.TaskId, directory, request.OutputFileName).ConfigureAwait(false);
            var item = new Work(new() { Id = request.TaskId, Request = request, OutputPath = reservation.OutputPath, State = DownloadTaskState.Queued });
            item.Reservation = reservation;
            try { await _persistence.EnqueueCriticalAsync(item.Snapshot).ConfigureAwait(false); }
            catch
            {
                try { await _reservations.ReleaseAsync(reservation).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Admission never started a transfer. Retain a hidden cleanup owner
                    // if both journaling and releasing its marker failed, rather than
                    // leaving a live-process marker with no Work or retry path.
                    item.Snapshot = item.Snapshot with
                    { State = DownloadTaskState.Cancelled, CleanupPending = true, CleanupErrorCode = error.GetType().Name, HistoryRemovalPending = true };
                    lock (_sync) _work.Add(request.TaskId, item);
                    _persistence.EnqueueProgress(item.Snapshot);
                    ScheduleCleanupRetry();
                    _logger?.LogWarning("Download {TaskId} admission cleanup deferred: {Reason}", request.TaskId, error.GetType().Name);
                }
                throw;
            }
            lock (_sync) { _work.Add(request.TaskId, item); _order.Add(request.TaskId); }
            Start(item);
        }
        finally { _actions.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _actions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_restored) return;
            _restored = true;
            var recovered = await _repository.GetAllAsync(token).ConfigureAwait(false);
            RecoveryError = _repository.RecoveryError;
            foreach (var snapshot in recovered)
            {
                token.ThrowIfCancellationRequested();
                var terminal = snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled;
                var item = new Work(snapshot with
                {
                    ActiveConnections = 0, BytesPerSecond = 0,
                    // Migrate old completed journals that classified cleanup as failure.
                    ErrorCode = terminal ? null : snapshot.ErrorCode,
                    CleanupPending = snapshot.CleanupPending || (terminal && snapshot.ErrorCode is not null),
                    CleanupErrorCode = snapshot.CleanupErrorCode ?? (terminal ? snapshot.ErrorCode : null),
                    HistoryRemovalPending = terminal && snapshot.HistoryRemovalPending,
                });
                lock (_sync)
                {
                    if (!_work.ContainsKey(snapshot.Id) && !item.Snapshot.HistoryRemovalPending) _order.Add(snapshot.Id);
                    _work[item.Snapshot.Id] = item;
                }
                try
                {
                if (snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled)
                {
                    if (await TryCleanupAsync(item).ConfigureAwait(false) && item.Snapshot.HistoryRemovalPending)
                        await DeleteHistoryRecordAsync(item).ConfigureAwait(false);
                    continue;
                }
                if (await ReconcileCommittedAsync(item, token).ConfigureAwait(false)) continue;
                await SetAsync(item, snapshot.State == DownloadTaskState.Failed ? DownloadTaskState.Failed : DownloadTaskState.Paused).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    lock (_sync) item.Snapshot = item.Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled
                        ? item.Snapshot with { ErrorCode = null, CleanupPending = true, CleanupErrorCode = "Recovery:" + error.GetType().Name }
                        : item.Snapshot with { State = DownloadTaskState.Failed, ErrorCode = "Recovery:" + error.GetType().Name };
                    if (item.Snapshot.CleanupPending) ScheduleCleanupRetry();
                    _logger?.LogWarning("Download {TaskId} recovery deferred: {Reason}", snapshot.Id, error.GetType().Name);
                    // Do not attempt another throwing checkpoint while reporting a failed checkpoint.
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            RecoveryError = error.GetType().Name;
            _logger?.LogWarning("Download recovery unavailable: {Reason}", RecoveryError);
            _restored = false;
        }
        finally { _actions.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private async Task<bool> ReconcileCommittedAsync(Work item, CancellationToken token)
    {
        var snapshot = item.Snapshot;
        if (snapshot.State is not (DownloadTaskState.Finalizing or DownloadTaskState.Failed) ||
            snapshot.FinalSha256 is not { Length: 64 } hash || !File.Exists(snapshot.OutputPath)) return false;
        DownloadStorage.Safe(snapshot.Request.OutputDirectory, snapshot.OutputPath);
        await using var file = new FileStream(snapshot.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (snapshot.TotalBytes != file.Length ||
            !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)), hash, StringComparison.OrdinalIgnoreCase)) return false;
        await SetAsync(item, DownloadTaskState.Completed).ConfigureAwait(false);
        await TryCleanupAsync(item).ConfigureAwait(false);
        return true;
    }
    private async Task<bool> TryCleanupAsync(Work item)
    {
        string? cleanupError = null;
        try
        {
            var snapshot = item.Snapshot;
            var marker = DownloadStorage.Safe(snapshot.Request.OutputDirectory, snapshot.OutputPath + ".dropspace-reservation");
            item.Reservation ??= new(snapshot.Id, snapshot.OutputPath, marker);
            await _reservations.ReleaseAsync(item.Reservation).ConfigureAwait(false);
            item.Reservation = null;
            if (Directory.Exists(Path.GetDirectoryName(Staging(snapshot.Request))))
                DownloadStorage.Clean(DownloadStorage.Safe(snapshot.Request.OutputDirectory, Staging(snapshot.Request)));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            cleanupError = error.GetType().Name;
            _logger?.LogWarning("Download {TaskId} cleanup deferred: {Reason}", item.Snapshot.Id, cleanupError);
        }
        DownloadTaskSnapshot next;
        bool changed;
        lock (_sync)
        {
            changed = item.Snapshot.CleanupPending != (cleanupError is not null) || item.Snapshot.CleanupErrorCode != cleanupError || item.Snapshot.ErrorCode is not null;
            item.Snapshot = next = item.Snapshot with
            { CleanupPending = cleanupError is not null, CleanupErrorCode = cleanupError, ErrorCode = null, UpdatedAt = DateTimeOffset.UtcNow };
        }
        if (cleanupError is not null) ScheduleCleanupRetry();
        if (changed)
        {
            try { await _persistence.EnqueueCriticalAsync(next).ConfigureAwait(false); }
            catch
            {
                // A failed auxiliary checkpoint is still work to retry, even if all
                // files were cleaned successfully. Do not clear the last retry bit.
                lock (_sync) item.Snapshot = item.Snapshot with
                { CleanupPending = true, CleanupErrorCode = "CleanupCheckpointDeferred", UpdatedAt = DateTimeOffset.UtcNow };
                ScheduleCleanupRetry();
                throw;
            }
            if (!next.HistoryRemovalPending) NotifyTask(next);
        }
        return cleanupError is null;
    }
    private async Task DeleteHistoryRecordAsync(Work item)
    {
        try { await _persistence.DeleteAsync(item.Snapshot.Id).ConfigureAwait(false); }
        catch { ScheduleCleanupRetry(); throw; }
        lock (_sync) { _work.Remove(item.Snapshot.Id); _order.Remove(item.Snapshot.Id); _dirtyProgress.Remove(item.Snapshot.Id); }
    }
    private void ScheduleCleanupRetry()
    {
        if (!_stopping) _cleanupRetry.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }
    private void StartCleanupRetry()
    {
        lock (_sync)
        {
            if (_stopping) return;
            if (!_cleanupRun.IsCompleted) { ScheduleCleanupRetry(); return; }
            _cleanupRun = Task.Run(RetryDeferredCleanupAsync);
        }
    }
    internal async Task RetryDeferredCleanupAsync()
    {
        try
        {
            await _actions.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (_stopping) return;
                Work[] pending;
                lock (_sync) pending = _work.Values.Where(item => item.Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled &&
                    (item.Snapshot.CleanupPending || item.Snapshot.HistoryRemovalPending)).ToArray();
                foreach (var item in pending)
                {
                    await ObserveRunAsync(item.Run).ConfigureAwait(false);
                    if (await TryCleanupAsync(item).ConfigureAwait(false) && item.Snapshot.HistoryRemovalPending)
                        await DeleteHistoryRecordAsync(item).ConfigureAwait(false);
                }
            }
            finally { _actions.Release(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _logger?.LogWarning("Download cleanup retry deferred: {Reason}", error.GetType().Name);
            ScheduleCleanupRetry();
        }
    }
    public async Task PauseAsync(Guid id) => await ControlAsync(id, resume: false, cancel: false).ConfigureAwait(false);
    public async Task ResumeAsync(Guid id) => await ControlAsync(id, resume: true, cancel: false).ConfigureAwait(false);
    public async Task CancelAsync(Guid id) => await ControlAsync(id, resume: false, cancel: true).ConfigureAwait(false);
    private async Task ControlAsync(Guid id, bool resume, bool cancel)
    {
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping || !_work.TryGetValue(id, out var item) || item.Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled) return;
            if (resume)
            {
                if (item.Snapshot.State is not (DownloadTaskState.Paused or DownloadTaskState.Failed)) return;
                await ObserveRunAsync(item.Run).ConfigureAwait(false);
                if (await ReconcileCommittedAsync(item, _lifetime.Token).ConfigureAwait(false)) return;
                await SetAsync(item, DownloadTaskState.Queued).ConfigureAwait(false);
                Start(item);
            }
            else
            {
                await RequestStopAsync(item).ConfigureAwait(false);
                await ObserveRunAsync(item.Run).ConfigureAwait(false);
                if (item.Snapshot.State == DownloadTaskState.Completed) return;
                await SetAsync(item, cancel ? DownloadTaskState.Cancelled : DownloadTaskState.Paused).ConfigureAwait(false);
                // Cancellation is terminal once all owned writes stop. Auxiliary deletion
                // failure is retained separately and never starts another transfer.
                if (cancel) await TryCleanupAsync(item).ConfigureAwait(false);
            }
        }
        finally { _actions.Release(); }
    }
    private Task RequestStopAsync(Work item)
    {
        lock (_sync)
        {
            if (item.Stop is not { } stop) return Task.CompletedTask;
            // A second CancelAsync call can return before the first call's callbacks settle.
            return item.StopCallbacks ??= stop.CancelAsync();
        }
    }
    private void Start(Work item)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = stop.Token;
        lock (_sync)
        {
            item.Stop = stop; item.StopCallbacks = null;
            item.Snapshot = item.Snapshot with { RunId = item.Snapshot.RunId + 1, ErrorCode = null };
        }
        item.Run = Task.Run(() => RunAsync(item, stop, token));
    }
    private async Task RunAsync(Work item, CancellationTokenSource stop, CancellationToken token)
    {
        try
        {
            var request = item.Snapshot.Request;
            if (item.Reservation is { } oldReservation && !File.Exists(oldReservation.MarkerPath)) item.Reservation = null;
            item.Reservation ??= await _reservations.ReserveAsync(request.TaskId, request.OutputDirectory, Path.GetFileName(item.Snapshot.OutputPath), token).ConfigureAwait(false);
            lock (_sync) item.Snapshot = item.Snapshot with { OutputPath = item.Reservation.OutputPath };
            await SetAsync(item, DownloadTaskState.Queued).ConfigureAwait(false);
            var staging = DownloadStorage.Safe(request.OutputDirectory, Staging(request));
            var progress = new InlineProgress(value =>
            {
                DownloadTaskSnapshot snapshot;
                lock (_sync)
                {
                    item.Snapshot = snapshot = item.Snapshot with { State = value.Stage == DownloadStage.Queued ? DownloadTaskState.Queued : DownloadTaskState.DownloadingFile,
                        DownloadedBytes = value.DownloadedBytes, TotalBytes = value.TotalBytes, BytesPerSecond = value.BytesPerSecond,
                        ActiveConnections = value.ActiveConnections, Stage = value.Stage, UpdatedAt = DateTimeOffset.UtcNow };
                }
                _persistence.EnqueueProgress(snapshot);
                lock (_sync)
                {
                    _dirtyProgress.Add(snapshot.Id);
                    if (!_notificationScheduled)
                    { _notificationScheduled = true; _progressNotification.Change(200, Timeout.Infinite); }
                }
            });
            await _engine.DownloadAsync(new Uri(request.Url), staging, _engine.OrdinaryPolicy, progress, token).ConfigureAwait(false);
            await SetAsync(item, DownloadTaskState.Finalizing).ConfigureAwait(false);
            await using (var file = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
                lock (_sync) item.Snapshot = item.Snapshot with { FinalSha256 = hash, DownloadedBytes = file.Length, TotalBytes = file.Length };
            }
            await SetAsync(item, DownloadTaskState.Finalizing).ConfigureAwait(false);
            // Persist any collision rename before the atomic publication.
            item.Reservation = await _reservations.CommitTrackedAsync(item.Reservation, staging, async reservation =>
            {
                lock (_sync)
                {
                    item.Reservation = reservation;
                    item.Snapshot = item.Snapshot with { OutputPath = reservation.OutputPath };
                }
                await _persistence.EnqueueCriticalAsync(item.Snapshot).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            lock (_sync) item.Snapshot = item.Snapshot with
            { ErrorCode = null, CleanupPending = item.Reservation.CleanupPending,
                CleanupErrorCode = item.Reservation.CleanupPending ? "MarkerCleanupDeferred" : null };
            await SetAsync(item, DownloadTaskState.Completed).ConfigureAwait(false);
            await TryCleanupAsync(item).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await SetAsync(item, DownloadTaskState.Paused).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (item.Snapshot.State != DownloadTaskState.Completed)
            {
                lock (_sync) item.Snapshot = item.Snapshot with { ErrorCode = error.GetType().Name };
                await SetAsync(item, DownloadTaskState.Failed).ConfigureAwait(false);
            }
            else
            {
                lock (_sync) item.Snapshot = item.Snapshot with
                { ErrorCode = null, CleanupPending = true, CleanupErrorCode = error.GetType().Name, UpdatedAt = DateTimeOffset.UtcNow };
                ScheduleCleanupRetry();
                _persistence.EnqueueProgress(item.Snapshot);
                _logger?.LogWarning("Completed download {TaskId} checkpoint or cleanup deferred: {Reason}", item.Snapshot.Id, error.GetType().Name);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            Task callbacks, parentCallbacks;
            lock (_sync)
            {
                // History owns snapshots; only this run owns its cancellation source.
                item.Stop = null;
                callbacks = item.StopCallbacks ?? Task.CompletedTask;
                item.StopCallbacks = null;
                parentCallbacks = _lifetimeCallbacks ?? Task.CompletedTask;
            }
            try
            {
                // ControlAsync and ShutdownAsync own and surface cancellation errors.
                // Retirement waits for the same callbacks without poisoning a later Retry.
                await callbacks.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await parentCallbacks.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            finally
            {
                // If parent cancellation began after the snapshot, linked disposal
                // waits for its registration. Never do this under the progress lock.
                stop.Dispose();
            }
        }
    }
    private async Task SetAsync(Work item, DownloadTaskState state)
    {
        DownloadTaskSnapshot snapshot;
        lock (_sync) item.Snapshot = snapshot = item.Snapshot with { State = state, ActiveConnections = 0, BytesPerSecond = 0,
            ErrorCode = state is DownloadTaskState.Completed or DownloadTaskState.Cancelled ? null : item.Snapshot.ErrorCode, UpdatedAt = DateTimeOffset.UtcNow };
        try { await _persistence.EnqueueCriticalAsync(snapshot).ConfigureAwait(false); }
        finally { NotifyTask(snapshot); }
    }
    private static async Task ObserveRunAsync(Task run)
    {
        // A failed checkpoint (for example a full disk) must not permanently poison Retry.
        // RunAsync already projects failure; observation still waits for all owned writes.
        try { await run.ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    public async Task ShutdownAsync()
    {
        Task callbacks;
        // Publish the first cancellation task before any callback can retire a run.
        lock (_sync) callbacks = _lifetimeCallbacks ??= _lifetime.CancelAsync();
        try { await callbacks.ConfigureAwait(false); }
        finally { await ShutdownCoreAsync().ConfigureAwait(false); }
    }
    private async Task ShutdownCoreAsync()
    {
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping) return;
            _stopping = true;
            _cleanupRetry.Change(Timeout.Infinite, Timeout.Infinite);
            try
            {
                foreach (var item in _work.Values) await RequestStopAsync(item).ConfigureAwait(false);
            }
            finally
            {
                await Task.WhenAll(_work.Values.Select(w => ObserveRunAsync(w.Run))).ConfigureAwait(false);
                foreach (var item in _work.Values.ToArray())
                {
                    try
                    {
                        if (item.Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled)
                        {
                            if (await TryCleanupAsync(item).ConfigureAwait(false) && item.Snapshot.HistoryRemovalPending)
                                await DeleteHistoryRecordAsync(item).ConfigureAwait(false);
                        }
                        else if (item.Reservation is { } reservation)
                        {
                            // Unfinished transfers keep their parts; only release their marker.
                            await _reservations.ReleaseAsync(reservation).ConfigureAwait(false);
                            item.Reservation = null;
                        }
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                    { _logger?.LogWarning("Download {TaskId} shutdown cleanup deferred: {Reason}", item.Snapshot.Id, error.GetType().Name); }
                }
                // Cleanup checkpoints and hidden removal records must settle before closing
                // the journal writer. Marker failures must not prevent network shutdown.
                await _persistence.DrainAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
        }
        finally { _actions.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await ShutdownAsync().ConfigureAwait(false); }
        finally
        {
            _disposed = true;
            await _cleanupRetry.DisposeAsync().ConfigureAwait(false);
            await _cleanupRun.ConfigureAwait(false);
            await _progressNotification.DisposeAsync().ConfigureAwait(false);
            await _persistence.DisposeAsync().ConfigureAwait(false); _reservations.Dispose(); _lifetime.Dispose();
        }
    }
    private sealed class TaskView(DownloadManager owner) : IReadOnlyList<DownloadTaskSnapshot>
    {
        public int Count { get { lock (owner._sync) return owner._order.Count; } }
        public DownloadTaskSnapshot this[int index] { get { lock (owner._sync) return owner._work[owner._order[index]].Snapshot; } }
        public IEnumerator<DownloadTaskSnapshot> GetEnumerator()
        {
            for (var index = 0; ; index++)
            {
                DownloadTaskSnapshot snapshot;
                lock (owner._sync)
                { if (index >= owner._order.Count) yield break; snapshot = owner._work[owner._order[index]].Snapshot; }
                yield return snapshot;
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Work(DownloadTaskSnapshot snapshot)
    {
        public DownloadTaskSnapshot Snapshot = snapshot;
        public OutputReservation? Reservation;
        public CancellationTokenSource? Stop;
        public Task? StopCallbacks;
        public Task Run = Task.CompletedTask;
    }
    internal sealed class InlineProgress(Action<TrackProgress> report) : IProgress<TrackProgress>
    { public void Report(TrackProgress value) => report(value); }
}
