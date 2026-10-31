using System.Collections.Concurrent;
using System.Threading.Channels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Storage;

/// <summary>
/// Passive, bounded intake into the existing repository. A watcher never enumerates a folder,
/// and none of these paths activates a window, changes an Island module, or owns an external file.
/// </summary>
public sealed class RecentItemsService(
    IItemRepository repository,
    IFileReferenceService files,
    ISettingsService settings,
    AppStoragePaths storage,
    ILogger<RecentItemsService> logger) : IRecentItemsService
{
    private const int MaximumPending = 2_048;
    private static readonly TimeSpan StabilityInterval = TimeSpan.FromMilliseconds(900);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<FileNotice> _notices = Channel.CreateBounded<FileNotice>(new BoundedChannelOptions(MaximumPending)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Dictionary<string, DirectorySubscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingFile> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, int> _protected = new();
    private readonly Dictionary<string, DateTimeOffset> _removedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _committedOutputs = new(StringComparer.OrdinalIgnoreCase);
    private RelayDirectoryStatus[] _directorySnapshot = [];
    private RecentFileSettings _preferences = new();
    private Task? _worker;
    private bool _started;
    private bool _disposed;
    private int _overflow;
    private int _ticks;
    private bool _retentionNeeded;

    public event EventHandler? Changed;
    public IReadOnlyList<RelayDirectoryStatus> Directories => Volatile.Read(ref _directorySnapshot);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _preferences = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).RecentFiles.Validate();
            foreach (var directory in _preferences.Directories) Subscribe(directory);
            // Recover only existing waiting objects. Do not enumerate or import directory
            // contents that were created while the application was not running.
            foreach (var item in await repository.QueryAsync(new ItemQuery(Status: ItemStatus.Processing, Limit: MaximumPending), cancellationToken).ConfigureAwait(false))
                if (item.File is { } file && (item.Source == ItemSource.Recent || RecentItemMetadata.Read(item.MetadataJson) is not null))
                    _pending[file.NormalizedPath] = new PendingFile(null, item.Id);
            _started = true;
            PublishDirectories();
            _worker = Task.Run(RunAsync);
        }
        finally { _gate.Release(); }
        await ApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = RecentFilePolicy.Normalize(path);
        RecentFilePolicy.ValidateLocalDirectory(normalized);
        if (IsInternal(normalized, allowExports: false)) throw new ArgumentException("DropSpace internal directories cannot be watched.", nameof(path));
        await ChangeDirectoriesAsync(current =>
        {
            if (current.Any(value => SamePath(value.Path, normalized))) return current;
            if (current.Count >= 16) throw new InvalidOperationException("At most 16 relay directories are supported.");
            return [.. current, new RelayDirectorySetting(normalized)];
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task SetDirectoryPausedAsync(string path, bool paused, CancellationToken cancellationToken = default) =>
        ChangeDirectoriesAsync(current => current.Select(value => SamePath(value.Path, path) ? value with { IsPaused = paused } : value).ToArray(), cancellationToken);

    public Task RemoveDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        ChangeDirectoriesAsync(current => current.Where(value => !SamePath(value.Path, path)).ToArray(), cancellationToken);

    private async Task ChangeDirectoriesAsync(Func<IReadOnlyList<RelayDirectorySetting>, IReadOnlyList<RelayDirectorySetting>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var saved = await settings.UpdateAsync(current => current with
            {
                RecentFiles = current.RecentFiles with { Directories = change(current.RecentFiles.Directories) },
            }, cancellationToken).ConfigureAwait(false);
            _preferences = saved.RecentFiles;
            foreach (var entry in _subscriptions.ToArray())
            {
                var next = _preferences.Directories.FirstOrDefault(value => SamePath(value.Path, entry.Key));
                if (next is not null && next.IsPaused == entry.Value.Setting.IsPaused) continue;
                entry.Value.Watcher?.Dispose();
                _subscriptions.Remove(entry.Key);
                foreach (var key in _pending.Where(value => SamePath(value.Value.Directory, entry.Key)).Select(value => value.Key).ToArray())
                {
                    // Pause/forget stops discovery, while already admitted objects may
                    // finish their availability check without acquiring any new record.
                    var existing = await repository.FindRecentFileByPathAsync(key, cancellationToken).ConfigureAwait(false);
                    if (existing is not null) _pending[key] = new PendingFile(null, existing.Id);
                    else _pending.Remove(key);
                }
            }
            foreach (var directory in _preferences.Directories)
                if (!_subscriptions.ContainsKey(directory.Path)) Subscribe(directory);
            PublishDirectories();
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    private void Subscribe(RelayDirectorySetting setting)
    {
        var subscription = new DirectorySubscription(setting);
        _subscriptions[setting.Path] = subscription;
        if (setting.IsPaused) return;
        try
        {
            RecentFilePolicy.ValidateLocalDirectory(setting.Path);
            if (IsInternal(setting.Path, false)) throw new ArgumentException("DropSpace internal directory");
            var watcher = new FileSystemWatcher(setting.Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                InternalBufferSize = 16 * 1024,
            };
            watcher.Created += (_, args) => Queue(new FileNotice(setting.Path, args.FullPath, true, false, subscription));
            watcher.Changed += (_, args) => Queue(new FileNotice(setting.Path, args.FullPath, false, false, subscription));
            watcher.Deleted += (_, args) => Queue(new FileNotice(setting.Path, args.FullPath, false, true, subscription));
            watcher.Renamed += (_, args) =>
            {
                Queue(new FileNotice(setting.Path, args.OldFullPath, false, true, subscription));
                Queue(new FileNotice(setting.Path, args.FullPath, true, false, subscription));
            };
            watcher.Error += (_, _) => Queue(new FileNotice(setting.Path, string.Empty, false, false, subscription, "WatcherInterrupted"));
            subscription.Watcher = watcher;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            subscription.Watcher?.Dispose();
            subscription.Watcher = null;
            subscription.Reason = exception is UnauthorizedAccessException ? "AccessDenied" :
                !Directory.Exists(setting.Path) ? "DirectoryUnavailable" : "DirectoryUnsupported";
            logger.LogWarning("A selected relay directory is unavailable ({Reason}).", subscription.Reason);
        }
    }

    private void Queue(FileNotice notice)
    {
        if (!_notices.Writer.TryWrite(notice)) Interlocked.Exchange(ref _overflow, 1);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(450));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (Interlocked.Exchange(ref _overflow, 0) != 0)
                    {
                        foreach (var subscription in _subscriptions.Values.Where(value => !value.Setting.IsPaused))
                            subscription.Reason = "EventsOverflow";
                        PublishDirectories();
                        RaiseChanged();
                    }
                    var notices = 0;
                    while (notices++ < MaximumPending && _notices.Reader.TryRead(out var notice))
                        await HandleNoticeAsync(notice, _lifetime.Token).ConfigureAwait(false);
                    // Bound filesystem work per turn so a busy selected folder cannot hold
                    // user pause/remove/retain operations behind thousands of metadata probes.
                    foreach (var pair in _pending.Where(pair => pair.Value.CheckAfter <= DateTimeOffset.UtcNow)
                        .OrderBy(pair => pair.Value.CheckAfter).Take(64).ToArray())
                    {
                        if (DateTimeOffset.UtcNow < pair.Value.CheckAfter) continue;
                        await CheckPendingAsync(pair.Key, pair.Value, _lifetime.Token).ConfigureAwait(false);
                    }
                    if (++_ticks % 24 == 0) CheckDirectoryHealth();
                    if (_retentionNeeded || _ticks % 134 == 0)
                    {
                        await ApplyRetentionCoreAsync(_lifetime.Token).ConfigureAwait(false);
                        _retentionNeeded = false;
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Passive recent-file intake failed; the worker remains available.");
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task HandleNoticeAsync(FileNotice notice, CancellationToken cancellationToken)
    {
        if (!_subscriptions.TryGetValue(notice.Directory, out var subscription) || subscription.Setting.IsPaused ||
            !ReferenceEquals(subscription, notice.Subscription)) return;
        if (notice.Error is not null)
        {
            subscription.Reason = notice.Error;
            subscription.Watcher?.Dispose();
            subscription.Watcher = null;
            PublishDirectories();
            RaiseChanged();
            return;
        }
        var path = RecentFilePolicy.Normalize(notice.Path);
        if (!SamePath(Path.GetDirectoryName(path), notice.Directory) || !IsAllowedFile(path, false)) return;
        if (_committedOutputs.TryGetValue(path, out var committedAt) && notice.ObservedAt <= committedAt) return;
        if (notice.Deleted)
        {
            _pending.Remove(path);
            var missing = await repository.FindRecentFileByPathAsync(path, cancellationToken).ConfigureAwait(false);
            if (missing is not null)
            {
                await repository.UpdateFileStatusAsync(missing.Id, ItemStatus.Missing, "File no longer exists", cancellationToken).ConfigureAwait(false);
                RaiseChanged();
            }
            return;
        }
        if (_removedPaths.TryGetValue(path, out var removedAt))
        {
            if (!notice.IsNew || notice.ObservedAt <= removedAt) return;
            _removedPaths.Remove(path);
        }
        if (_pending.TryGetValue(path, out var pending))
        {
            pending.CheckAfter = DateTimeOffset.UtcNow + StabilityInterval;
            return;
        }
        // Changed is not a discovery event: modifying a file that predated subscription
        // cannot import it. Created/Renamed are the only external admission boundaries.
        var known = await repository.FindRecentFileByPathAsync(path, cancellationToken).ConfigureAwait(false);
        if (!notice.IsNew && known is null) return;
        if (_pending.Count >= MaximumPending) { subscription.Reason = "EventsOverflow"; return; }
        _pending[path] = new PendingFile(notice.Directory);
        if (known is not null) await MarkWaitingAsync(known, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckPendingAsync(string path, PendingFile pending, CancellationToken cancellationToken)
    {
        var candidate = await files.InspectAsync(path, cancellationToken).ConfigureAwait(false);
        if (candidate.EntryKind == FileEntryKind.Folder || IsReparseFile(path))
        {
            _pending.Remove(path);
            var existing = pending.ExistingId is { } id
                ? await repository.GetAsync(id, cancellationToken).ConfigureAwait(false)
                : await repository.FindRecentFileByPathAsync(path, cancellationToken).ConfigureAwait(false);
            if (existing is not null) await MarkUnavailableAsync(existing, "Entry type changed", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (candidate.Status == ItemStatus.Missing)
        {
            _pending.Remove(path);
            var existing = await repository.FindRecentFileByPathAsync(path, cancellationToken).ConfigureAwait(false);
            if (existing is not null || pending.ExistingId is not null)
                await CommitPendingCandidateAsync(candidate, pending, cancellationToken).ConfigureAwait(false);
            return;
        }
        var stable = pending.Size == candidate.KnownSize && pending.Modified == candidate.KnownModifiedAtUtc && pending.Sampled;
        if (!stable && !pending.Sampled)
        {
            var existing = await repository.FindRecentFileByPathAsync(path, cancellationToken).ConfigureAwait(false);
            stable = existing is { Status: ItemStatus.Available, File: not null } &&
                RecentItemMetadata.Read(existing.MetadataJson)?.IsOutput == true &&
                existing.File.KnownSize == candidate.KnownSize && existing.File.KnownModifiedAtUtc == candidate.KnownModifiedAtUtc;
        }
        pending.Sampled = true;
        pending.Size = candidate.KnownSize;
        pending.Modified = candidate.KnownModifiedAtUtc;
        pending.CheckAfter = DateTimeOffset.UtcNow + (pending.Attempts++ < 12 ? StabilityInterval : TimeSpan.FromSeconds(5));
        if (stable && candidate.Status == ItemStatus.Available && CanRead(path))
        {
            await CommitPendingCandidateAsync(candidate, pending, cancellationToken).ConfigureAwait(false);
            _pending.Remove(path);
        }
        else
        {
            await CommitPendingCandidateAsync(candidate with { Status = ItemStatus.Processing, AvailabilityReason = "WaitingForAvailability" },
                pending, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CommitPendingCandidateAsync(FileCandidate candidate, PendingFile pending, CancellationToken cancellationToken)
    {
        if (pending.ExistingId is not { } id)
        {
            await UpdateCandidateAsync(candidate, new(pending.Directory, null, false, false), cancellationToken).ConfigureAwait(false);
            return;
        }
        var item = await repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (item?.File is not { } reference || !SamePath(reference.NormalizedPath, candidate.NormalizedPath))
        {
            _pending.Remove(candidate.NormalizedPath);
            return;
        }
        if (item.Status == candidate.Status && reference.EntryKind == candidate.EntryKind &&
            reference.KnownSize == candidate.KnownSize && reference.KnownModifiedAtUtc == candidate.KnownModifiedAtUtc &&
            reference.AvailabilityReason == candidate.AvailabilityReason) return;
        if (await repository.TryReplaceFileReferenceAsync(id, item.Revision, reference.NormalizedPath,
            reference.EntryKind, candidate, cancellationToken).ConfigureAwait(false)) RaiseChanged();
    }

    public async Task<DropItem?> RegisterOutputAsync(string path, string operation, bool partial = false, CancellationToken cancellationToken = default)
    {
        var normalized = RecentFilePolicy.Normalize(path);
        if (!IsAllowedFile(normalized, true) || IsReparseFile(normalized)) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var candidate = await files.InspectAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (candidate.Status != ItemStatus.Available || candidate.EntryKind == FileEntryKind.Folder || !CanRead(normalized)) return null;
            _removedPaths.Remove(normalized);
            _pending.Remove(normalized);
            var checkedAt = DateTimeOffset.UtcNow;
            var directory = _subscriptions.Values.FirstOrDefault(subscription => !subscription.Setting.IsPaused &&
                SamePath(subscription.Setting.Path, Path.GetDirectoryName(normalized)))?.Setting.Path;
            var item = await UpdateCandidateAsync(candidate, new(directory, operation, true, partial), cancellationToken).ConfigureAwait(false);
            _committedOutputs[normalized] = checkedAt;
            if (_committedOutputs.Count > MaximumPending)
                _committedOutputs.Remove(_committedOutputs.MinBy(entry => entry.Value).Key);
            await ApplyRetentionCoreAsync(cancellationToken).ConfigureAwait(false);
            return item;
        }
        finally { _gate.Release(); }
    }

    private async Task<DropItem> UpdateCandidateAsync(FileCandidate candidate, RecentItemMetadata metadata, CancellationToken cancellationToken)
    {
        var previous = await repository.FindRecentFileByPathAsync(candidate.NormalizedPath, cancellationToken).ConfigureAwait(false);
        var current = await repository.UpsertRecentFileAsync(candidate, metadata, cancellationToken).ConfigureAwait(false);
        if (previous is null) _retentionNeeded = true;
        if (previous is null || previous.Revision != current.Revision) RaiseChanged();
        return current;
    }

    public async Task RefreshAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = await repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (item?.File is not { } reference) return;
            // The stability owner alone may promote a waiting path. UI/reference refreshes
            // cannot bypass its observation interval simply because a writer allows reads.
            if (_pending.ContainsKey(reference.NormalizedPath))
            {
                await MarkWaitingAsync(item, cancellationToken).ConfigureAwait(false);
                return;
            }
            var candidate = await files.InspectAsync(reference.OriginalPath, cancellationToken).ConfigureAwait(false);
            // An unavailable path has no observed entry kind. Inspect falls back to
            // File for a missing folder; retain the reference's identity so restoring
            // that folder does not subsequently look like a file-to-folder replacement.
            if (candidate.Status != ItemStatus.Available)
                candidate = candidate with { EntryKind = reference.EntryKind, Extension = reference.Extension };
            if (IsReparseFile(reference.OriginalPath) || candidate.Status == ItemStatus.Available && candidate.EntryKind != reference.EntryKind)
            {
                await MarkUnavailableAsync(item, "Entry type changed", cancellationToken).ConfigureAwait(false);
                return;
            }
            var readable = candidate.Status == ItemStatus.Available &&
                (candidate.EntryKind == FileEntryKind.Folder || CanRead(candidate.OriginalPath));
            if (item.Status == candidate.Status && reference.EntryKind == candidate.EntryKind &&
                reference.KnownSize == candidate.KnownSize && reference.KnownModifiedAtUtc == candidate.KnownModifiedAtUtc &&
                reference.AvailabilityReason == candidate.AvailabilityReason && (candidate.Status != ItemStatus.Available || readable)) return;
            if (candidate.Status == ItemStatus.Available && candidate.EntryKind != FileEntryKind.Folder)
            {
                var pending = new PendingFile(null, id)
                {
                    Sampled = true,
                    Size = candidate.KnownSize,
                    Modified = candidate.KnownModifiedAtUtc,
                    CheckAfter = DateTimeOffset.UtcNow + StabilityInterval,
                };
                _pending[reference.NormalizedPath] = pending;
                candidate = candidate with { Status = ItemStatus.Processing, AvailabilityReason = "WaitingForAvailability" };
            }
            if (await repository.TryReplaceFileReferenceAsync(id, item.Revision, reference.NormalizedPath, reference.EntryKind, candidate, cancellationToken).ConfigureAwait(false))
                RaiseChanged();
        }
        finally { _gate.Release(); }
    }

    private async Task MarkWaitingAsync(DropItem item, CancellationToken cancellationToken)
    {
        if (item.Status == ItemStatus.Processing || item.File is not { } file) return;
        if (await repository.TryUpdateFileStatusAsync(item.Id, item.Revision, file.NormalizedPath, file.EntryKind,
            ItemStatus.Processing, "WaitingForAvailability", cancellationToken).ConfigureAwait(false)) RaiseChanged();
    }

    private async Task MarkUnavailableAsync(DropItem item, string reason, CancellationToken cancellationToken)
    {
        if (item.File is not { } file || item.Status == ItemStatus.Unavailable && file.AvailabilityReason == reason) return;
        if (await repository.TryUpdateFileStatusAsync(item.Id, item.Revision, file.NormalizedPath, file.EntryKind,
            ItemStatus.Unavailable, reason, cancellationToken).ConfigureAwait(false)) RaiseChanged();
    }

    public async Task RetainAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await repository.RetainRecentAsync(id, cancellationToken).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = await repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (item?.Source != ItemSource.Recent) return;
            if (item.File is { } file)
            {
                _pending.Remove(file.NormalizedPath);
                _removedPaths[file.NormalizedPath] = DateTimeOffset.UtcNow;
                if (_removedPaths.Count > MaximumPending)
                    _removedPaths.Remove(_removedPaths.MinBy(entry => entry.Value).Key);
            }
            // Recent items are external references, never payload ownership.
            if (item.Payload is null) await repository.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            RaiseChanged();
        }
        finally { _gate.Release(); }
    }

    public IDisposable Protect(Guid id)
    {
        _protected.AddOrUpdate(id, 1, static (_, count) => count + 1);
        return new Protection(() =>
        {
            while (_protected.TryGetValue(id, out var count))
            {
                if (count == 1) { if (_protected.TryRemove(new KeyValuePair<Guid, int>(id, count))) return; }
                else if (_protected.TryUpdate(id, count - 1, count)) return;
            }
        });
    }

    public async Task ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ApplyRetentionCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task ApplyRetentionCoreAsync(CancellationToken cancellationToken)
    {
        var count = await repository.ApplyRecentRetentionAsync(DateTimeOffset.UtcNow.AddDays(-_preferences.RetentionDays),
            _preferences.CountLimit, _protected.Keys.ToArray(), cancellationToken).ConfigureAwait(false);
        if (count > 0)
        {
            // Retired waiting records must not be resurrected by their retry timer.
            foreach (var path in _pending.Keys.ToArray())
                if (_pending[path].ExistingId is null && await repository.FindRecentFileByPathAsync(path, cancellationToken).ConfigureAwait(false) is null)
                    _pending.Remove(path);
            RaiseChanged();
        }
    }

    private void CheckDirectoryHealth()
    {
        var changed = false;
        foreach (var entry in _subscriptions.ToArray())
        {
            if (entry.Value.Setting.IsPaused) continue;
            if (!Directory.Exists(entry.Key))
            {
                if (entry.Value.Reason == "DirectoryUnavailable") continue;
                entry.Value.Watcher?.Dispose();
                entry.Value.Watcher = null;
                entry.Value.Reason = "DirectoryUnavailable";
                changed = true;
            }
            else if (entry.Value.Watcher is null)
            {
                Subscribe(entry.Value.Setting);
                changed = true;
            }
        }
        if (changed) { PublishDirectories(); RaiseChanged(); }
    }

    private void PublishDirectories() => Volatile.Write(ref _directorySnapshot, _subscriptions.Values.Select(value =>
        new RelayDirectoryStatus(value.Setting.Path, value.Setting.IsPaused, value.Watcher is not null, value.Reason)).ToArray());

    private bool IsAllowedFile(string path, bool allowExports) => RecentFilePolicy.IsCandidateName(path) &&
        !RecentFilePolicy.IsInternalPackage(path) && !IsInternal(path, allowExports);

    private bool IsInternal(string path, bool allowExports) =>
        RecentFilePolicy.IsWithin(path, RecentFilePolicy.Normalize(AppContext.BaseDirectory)) ||
        RecentFilePolicy.IsWithin(path, storage.Root) && !(allowExports && RecentFilePolicy.IsWithin(path, storage.Exports));

    private static bool IsReparseFile(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length == 0 || stream.ReadByte() >= 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool SamePath(string? left, string? right) => string.Equals(
        left?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        right?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { logger.LogWarning(exception, "A recent-items observer failed."); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _subscriptions.Values) entry.Watcher?.Dispose();
            _subscriptions.Clear();
            _pending.Clear();
            _lifetime.Cancel();
            _notices.Writer.TryComplete();
        }
        finally { _gate.Release(); }
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed record FileNotice(string Directory, string Path, bool IsNew, bool Deleted, DirectorySubscription Subscription, string? Error = null)
    {
        public DateTimeOffset ObservedAt { get; } = DateTimeOffset.UtcNow;
    }
    private sealed class DirectorySubscription(RelayDirectorySetting setting)
    {
        public RelayDirectorySetting Setting { get; } = setting;
        public FileSystemWatcher? Watcher { get; set; }
        public string? Reason { get; set; }
    }
    private sealed class PendingFile(string? directory, Guid? existingId = null)
    {
        public string? Directory { get; } = directory;
        public Guid? ExistingId { get; } = existingId;
        public DateTimeOffset CheckAfter { get; set; } = DateTimeOffset.UtcNow;
        public long? Size { get; set; }
        public DateTimeOffset? Modified { get; set; }
        public bool Sampled { get; set; }
        public int Attempts { get; set; }
    }
    private sealed class Protection(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
