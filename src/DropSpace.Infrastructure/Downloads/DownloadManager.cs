// File-only adaptation of NovaClip Beta8 DownloadManager: durable admission, per-run ownership,
// cancellation settlement, reservations and checkpoint persistence. No media resolver or FFmpeg.
using System.Security.Cryptography;
using DropSpace.Core.Downloads;

namespace DropSpace.Infrastructure.Downloads;

public sealed class DownloadManager : IAsyncDisposable
{
    private readonly HttpRangeDownloader _engine;
    private readonly IDownloadTaskRepository _repository;
    private readonly DownloadPersistenceWorker _persistence;
    private readonly OutputReservationService _reservations = new();
    private readonly Dictionary<Guid, Work> _work = [];
    private readonly object _sync = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private bool _stopping;
    private bool _restored;
    private bool _disposed;
    public DownloadManager(HttpRangeDownloader engine, IDownloadTaskRepository repository)
    { _engine = engine; _repository = repository; _persistence = new(repository); }
    public event EventHandler? Changed;
    public IReadOnlyList<DownloadTaskSnapshot> Tasks { get { lock (_sync) return _work.Values.Select(w => w.Snapshot).OrderBy(w => w.Id).ToArray(); } }
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
            lock (_sync) _work.Add(request.TaskId, item);
            Start(item);
        }
        finally { _actions.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task RestoreAsync()
    {
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_restored) return;
            _restored = true;
            foreach (var snapshot in await _repository.GetAllAsync().ConfigureAwait(false))
            {
                var item = new Work(snapshot with { ActiveConnections = 0, BytesPerSecond = 0 });
                lock (_sync) _work[item.Snapshot.Id] = item;
                if (snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled)
                { TryClean(item.Snapshot.Request); continue; }
                // A process may exit after rename but before persisting Completed.
                if (snapshot.State == DownloadTaskState.Finalizing && snapshot.FinalSha256 is { } hash && File.Exists(snapshot.OutputPath))
                {
                    try
                    {
                        await using var file = new FileStream(snapshot.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                        if (Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false)) == hash)
                        { await SetAsync(item, DownloadTaskState.Completed).ConfigureAwait(false); TryClean(snapshot.Request); continue; }
                    }
                    catch (IOException) { }
                }
                await SetAsync(item, snapshot.State == DownloadTaskState.Failed ? DownloadTaskState.Failed : DownloadTaskState.Paused).ConfigureAwait(false);
            }
        }
        finally { _actions.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
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
        item.Stop?.Dispose(); item.Stop = new();
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
                    item.Snapshot = snapshot = item.Snapshot with { State = DownloadTaskState.DownloadingFile,
                        DownloadedBytes = value.DownloadedBytes, TotalBytes = value.TotalBytes, BytesPerSecond = value.BytesPerSecond,
                        ActiveConnections = value.ActiveConnections, UpdatedAt = DateTimeOffset.UtcNow };
                }
                _persistence.EnqueueProgress(snapshot);
                // The view renders on its 250 ms timer; do not flood the UI dispatcher per chunk.
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
            await SetAsync(item, DownloadTaskState.Completed).ConfigureAwait(false);
            DownloadStorage.Clean(staging);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await SetAsync(item, DownloadTaskState.Paused).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (_sync) item.Snapshot = item.Snapshot with { ErrorCode = error.GetType().Name };
            if (item.Snapshot.State != DownloadTaskState.Completed) await SetAsync(item, DownloadTaskState.Failed).ConfigureAwait(false);
        }
    }
    private async Task SetAsync(Work item, DownloadTaskState state)
    {
        DownloadTaskSnapshot snapshot;
        lock (_sync) item.Snapshot = snapshot = item.Snapshot with { State = state, ActiveConnections = 0, BytesPerSecond = 0, UpdatedAt = DateTimeOffset.UtcNow };
        await _persistence.EnqueueCriticalAsync(snapshot).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
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
        await _persistence.DisposeAsync().ConfigureAwait(false); _reservations.Dispose();
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
