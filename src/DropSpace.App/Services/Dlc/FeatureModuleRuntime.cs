using System.Text.Json;
using DropSpace.Core.Dlc;
using DropSpace.Infrastructure.Dlc;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Dlc;

/// <summary>Application-owned feature lifecycle. Package IO, worker IPC and UI rendering have separate owners.</summary>
public sealed class FeatureModuleRuntime(ModulePackageStore store, OfficialModuleCatalog catalog,
    ILogger<FeatureModuleRuntime> logger) : IAsyncDisposable
{
    private sealed class Slot(ModuleInstallation installation)
    {
        public ModuleInstallation Installation = installation;
        public ModuleManifest? Manifest;
        public ModuleWorkerClient? Worker;
        public ModuleRunState State;
        public long Generation;
        public CancellationTokenSource Requests = new();
        public SemaphoreSlim Gate = new(1, 1);
        public ModuleIslandContent? Island;
        public Dictionary<CancellationTokenSource, int> Calls = [];
        public HashSet<CancellationTokenSource> Canceling = [];
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private Timer? _expiry;
    private Task? _initialization, _disposal;
    private bool _disposed;
    public event EventHandler? Changed;
    internal (int WorkerOwners, int AliveWorkers, int ActiveCalls, bool HasExpiryTimer, bool Disposed) DiagnosticState
    {
        get { lock (_sync) return (_slots.Values.Count(s => s.Worker is not null),
            _slots.Values.Count(s => s.Worker?.IsAlive == true), _slots.Values.Sum(s => s.Calls.Values.Sum()), _expiry is not null, _disposed); }
    }
    public IReadOnlyList<OfficialModulePackage> AvailablePackages => catalog.Packages.GroupBy(p => p.Id)
        .Select(group => group.MaxBy(p => Version.Parse(p.Version))!).ToArray();
    public OfficialModulePackage? GetPackage(string id) => AvailablePackages.FirstOrDefault(package => package.Id == id);
    public async Task RefreshCatalogAsync(CancellationToken token)
    {
        await catalog.RefreshAsync(token).ConfigureAwait(false);
        Notify();
    }
    public IReadOnlyList<ModuleSnapshot> Snapshots
    {
        get
        {
            lock (_sync) return AvailablePackages.Select(package => _slots.TryGetValue(package.Id, out var slot)
                ? new ModuleSnapshot(slot.Installation, slot.State, slot.Manifest)
                : new ModuleSnapshot(new(package.Id, null, false, 0), ModuleRunState.Stopped, null))
                .Concat(_slots.Where(pair => !catalog.Packages.Any(p => p.Id == pair.Key))
                    .Select(pair => new ModuleSnapshot(pair.Value.Installation, pair.Value.State, pair.Value.Manifest))).ToArray();
        }
    }
    public IReadOnlyList<(string Id, ModuleManifest Manifest, ModuleIslandContent Content)> IslandContents
    {
        get
        {
            lock (_sync) return _slots.Where(pair => pair.Value is { State: ModuleRunState.Running, Manifest: not null, Island: not null } &&
                pair.Value.Installation.Enabled && pair.Value.Island.ExpiresAt > DateTimeOffset.UtcNow)
                .Select(pair => (pair.Key, pair.Value.Manifest!, pair.Value.Island!)).ToArray();
        }
    }
    private void Notify()
    {
        foreach (EventHandler handler in Changed?.GetInvocationList() ?? [])
            try { handler(this, EventArgs.Empty); }
            catch (Exception error) when (error is not OutOfMemoryException) { logger.LogWarning("Module UI subscriber failed: {Category}", error.GetType().Name); }
    }
    private Slot GetSlot(string id)
    {
        if (!ModuleContract.ValidId(id)) throw new InvalidDataException("ModuleNotOfficial");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_slots.TryGetValue(id, out var slot))
            {
                if (!AvailablePackages.Any(p => p.Id == id)) throw new InvalidDataException("ModuleNotOfficial");
                _slots.Add(id, slot = new(new(id, null, false, 0)));
            }
            return slot;
        }
    }
    public Task InitializeAsync()
    {
        lock (_sync) return _initialization ??= Task.Run(InitializeCoreAsync);
    }
    private async Task InitializeCoreAsync()
    {
        // Missing Modules state is a read-only fast path: no directory, timer or empty process.
        foreach (var entry in await store.LoadAsync(_lifetime.Token).ConfigureAwait(false))
        {
            Slot slot;
            lock (_sync) { _slots[entry.Id] = slot = new(entry); }
            try { await RunAsync(slot, async () =>
            {
                var current = entry;
                if (entry.Transaction is ModuleTransactionState.Downloading or ModuleTransactionState.Preparing or ModuleTransactionState.Activating)
                {
                    // The journal still points at the old committed version until handshake and data policy pass.
                    if (entry.CandidateVersion is { } candidate && candidate != entry.Version)
                        if (!await store.RemoveVersionAsync(entry.Id, candidate).ConfigureAwait(false))
                            current = entry with { Transaction = ModuleTransactionState.PendingCleanup, ErrorCode = "ModuleCleanupPending" };
                    if (current.Transaction != ModuleTransactionState.PendingCleanup)
                        current = entry with { Transaction = ModuleTransactionState.None, CandidateVersion = null, PreviousVersion = null, ErrorCode = "ModuleInterrupted" };
                    await store.WriteAsync(current, _lifetime.Token).ConfigureAwait(false);
                }
                if (current.Transaction is ModuleTransactionState.Cleaning or ModuleTransactionState.PendingCleanup)
                {
                    // The candidate is retired/failed; the committed current version is never a cleanup fallback.
                    var cleanup = current.CandidateVersion;
                    if (cleanup is null || await store.RemoveVersionAsync(entry.Id, cleanup).ConfigureAwait(false))
                    {
                        current = current with { Transaction = ModuleTransactionState.None, CandidateVersion = null, PreviousVersion = null, ErrorCode = null };
                        await store.WriteAsync(current, _lifetime.Token).ConfigureAwait(false);
                    }
                }
                if (!await store.RemoveStagingAsync(entry.Id).ConfigureAwait(false))
                {
                    current = current with { Transaction = ModuleTransactionState.PendingCleanup, ErrorCode = "ModuleCleanupPending" };
                    await store.WriteAsync(current, _lifetime.Token).ConfigureAwait(false);
                }
                lock (_sync) slot.Installation = current;
                if (current.Version is not null)
                {
                    var manifest = await store.ReadManifestAsync(current.Id, current.Version, _lifetime.Token).ConfigureAwait(false);
                    ValidateManifest(manifest, current.DataVersion);
                    lock (_sync) slot.Manifest = manifest;
                    if (current.Enabled) await ActivateAsync(slot, manifest, slot.Generation).ConfigureAwait(false);
                }
            }).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                lock (_sync) slot.State = ModuleRunState.Faulted;
                logger.LogWarning("Feature module recovery isolated: {Category}", error.GetType().Name);
            }
        }
        Notify();
    }
    private void ValidateManifest(ModuleManifest manifest, int dataVersion)
    {
        Dictionary<string, ModuleInstallation> installed;
        lock (_sync) installed = _slots.ToDictionary(pair => pair.Key, pair => pair.Value.Installation);
        ModuleContract.Validate(manifest, installed, Environment.OSVersion.Version.Build);
        if (dataVersion > 0 && (dataVersion < manifest.Data.MinimumReadableVersion || dataVersion > manifest.Data.MaximumReadableVersion ||
            dataVersion != manifest.Data.Version)) throw new InvalidDataException("ModuleDataMigrationUnsupported");
    }
    public Task InstallAsync(string id)
    {
        var slot = GetSlot(id);
        return RunAsync(slot, async () =>
        {
            var package = GetPackage(id) ?? throw new IOException("ModuleCatalogUnavailable");
            var old = slot.Installation;
            var oldManifest = slot.Manifest;
            if (old.Version is { } installedVersion && Version.Parse(package.Version) < Version.Parse(installedVersion))
                throw new InvalidDataException("ModuleDowngradeRejected");
            if (old.Version == package.Version && old.Transaction == ModuleTransactionState.None)
            { if (!old.Enabled || slot.State != ModuleRunState.Running) await EnableCoreAsync(slot).ConfigureAwait(false); return; }
            if (old.Transaction is ModuleTransactionState.Cleaning or ModuleTransactionState.PendingCleanup)
                throw new IOException("ModuleCleanupPending");
            long generation;
            lock (_sync) generation = slot.Generation;
            var transaction = old with { Transaction = ModuleTransactionState.Downloading, CandidateVersion = package.Version,
                PreviousVersion = old.Version, ErrorCode = null };
            await PublishInstallationAsync(slot, transaction).ConfigureAwait(false);
            var switched = false;
            ModuleManifest? candidateManifest = null;
            try
            {
                Dictionary<string, ModuleInstallation> installed;
                lock (_sync) installed = _slots.ToDictionary(pair => pair.Key, pair => pair.Value.Installation);
                ModuleManifest manifest;
                using (var preparation = BeginCall(slot, _lifetime.Token, generation))
                    manifest = await store.PrepareAsync(package, installed, preparation.Token).ConfigureAwait(false);
                ValidateManifest(manifest, old.DataVersion);
                candidateManifest = manifest;
                if (!IsCurrent(slot, generation)) throw new OperationCanceledException("ModuleOperationRetired");
                generation = Retire(slot);
                switched = true;
                if (!await StopWorkerAsync(slot).ConfigureAwait(false)) throw new IOException("ModuleStopUnconfirmed");
                await PublishInstallationAsync(slot, transaction with { Transaction = ModuleTransactionState.Activating,
                    DataVersion = manifest.Data.Version }).ConfigureAwait(false);
                await ActivateAsync(slot, manifest, generation).ConfigureAwait(false);
                if (!IsCurrent(slot, generation)) throw new OperationCanceledException("ModuleOperationRetired");
                // Commit only after handshake. First-version migrations cannot change formats, so old-data rollback remains valid.
                await PublishInstallationAsync(slot, new(id, package.Version, true, manifest.Data.Version,
                    ModuleTransactionState.Cleaning, CandidateVersion: old.Version)).ConfigureAwait(false);
                lock (_sync) slot.Manifest = manifest;
                Notify();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                var superseded = !IsCurrent(slot, generation);
                if (switched) Retire(slot);
                var stopped = !switched || await StopWorkerAsync(slot).ConfigureAwait(false);
                var clean = stopped && await store.RemoveVersionAsync(id, package.Version).ConfigureAwait(false);
                var stagingClean = await store.RemoveStagingAsync(id).ConfigureAwait(false);
                var rollback = old with { ErrorCode = error.Message.StartsWith("HostInterfaceRequired:", StringComparison.Ordinal) ? error.Message : "ModuleActivationFailed",
                    Transaction = clean && stagingClean ? ModuleTransactionState.None : ModuleTransactionState.PendingCleanup,
                    DataVersion = switched ? Math.Max(old.DataVersion, candidateManifest?.Data.Version ?? 0) : old.DataVersion,
                    CandidateVersion = clean ? null : package.Version };
                await PublishInstallationAsync(slot, rollback).ConfigureAwait(false);
                lock (_sync) slot.Manifest = oldManifest;
                if (switched && old.Version is { } version && old.Enabled && stopped && !superseded)
                {
                    var previous = await store.ReadManifestAsync(id, version, _lifetime.Token).ConfigureAwait(false);
                    ValidateManifest(previous, old.DataVersion);
                    await ActivateAsync(slot, previous, slot.Generation).ConfigureAwait(false);
                }
                throw;
            }
            // The new version is now committed. Cleanup failure must never enter the activation rollback path.
            var cleanupVersion = old.Version is { } oldVersion && oldVersion != package.Version ? oldVersion : null;
            var cleanup = slot.Installation with { Transaction = ModuleTransactionState.Cleaning, CandidateVersion = cleanupVersion };
            await PublishInstallationAsync(slot, cleanup).ConfigureAwait(false);
            var completed = (cleanupVersion is null || await store.RemoveVersionAsync(id, cleanupVersion).ConfigureAwait(false)) &&
                await store.RemoveStagingAsync(id).ConfigureAwait(false);
            await PublishInstallationAsync(slot, cleanup with { Transaction = completed ? ModuleTransactionState.None : ModuleTransactionState.PendingCleanup,
                CandidateVersion = completed ? null : cleanupVersion, ErrorCode = completed ? null : "ModuleCleanupPending" }).ConfigureAwait(false);
        });
    }
    public Task EnableAsync(string id) { var slot = GetSlot(id); return RunAsync(slot, () => EnableCoreAsync(slot)); }
    private async Task EnableCoreAsync(Slot slot)
    {
        if (slot.Installation.Version is not { } version) throw new IOException("ModuleNotInstalled");
        if (slot.State == ModuleRunState.Running && slot.Installation.Enabled) return;
        if (slot.Installation.Transaction is not (ModuleTransactionState.None or ModuleTransactionState.PendingCleanup)) throw new IOException("ModuleCleanupPending");
        var generation = Retire(slot);
        if (!await StopWorkerAsync(slot).ConfigureAwait(false)) throw new IOException("ModuleStopUnconfirmed");
        var manifest = await store.ReadManifestAsync(slot.Installation.Id, version, _lifetime.Token).ConfigureAwait(false);
        ValidateManifest(manifest, slot.Installation.DataVersion);
        await ActivateAsync(slot, manifest, generation).ConfigureAwait(false);
        await PublishInstallationAsync(slot, slot.Installation with { Enabled = true, ErrorCode = null }).ConfigureAwait(false);
        Notify();
    }
    public Task DisableAsync(string id)
    {
        var slot = GetSlot(id);
        Retire(slot); // synchronously withdraw and cancel before any async wait
        return RunAsync(slot, async () =>
        {
            await PublishInstallationAsync(slot, slot.Installation with { Enabled = false }).ConfigureAwait(false);
            await StopWorkerAsync(slot).ConfigureAwait(false);
        });
    }
    public Task UninstallAsync(string id)
    {
        var slot = GetSlot(id);
        lock (_sync)
            if (_slots.Values.Any(other => other.Installation.Enabled && other.Manifest?.Dependencies.Any(d => d.Id == id) == true))
                throw new InvalidOperationException("ModuleDependencyInUse");
        Retire(slot);
        return RunAsync(slot, async () =>
        {
            // A dependent module must be disabled explicitly before removing its dependency.
            lock (_sync)
                if (_slots.Values.Any(other => other.Installation.Enabled && other.Manifest?.Dependencies.Any(d => d.Id == id) == true))
                    throw new InvalidOperationException("ModuleDependencyInUse");
            var version = slot.Installation.Version;
            await PublishInstallationAsync(slot, slot.Installation with { Enabled = false }).ConfigureAwait(false);
            var stopped = await StopWorkerAsync(slot).ConfigureAwait(false);
            if (!stopped) throw new IOException("ModuleStopUnconfirmed");
            // Keep any earlier cleanup obligation until it really completes; do not overwrite its journal.
            if (slot.Installation.CandidateVersion is { } prior && prior != version &&
                !await store.RemoveVersionAsync(id, prior).ConfigureAwait(false))
                throw new IOException("ModuleCleanupPending");
            await PublishInstallationAsync(slot, slot.Installation with { Transaction = ModuleTransactionState.Cleaning, CandidateVersion = version }).ConfigureAwait(false);
            var clean = stopped && (version is null || await store.RemoveVersionAsync(id, version).ConfigureAwait(false));
            clean = await store.RemoveStagingAsync(id).ConfigureAwait(false) && clean;
            await PublishInstallationAsync(slot, slot.Installation with { Version = null, Enabled = false,
                Transaction = clean ? ModuleTransactionState.None : ModuleTransactionState.PendingCleanup,
                CandidateVersion = clean ? null : version, ErrorCode = clean ? null : "ModuleCleanupPending" }).ConfigureAwait(false);
            if (clean) lock (_sync) slot.Manifest = null;
        });
    }
    private bool IsCurrent(Slot slot, long generation) { lock (_sync) return !_disposed && slot.Generation == generation; }
    private long Retire(Slot slot)
    {
        CancellationTokenSource previous;
        long generation;
        lock (_sync)
        {
            previous = slot.Requests; slot.Canceling.Add(previous);
            slot.Requests = new(); slot.Island = null;
            generation = ++slot.Generation;
            if (slot.State is ModuleRunState.Running or ModuleRunState.Starting) slot.State = ModuleRunState.Stopping;
            ScheduleExpiry();
        }
        try { previous.Cancel(); } // no user callback while the runtime lock is held
        finally
        {
            lock (_sync)
            {
                slot.Canceling.Remove(previous);
                if (!slot.Calls.ContainsKey(previous)) previous.Dispose();
            }
        }
        Notify();
        return generation;
    }
    private async Task ActivateAsync(Slot slot, ModuleManifest manifest, long generation)
    {
        lock (_sync)
        {
            if (!IsCurrent(slot, generation)) throw new OperationCanceledException("ModuleOperationRetired");
            slot.State = ModuleRunState.Starting; slot.Manifest = manifest;
        }
        Notify();
        using var token = BeginCall(slot, _lifetime.Token, generation);
        var worker = await ModuleWorkerClient.StartAsync(Path.Combine(store.GetVersionDirectory(manifest.Id, manifest.Version), manifest.EntryPoint),
            store.GetDataDirectory(manifest.Id), manifest, token.Token).ConfigureAwait(false);
        try
        {
            var settings = await store.ReadSettingsAsync(manifest.Id, token.Token).ConfigureAwait(false);
            var declared = manifest.Ui.Settings.ToDictionary(s => s.Id, s => settings.GetValueOrDefault(s.Id, s.DefaultValue));
            if (manifest.Ui.Settings.Any(s => declared[s.Id].Length > 4096 || s.Kind == "boolean" && declared[s.Id] is not ("true" or "false")))
                throw new InvalidDataException("ModuleSettingInvalid");
            if (declared.Count > 0) await worker.RequestAsync("settings", JsonSerializer.SerializeToElement(declared), token.Token).ConfigureAwait(false);
        }
        catch { await worker.DisposeAsync().ConfigureAwait(false); throw; }
        lock (_sync)
        {
            if (IsCurrent(slot, generation)) { slot.Worker = worker; slot.State = ModuleRunState.Running; }
        }
        if (!IsCurrent(slot, generation)) { await worker.DisposeAsync().ConfigureAwait(false); throw new OperationCanceledException("ModuleOperationRetired"); }
        worker.Exited += (_, _) => WorkerExited(slot, worker, generation);
        if (!worker.IsAlive) WorkerExited(slot, worker, generation);
        // No entry is visible until committed Enabled state and a live Running generation coincide.
    }
    private void WorkerExited(Slot slot, ModuleWorkerClient worker, long generation)
    {
        lock (_sync)
        {
            if (slot.Worker != worker || slot.Generation != generation || _disposed) return;
            slot.State = ModuleRunState.Faulted; slot.Island = null;
            slot.Installation = slot.Installation with { ErrorCode = "ModuleCrashed" };
            ScheduleExpiry();
        }
        Notify(); // explicit enable/retry required; no automatic restart/backoff loop
    }
    private async Task<bool> StopWorkerAsync(Slot slot)
    {
        ModuleWorkerClient? worker;
        lock (_sync) { worker = slot.Worker; if (worker is not null) slot.State = ModuleRunState.Stopping; }
        if (worker is not null && !await worker.StopAsync().ConfigureAwait(false))
        { lock (_sync) slot.State = ModuleRunState.Faulted; return false; }
        if (worker is not null) await worker.DisposeAsync().ConfigureAwait(false);
        lock (_sync) { slot.Worker = null; slot.State = ModuleRunState.Stopped; }
        Notify(); return true;
    }
    private async Task PublishInstallationAsync(Slot slot, ModuleInstallation installation)
    {
        await store.WriteAsync(installation, _lifetime.Token).ConfigureAwait(false);
        lock (_sync) slot.Installation = installation;
        Notify();
    }
    private Task RunAsync(Slot slot, Func<Task> operation) => Task.Run(async () =>
    {
        await slot.Gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); await operation().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("Feature module {Id} operation failed: {Category}", slot.Installation.Id, error.GetType().Name);
            lock (_sync) slot.Installation = slot.Installation with { ErrorCode =
                error.Message.Length <= 256 && (error.Message.StartsWith("Module", StringComparison.Ordinal) ||
                    error.Message.StartsWith("HostInterfaceRequired:", StringComparison.Ordinal) ||
                    error.Message.StartsWith("RequiredCapabilityMissing:", StringComparison.Ordinal)) ? error.Message : "ModuleOperationFailed" };
            Notify(); throw;
        }
        finally { slot.Gate.Release(); }
    });
    public async Task<ModuleReply> InvokeAsync(string id, string action, JsonElement payload, CancellationToken token)
    {
        var slot = GetSlot(id);
        ModuleWorkerClient worker; long generation;
        lock (_sync)
        {
            if (!slot.Installation.Enabled || slot.State != ModuleRunState.Running || slot.Worker is null ||
                slot.Manifest is null || !slot.Manifest.Ui.Pages.SelectMany(p => p.Actions).Any(a => a.Id == action))
                throw new IOException("ModuleActionUnavailable");
            worker = slot.Worker; generation = slot.Generation;
        }
        using var stop = BeginCall(slot, token, generation);
        var result = await worker.RequestAsync("action", JsonSerializer.SerializeToElement(new { action, input = payload }), stop.Token).ConfigureAwait(false);
        lock (_sync)
        {
            if (!IsCurrent(slot, generation) || slot.State != ModuleRunState.Running) throw new OperationCanceledException("ModuleResultExpired");
            if (result.Island is { } island)
            {
                if (!ModuleContract.HasCapability(slot.Manifest!, "island.content") || island.ExpiresAt <= DateTimeOffset.UtcNow ||
                    island.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1) || island.Actions.Length > 4 ||
                    island.Actions.Any(a => !slot.Manifest!.Ui.Pages.SelectMany(p => p.Actions).Any(allowed => allowed.Id == a.Id && allowed.Label == a.Label)) ||
                    !slot.Manifest!.Resources["en-US"].ContainsKey(island.Compact.Key) || !slot.Manifest.Resources["en-US"].ContainsKey(island.Expanded.Key))
                    throw new InvalidDataException("ModuleIslandInvalid");
                slot.Island = island; ScheduleExpiry();
            }
        }
        Notify(); return result;
    }
    private void ScheduleExpiry()
    {
        var deadline = _slots.Values.Where(s => s.Island is not null).Select(s => s.Island!.ExpiresAt).DefaultIfEmpty().Min();
        if (deadline == default) { _expiry?.Dispose(); _expiry = null; return; }
        _expiry ??= new Timer(_ => Expire(), null, Timeout.Infinite, Timeout.Infinite);
        _expiry.Change(TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds)), Timeout.InfiniteTimeSpan);
    }
    private void Expire()
    {
        lock (_sync)
        {
            foreach (var slot in _slots.Values) if (slot.Island?.ExpiresAt <= DateTimeOffset.UtcNow) slot.Island = null;
            ScheduleExpiry();
        }
        Notify();
    }
    public Task<Dictionary<string, string>> ReadSettingsAsync(string id, CancellationToken token) => store.ReadSettingsAsync(id, token);
    public async Task WriteSettingAsync(string id, string key, string value, CancellationToken token)
    {
        var slot = GetSlot(id);
        await slot.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ModuleWorkerClient worker; long generation; ModuleSetting descriptor;
            lock (_sync)
            {
                if (slot.State != ModuleRunState.Running || !slot.Installation.Enabled || slot.Worker is null) throw new IOException("ModuleNotRunning");
                descriptor = slot.Manifest!.Ui.Settings.Single(s => s.Id == key);
                worker = slot.Worker; generation = slot.Generation;
            }
            if (value.Length > 4096 || descriptor.Kind == "boolean" && value is not ("true" or "false")) throw new InvalidDataException("ModuleSettingInvalid");
            using var stop = BeginCall(slot, token, generation);
            var settings = await store.ReadSettingsAsync(id, stop.Token).ConfigureAwait(false);
            var values = slot.Manifest!.Ui.Settings.ToDictionary(s => s.Id, s => settings.GetValueOrDefault(s.Id, s.DefaultValue));
            values[key] = value;
            await worker.RequestAsync("settings", JsonSerializer.SerializeToElement(values), stop.Token).ConfigureAwait(false);
            if (!IsCurrent(slot, generation)) throw new OperationCanceledException("ModuleResultExpired");
            try { await store.WriteSettingsAsync(id, values, stop.Token).ConfigureAwait(false); }
            catch
            {
                // Acknowledged settings must not diverge from the durable preference after a failed write.
                Retire(slot);
                await StopWorkerAsync(slot).ConfigureAwait(false);
                throw;
            }
        }
        finally { slot.Gate.Release(); }
    }
    public Task ClearDataAsync(string id)
    {
        var slot = GetSlot(id);
        return RunAsync(slot, async () =>
        {
            if (slot.Installation.Enabled || slot.Worker is not null) throw new IOException("ModuleDisableBeforeDataRemoval");
            await store.ClearDataAsync(id).ConfigureAwait(false);
            await PublishInstallationAsync(slot, slot.Installation with { DataVersion = slot.Manifest?.Data.Version ?? 0, ErrorCode = null }).ConfigureAwait(false);
        });
    }
    public Task RetryCleanupAsync(string id)
    {
        var slot = GetSlot(id);
        return RunAsync(slot, async () =>
        {
            var current = slot.Installation;
            if (current.Transaction != ModuleTransactionState.PendingCleanup) return;
            var version = current.CandidateVersion;
            // A retired old package can be cleaned while the committed current worker keeps running.
            var runningCurrent = slot.State == ModuleRunState.Running && current.Version is not null && current.Version != version;
            if (!runningCurrent && !await StopWorkerAsync(slot).ConfigureAwait(false) ||
                version is not null && !await store.RemoveVersionAsync(id, version).ConfigureAwait(false))
                throw new IOException("ModuleCleanupPending");
            if (!await store.RemoveStagingAsync(id).ConfigureAwait(false)) throw new IOException("ModuleCleanupPending");
            await PublishInstallationAsync(slot, current with { Transaction = ModuleTransactionState.None, CandidateVersion = null,
                PreviousVersion = null, ErrorCode = null }).ConfigureAwait(false);
            if (!runningCurrent && current.Enabled && current.Version is not null)
                await EnableCoreAsync(slot).ConfigureAwait(false);
        });
    }
    private CallLifetime BeginCall(Slot slot, CancellationToken token, long expectedGeneration)
    {
        lock (_sync)
        {
            if (slot.Generation != expectedGeneration) throw new OperationCanceledException("ModuleOperationRetired");
            if (_disposed || slot.Calls.Values.Sum() >= 8) throw new IOException("ModuleRequestUnavailable");
            var source = slot.Requests;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, source.Token, _lifetime.Token);
            slot.Calls[source] = slot.Calls.GetValueOrDefault(source) + 1;
            return new(this, slot, source, linked);
        }
    }
    private sealed class CallLifetime(FeatureModuleRuntime runtime, Slot slot, CancellationTokenSource source, CancellationTokenSource linked) : IDisposable
    {
        public CancellationToken Token => linked.Token;
        public void Dispose()
        {
            linked.Dispose();
            lock (runtime._sync)
            {
                if (--slot.Calls[source] == 0)
                {
                    slot.Calls.Remove(source);
                    if ((source != slot.Requests || runtime._disposed) && !slot.Canceling.Contains(source)) source.Dispose();
                }
            }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposal ??= Task.Run(DisposeCoreAsync));
    }
    private async Task DisposeCoreAsync()
    {
        Slot[] slots;
        lock (_sync) { _disposed = true; slots = _slots.Values.ToArray(); _expiry?.Dispose(); _expiry = null; }
        foreach (var slot in slots) Retire(slot);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var slot in slots)
        {
            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try { await StopWorkerAsync(slot).ConfigureAwait(false); } finally { slot.Gate.Release(); }
        }
    }
}
