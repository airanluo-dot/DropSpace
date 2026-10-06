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
    private bool _notificationScheduled;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<DownloadManager>? _logger;
    private bool _stopping;
    private bool _restored;
    private bool _disposed;
    public DownloadManager(HttpRangeDownloader engine, IDownloadTaskRepository repository, ILogger<DownloadManager>? logger = null)
    {
        _engine = engine; _repository = repository; _persistence = new(repository); _logger = logger;
        _taskView = new TaskView(this);
        _progressNotification = new Timer(_ => PublishProgress(), null, Timeout.Infinite, Timeout.Infinite);
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
            await _persistence.DeleteAsync(id).ConfigureAwait(false);
            lock (_sync) { _work.Remove(id); _order.Remove(id); _dirtyProgress.Remove(id); }
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
            catch { await _reservations.ReleaseAsync(reservation).ConfigureAwait(false); throw; }
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
                var item = new Work(snapshot with { ActiveConnections = 0, BytesPerSecond = 0 });
                lock (_sync) { if (!_work.ContainsKey(snapshot.Id)) _order.Add(snapshot.Id); _work[item.Snapshot.Id] = item; }
                try
                {
                if (snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled)
                { TryClean(item.Snapshot.Request); await ReleaseRecoveredMarkerAsync(item.Snapshot).ConfigureAwait(false); continue; }
                if (await ReconcileCommittedAsync(item, token).ConfigureAwait(false)) continue;
                await SetAsync(item, snapshot.State == DownloadTaskState.Failed ? DownloadTaskState.Failed : DownloadTaskState.Paused).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    lock (_sync) item.Snapshot = item.Snapshot with
                    { State = item.Snapshot.State == DownloadTaskState.Completed ? DownloadTaskState.Completed : DownloadTaskState.Failed,
                      ErrorCode = "Recovery:" + error.GetType().Name };
                    _logger?.LogWarning("Download {TaskId} recovery deferred: {Reason}", snapshot.Id, error.GetType().Name);
                    // Do not attempt another throwing checkpoint while reporting a failed checkpoint.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
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
        TryClean(snapshot.Request);
        await ReleaseRecoveredMarkerAsync(snapshot).ConfigureAwait(false);
        return true;
    }
    private async Task ReleaseRecoveredMarkerAsync(DownloadTaskSnapshot snapshot)
    {
        try
        {
            var marker = DownloadStorage.Safe(snapshot.Request.OutputDirectory, snapshot.OutputPath + ".dropspace-reservation");
            await _reservations.ReleaseAsync(new(snapshot.Id, snapshot.OutputPath, marker)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _logger?.LogWarning("Download marker cleanup deferred: {Reason}", error.GetType().Name); }
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
                if (item.Stop is not null) await item.Stop.CancelAsync().ConfigureAwait(false);
                await ObserveRunAsync(item.Run).ConfigureAwait(false);
                if (item.Snapshot.State == DownloadTaskState.Completed) return;
                if (cancel)
                {
                    // Failed cleanup remains retryable; never claim canceled until owned writes stop.
                    var staging = DownloadStorage.Safe(item.Snapshot.Request.OutputDirectory, Staging(item.Snapshot.Request));
                    DownloadStorage.Clean(staging);
                    if (item.Reservation is { } reservation) await _reservations.ReleaseAsync(reservation).ConfigureAwait(false);
                }
                await SetAsync(item, cancel ? DownloadTaskState.Cancelled : DownloadTaskState.Paused).ConfigureAwait(false);
            }
        }
        finally { _actions.Release(); }
    }
    private void Start(Work item)
    {
        item.Stop?.Dispose(); item.Stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_sync) item.Snapshot = item.Snapshot with { RunId = item.Snapshot.RunId + 1, ErrorCode = null };
        item.Run = Task.Run(() => RunAsync(item, item.Stop.Token));
    }
    private async Task RunAsync(Work item, CancellationToken token)
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
            if (item.Reservation.CleanupPending)
                lock (_sync) item.Snapshot = item.Snapshot with { ErrorCode = "MarkerCleanupDeferred" };
            await SetAsync(item, DownloadTaskState.Completed).ConfigureAwait(false);
            DownloadStorage.Clean(staging);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await SetAsync(item, DownloadTaskState.Paused).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (_sync) item.Snapshot = item.Snapshot with { ErrorCode = error.GetType().Name };
            if (item.Snapshot.State != DownloadTaskState.Completed) await SetAsync(item, DownloadTaskState.Failed).ConfigureAwait(false);
            else
            {
                _logger?.LogWarning("Completed download {TaskId} checkpoint or cleanup deferred: {Reason}", item.Snapshot.Id, error.GetType().Name);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
    private async Task SetAsync(Work item, DownloadTaskState state)
    {
        DownloadTaskSnapshot snapshot;
        lock (_sync) item.Snapshot = snapshot = item.Snapshot with { State = state, ActiveConnections = 0, BytesPerSecond = 0, UpdatedAt = DateTimeOffset.UtcNow };
        try { await _persistence.EnqueueCriticalAsync(snapshot).ConfigureAwait(false); }
        finally { NotifyTask(snapshot); }
    }
    private static void TryClean(DownloadRequest request)
    {
        try
        {
            // Validate from the user-selected root as well as the private task root.
            if (!Directory.Exists(Path.GetDirectoryName(Staging(request)))) return;
            var staging = DownloadStorage.Safe(request.OutputDirectory, Staging(request));
            DownloadStorage.Clean(staging);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
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
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping) return;
            _stopping = true;
            foreach (var item in _work.Values) if (item.Stop is not null) await item.Stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_work.Values.Select(w => ObserveRunAsync(w.Run))).ConfigureAwait(false);
            await _persistence.DrainAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            foreach (var item in _work.Values)
            {
                if (item.Reservation is { } reservation) await _reservations.ReleaseAsync(reservation).ConfigureAwait(false);
                item.Stop?.Dispose(); item.Stop = null;
            }
        }
        finally { _actions.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await ShutdownAsync().ConfigureAwait(false); _disposed = true;
        await _progressNotification.DisposeAsync().ConfigureAwait(false);
        await _persistence.DisposeAsync().ConfigureAwait(false); _reservations.Dispose(); _lifetime.Dispose();
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
        public Task Run = Task.CompletedTask;
    }
    internal sealed class InlineProgress(Action<TrackProgress> report) : IProgress<TrackProgress>
    { public void Report(TrackProgress value) => report(value); }
}
