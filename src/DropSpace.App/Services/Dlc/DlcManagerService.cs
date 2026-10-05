using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;
using DropSpace.Infrastructure.Storage;
using System.Text.Json;
using System.Diagnostics;

namespace DropSpace.App.Services.Dlc;

public enum DlcPackageState { Checking, Available, Installed, Downloading, Canceling, Removing, Failed }
public enum DlcPackageAction { Inspect, Download, Delete }
public sealed record DlcPackageSnapshot(DlcPackageDescriptor Package, DlcPackageInspection? Installation,
    DlcPackageState State, double? Progress = null, DlcPackageAction? FailedAction = null, bool WasCanceled = false);

/// <summary>Application-lifetime inventory and operation state. Lightweight provider metadata
/// describes installed files; it is never reused as execution trust.</summary>
public sealed class DlcManagerService : IAsyncDisposable
{
    private readonly IDlcPackageProvider[] _providers;
    private readonly ILogger<DlcManagerService> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, DlcPackageSnapshot> _snapshots = new(StringComparer.Ordinal);
    private CancellationTokenSource? _downloadStop;
    private string? _activeId;
    private bool _refreshPending;
    private bool _disposed;
    private long? _lastRefresh;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly string _pendingPath;

    public DlcManagerService(IEnumerable<IDlcPackageProvider> providers, ILogger<DlcManagerService> logger, AppStoragePaths paths)
    {
        _providers = providers.ToArray();
        _logger = logger;
        _pendingPath = Path.Combine(paths.Root, "Downloads", "managed-pending.json");
        foreach (var provider in _providers)
        {
            provider.PackagesChanged += OnPackagesChanged;
            foreach (var package in provider.Packages)
                _snapshots.Add(package.Id, new(package, null, DlcPackageState.Checking));
        }
    }

