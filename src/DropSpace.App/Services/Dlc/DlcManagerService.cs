using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Dlc;

public enum DlcPackageState { Checking, Available, Installed, Downloading, Canceling, Removing, Failed }
public enum DlcPackageAction { Inspect, Download, Delete }
public sealed record DlcPackageSnapshot(DlcPackageDescriptor Package, DlcPackageInspection? Installation,
    DlcPackageState State, double? Progress = null, DlcPackageAction? FailedAction = null, bool WasCanceled = false);

/// <summary>Application-lifetime transient state. Provider inspection is the persistent source of truth.</summary>
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

    public DlcManagerService(IEnumerable<IDlcPackageProvider> providers, ILogger<DlcManagerService> logger)
    {
        _providers = providers.ToArray();
        _logger = logger;
        foreach (var provider in _providers)
        {
            provider.PackagesChanged += OnPackagesChanged;
            foreach (var package in provider.Packages)
                _snapshots.Add(package.Id, new(package, null, DlcPackageState.Checking));
        }
    }

    public event EventHandler? Changed;
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

    public async Task RefreshAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _refreshPending = true;
        }
        if (!await _operation.WaitAsync(0)) return;
        try
        {
            lock (_sync) { if (_disposed) return; _activeId = string.Empty; }
            do
            {
                lock (_sync) _refreshPending = false;
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
                    Publish(new(package, previous?.Installation, DlcPackageState.Checking));
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
                lock (_sync) { if (!_refreshPending) break; }
            } while (true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            bool refresh;
            lock (_sync) { _activeId = null; refresh = _refreshPending && !_disposed; }
            _operation.Release();
            Changed?.Invoke(this, EventArgs.Empty);
            if (refresh) await RefreshAsync();
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
            stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            if (action == DlcPackageAction.Download)
            {
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
            if (refresh) await RefreshAsync();
        }
    }

    private void OnPackagesChanged(object? sender, EventArgs args) => _ = RefreshAsync();

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