    public event EventHandler? Changed;
    public async Task RestoreAsync()
    {
        if (!File.Exists(_pendingPath)) return;
        try
        {
            if (new FileInfo(_pendingPath).Length > 4096) return;
            var id = JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(_pendingPath));
            lock (_sync)
                if (id is not null && _snapshots.TryGetValue(id, out var snapshot))
                    _snapshots[id] = snapshot with { State = DlcPackageState.Available, WasCanceled = true };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { _logger.LogWarning("DLC recovery journal unavailable ({Category}).", error.GetType().Name); }
    }
    private async Task PersistPendingAsync(string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath)!);
        await File.WriteAllTextAsync(_pendingPath + ".tmp", JsonSerializer.Serialize(id), _lifetime.Token);
        File.Move(_pendingPath + ".tmp", _pendingPath, true);
    }
    public bool IsBusy { get { lock (_sync) return _activeId is not null; } }
    public IReadOnlyList<DlcPackageSnapshot> Packages
    {
        get
        {
            lock (_sync) return _snapshots.Values.Where(item => item.Package.CanDownload ||
                item.Installation?.HasArtifacts == true).ToArray();
        }
    }

    private void Publish(DlcPackageSnapshot snapshot)
    {
        lock (_sync) _snapshots[snapshot.Package.Id] = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshAsync(bool force = false) => Task.Run(() => RefreshCoreAsync(force));

    private async Task RefreshCoreAsync(bool force)
    {
        lock (_sync)
        {
            if (_disposed) return;
            // Reopening either settings surface reuses the current inventory pass.
            // Only an explicit refresh or package mutation needs a follow-up pass.
            if (_activeId == string.Empty)
            {
                if (force) _refreshPending = true;
                return;
            }
            if (!force && _lastRefresh is { } refreshed && Stopwatch.GetElapsedTime(refreshed) < RefreshInterval) return;
            _refreshPending = true;
        }
        if (!await _operation.WaitAsync(0)) return;
        try
        {
            lock (_sync) { if (_disposed) return; _activeId = string.Empty; }
            do
            {
                lock (_sync) _refreshPending = false;
                var started = Stopwatch.GetTimestamp();
                var catalog = _providers.SelectMany(provider => provider.Packages.Select(package => (provider, package))).ToArray();
                var currentIds = catalog.Select(item => item.package.Id).ToHashSet(StringComparer.Ordinal);
                lock (_sync)
                {
                    foreach (var obsolete in _snapshots.Keys.Where(id => !currentIds.Contains(id)).ToArray()) _snapshots.Remove(obsolete);
                }
                foreach (var (provider, package) in catalog)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    DlcPackageSnapshot? previous;
                    lock (_sync) _snapshots.TryGetValue(package.Id, out previous);
                    if (previous?.Installation is null) Publish(new(package, null, DlcPackageState.Checking));
                    try
                    {
                        var inspection = await provider.InspectAsync(package.Id, _lifetime.Token);
                        var failedAction = previous?.State == DlcPackageState.Failed && previous.FailedAction != DlcPackageAction.Inspect
                            ? previous.FailedAction : null;
                        Publish(new(package, inspection, failedAction is not null ? DlcPackageState.Failed :
                            inspection.IsInstalled ? DlcPackageState.Installed : DlcPackageState.Available,
                            FailedAction: failedAction, WasCanceled: previous?.WasCanceled == true));
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        _logger.LogWarning("DLC inspection failed ({Category}).", error.GetType().Name);
                        Publish(new(package, previous?.Installation, DlcPackageState.Failed, FailedAction: DlcPackageAction.Inspect));
                    }
                }
                lock (_sync)
                {
                    _lastRefresh = Stopwatch.GetTimestamp();
                }
                _logger.LogDebug("DLC inventory refreshed: {PackageCount} packages in {ElapsedMilliseconds} ms.",
                    catalog.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                lock (_sync) { if (!_refreshPending) break; }
            } while (true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { _logger.LogWarning("DLC inventory refresh failed ({Category}).", error.GetType().Name); }
        finally
        {
            bool refresh;
            lock (_sync) { _activeId = null; refresh = _refreshPending && !_disposed; }
            _operation.Release();
            Changed?.Invoke(this, EventArgs.Empty);
            if (refresh) await RefreshCoreAsync(force: true);
        }
    }

    public Task DownloadAsync(string packageId, bool consent) => RunAsync(packageId, DlcPackageAction.Download, consent);
    public Task DeleteAsync(string packageId) => RunAsync(packageId, DlcPackageAction.Delete, false);
    public void CancelDownload(string packageId)
    {
        CancellationTokenSource stop;
        lock (_sync)
        {
            if (_activeId != packageId || _downloadStop is null || _downloadStop.IsCancellationRequested) return;
            stop = _downloadStop;
            _snapshots[packageId] = _snapshots[packageId] with { State = DlcPackageState.Canceling };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        // Provider callbacks may wait for workers which publish progress. Never cancel under _sync.
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { /* The completed operation already released its cancellation owner. */ }
        catch (AggregateException error) { _logger.LogWarning("DLC cancellation callback failed ({Category}).", error.GetType().Name); }
    }

    private async Task RunAsync(string packageId, DlcPackageAction action, bool consent)
    {
        lock (_sync) { if (_disposed) return; }
        if (!await _operation.WaitAsync(0)) return;
        CancellationTokenSource? stop = null;
        DlcPackageSnapshot? snapshot = null;
        IDlcPackageProvider? provider = null;
        try
        {
            lock (_sync)
            {
                if (_disposed || !_snapshots.TryGetValue(packageId, out snapshot)) return;
                _activeId = packageId;
            }
            provider = _providers.Single(p => p.Packages.Any(item => item.Id == packageId));
            if (action == DlcPackageAction.Download && (!consent || !snapshot.Package.CanDownload || snapshot.Installation?.CanDownload == false))
                throw new InvalidOperationException("Package download is unavailable or consent is missing.");
            if (action == DlcPackageAction.Delete && snapshot.Installation?.CanDelete == false)
                throw new InvalidOperationException("This package is not owned by DropSpace.");
            stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            if (action == DlcPackageAction.Download)
            {
                // Durable before the shared transport may queue for a task slot.
                await PersistPendingAsync(packageId);
                lock (_sync) _downloadStop = stop;
                Publish(snapshot with { State = DlcPackageState.Downloading, Progress = null, FailedAction = null, WasCanceled = false });
                await provider.DownloadAsync(packageId, consent, new PackageProgress(this, packageId, stop), stop.Token);
            }
            else
            {
                Publish(snapshot with { State = DlcPackageState.Removing, FailedAction = null });
                await provider.DeleteAsync(packageId, stop.Token);
            }
            // Reconstruct local reality even when cancellation raced a completed atomic install.
            var inspection = await provider.InspectAsync(packageId, _lifetime.Token);
            if (File.Exists(_pendingPath) && JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(_pendingPath)) == packageId)
                File.Delete(_pendingPath);
            Publish(new(snapshot.Package, inspection, inspection.IsInstalled ? DlcPackageState.Installed : DlcPackageState.Available));
        }
        catch (OperationCanceledException) when (stop?.IsCancellationRequested == true)
        {
            if (snapshot is not null && !_lifetime.IsCancellationRequested)
            {
                Publish(snapshot with { State = snapshot.Installation?.IsInstalled == true ? DlcPackageState.Installed : DlcPackageState.Available,
                    Progress = null, WasCanceled = true });
                lock (_sync) _refreshPending = true;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _logger.LogWarning("DLC package operation failed ({Category}).", error.GetType().Name);
            if (snapshot is not null)
            {
                // Retain the original error/action, but expose real partial files for removal.
                if (provider is not null && !_lifetime.IsCancellationRequested)
                {
                    try { snapshot = snapshot with { Installation = await provider.InspectAsync(packageId, _lifetime.Token) }; }
                    catch (Exception inspectionError) when (inspectionError is not OutOfMemoryException)
                    { _logger.LogWarning("DLC post-failure inspection failed ({Category}).", inspectionError.GetType().Name); }
                }
                Publish(snapshot with { State = DlcPackageState.Failed, FailedAction = action, Progress = null });
            }
        }
        finally
        {
            bool refresh;
            lock (_sync)
            {
                _downloadStop = null;
                _activeId = null;
                refresh = _refreshPending && !_disposed;
            }
            stop?.Dispose();
            _operation.Release();
            Changed?.Invoke(this, EventArgs.Empty);
            if (refresh) await RefreshAsync(force: true);
        }
    }

    private void OnPackagesChanged(object? sender, EventArgs args) => _ = RefreshAsync(force: true);

    private sealed class PackageProgress(DlcManagerService owner, string id, CancellationTokenSource stop) : IProgress<double>
    {
        private int _last = -1;
        public void Report(double value)
        {
            if (!double.IsFinite(value)) return;
            value = Math.Clamp(value, 0, 1);
            var percent = (int)(value * 100);
            if (Interlocked.Exchange(ref _last, percent) == percent) return;
            lock (owner._sync)
            {
                if (!ReferenceEquals(owner._downloadStop, stop) || stop.IsCancellationRequested) return;
                owner._snapshots[id] = owner._snapshots[id] with { Progress = value };
            }
            owner.Changed?.Invoke(owner, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        foreach (var provider in _providers) provider.PackagesChanged -= OnPackagesChanged;
        await _lifetime.CancelAsync();
        await _operation.WaitAsync();
        _operation.Release();
        _lifetime.Dispose();
    }
}
