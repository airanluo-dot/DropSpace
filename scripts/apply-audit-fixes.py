from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    (ROOT / path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, found {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))


def insert_before(path: str, marker: str, block: str) -> None:
    replace_once(path, marker, block + marker)


def append_once(path: str, marker: str, block: str) -> None:
    text = read(path)
    if block in text:
        return
    if marker not in text:
        raise RuntimeError(f"{path}: marker missing")
    write(path, text.replace(marker, marker + block, 1))


# AUD-001/AUD-020/AUD-021: retention selection and destructive commit share the
# same write owner, final deletion re-validates pin/source state, and the result
# reports actual affected rows rather than requested IDs.
replace_once(
    "src/DropSpace.Infrastructure/Data/SqliteItemRepository.cs",
    '''    public async Task<RetentionResult> ApplyRetentionAsync(
        DateTimeOffset ageCutoffUtc,
        int countLimit,
        CancellationToken cancellationToken = default)
    {
        var items = await QueryAsync(
                new ItemQuery(Source: ItemSource.Clipboard, Limit: 100_000),
                cancellationToken)
            .ConfigureAwait(false);
        var ids = RetentionPolicy.SelectExpired(items, ageCutoffUtc, countLimit);
        if (ids.Count == 0)
        {
            return new RetentionResult(0, Array.Empty<string>());
        }

        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var removed = await RemoveManyCoreAsync(connection, ids, cancellationToken).ConfigureAwait(false);
            return new RetentionResult(removed.RemovedCount, removed.PayloadPaths);
        }
        finally
        {
            database.WriteGate.Release();
        }
    }
''',
    '''    public async Task<RetentionResult> ApplyRetentionAsync(
        DateTimeOffset ageCutoffUtc,
        int countLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countLimit);
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var ids = new List<Guid>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    WITH ranked AS (
                        SELECT id, created_at_utc,
                               ROW_NUMBER() OVER (ORDER BY created_at_utc DESC, id DESC) AS retention_rank
                        FROM items
                        WHERE source = @source
                          AND is_pinned = 0
                          AND pending_delete_token IS NULL
                    )
                    SELECT id
                    FROM ranked
                    WHERE created_at_utc < @cutoff OR retention_rank > @count_limit;
                    """;
                command.Parameters.AddWithValue("@source", (int)ItemSource.Clipboard);
                command.Parameters.AddWithValue("@cutoff", ToTimestamp(ageCutoffUtc));
                command.Parameters.AddWithValue("@count_limit", countLimit);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(ReadGuid(reader, 0));
                }
            }

            if (ids.Count == 0)
            {
                return new RetentionResult(0, Array.Empty<string>());
            }

            // Re-check source/pin/pending state in the actual DELETE as a second line of
            // defense. This keeps the retention invariant correct even if a future writer
            // accidentally bypasses the shared repository write gate.
            return await RemoveManyCoreAsync(
                    connection,
                    ids,
                    cancellationToken,
                    protectPinnedClipboard: true)
                .ConfigureAwait(false);
        }
        finally
        {
            database.WriteGate.Release();
        }
    }
''')

replace_once(
    "src/DropSpace.Infrastructure/Data/SqliteItemRepository.cs",
    '''    private static async Task<RetentionResult> RemoveManyCoreAsync(
        SqliteConnection connection,
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
''',
    '''    private static async Task<RetentionResult> RemoveManyCoreAsync(
        SqliteConnection connection,
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken,
        bool protectPinnedClipboard = false)
''')

replace_once(
    "src/DropSpace.Infrastructure/Data/SqliteItemRepository.cs",
    '''        var payloads = new List<(Guid Id, string Path)>();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
''',
    '''        var payloads = new List<(Guid Id, string Path)>();
        var deletedPayloadPaths = new List<string>();
        var removedCount = 0;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
''')

replace_once(
    "src/DropSpace.Infrastructure/Data/SqliteItemRepository.cs",
    '''            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM items WHERE id = @id;";
            delete.Parameters.AddWithValue("@id", ToBytes(id));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
''',
    '''            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = protectPinnedClipboard
                ? "DELETE FROM items WHERE id = @id AND source = @source AND is_pinned = 0 AND pending_delete_token IS NULL;"
                : "DELETE FROM items WHERE id = @id;";
            delete.Parameters.AddWithValue("@id", ToBytes(id));
            if (protectPinnedClipboard)
            {
                delete.Parameters.AddWithValue("@source", (int)ItemSource.Clipboard);
            }
            removedCount += await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
''')

replace_once(
    "src/DropSpace.Infrastructure/Data/SqliteItemRepository.cs",
    '''        foreach (var payload in payloads)
        {
            await using var deletePayload = connection.CreateCommand();
            deletePayload.Transaction = (SqliteTransaction)transaction;
            deletePayload.CommandText = "DELETE FROM payloads WHERE id = @id;";
            deletePayload.Parameters.AddWithValue("@id", ToBytes(payload.Id));
            await deletePayload.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RetentionResult(ids.Count, payloads.Select(payload => payload.Path).ToArray());
''',
    '''        foreach (var payload in payloads.DistinctBy(payload => payload.Id))
        {
            await using var deletePayload = connection.CreateCommand();
            deletePayload.Transaction = (SqliteTransaction)transaction;
            deletePayload.CommandText = "DELETE FROM payloads WHERE id = @id AND NOT EXISTS (SELECT 1 FROM items WHERE payload_id = @id);";
            deletePayload.Parameters.AddWithValue("@id", ToBytes(payload.Id));
            if (await deletePayload.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                deletedPayloadPaths.Add(payload.Path);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RetentionResult(
            removedCount,
            deletedPayloadPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
''')

# AUD-013: migration backup is no longer synchronous on the caller/UI thread.
replace_once(
    "src/DropSpace.Infrastructure/Data/SqliteDatabase.cs",
    '''        if (fromVersion > 0)
        {
            CreateBackup(connection, fromVersion);
        }
''',
    '''        if (fromVersion > 0)
        {
            await Task.Run(() => CreateBackup(connection, fromVersion), cancellationToken).ConfigureAwait(false);
        }
''')

# AUD-003: one shared reparse-aware confinement policy for staging and remote finalization.
write(
    "src/DropSpace.Infrastructure/Storage/ReparseSafePathPolicy.cs",
    '''using DropSpace.Core.Policies;

namespace DropSpace.Infrastructure.Storage;

public static class ReparseSafePathPolicy
{
    public static string ResolveExistingContainedPath(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
        var candidate = PayloadPathPolicy.ResolveContainedPath(fullRoot, relative);
        EnsureExistingPathDoesNotTraverseReparsePoints(fullRoot, candidate);
        return candidate;
    }

    public static string PrepareContainedFileDestination(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        EnsureNotReparsePoint(fullRoot);

        var candidate = PayloadPathPolicy.ResolveContainedPath(fullRoot, relativePath);
        var parent = Path.GetDirectoryName(candidate)
            ?? throw new InvalidDataException("The destination has no parent directory.");
        var parentRelative = Path.GetRelativePath(fullRoot, parent);
        if (!string.Equals(parentRelative, ".", StringComparison.Ordinal))
        {
            var current = fullRoot;
            foreach (var segment in parentRelative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (Directory.Exists(current))
                {
                    EnsureNotReparsePoint(current);
                    continue;
                }

                Directory.CreateDirectory(current);
                EnsureNotReparsePoint(current);
            }
        }

        EnsureExistingPathDoesNotTraverseReparsePoints(fullRoot, parent);
        if (File.Exists(candidate) || Directory.Exists(candidate))
        {
            EnsureNotReparsePoint(candidate);
        }
        return candidate;
    }

    public static void RevalidatePreparedDestination(string root, string destination)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullDestination = Path.GetFullPath(destination);
        _ = PayloadPathPolicy.ResolveContainedPath(fullRoot, Path.GetRelativePath(fullRoot, fullDestination));
        var parent = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidDataException("The destination has no parent directory.");
        EnsureExistingPathDoesNotTraverseReparsePoints(fullRoot, parent);
        if (File.Exists(fullDestination) || Directory.Exists(fullDestination))
        {
            EnsureNotReparsePoint(fullDestination);
        }
    }

    private static void EnsureExistingPathDoesNotTraverseReparsePoints(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(path);
        while (true)
        {
            EnsureNotReparsePoint(current);
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The path escaped its trusted root.");
            }
            current = parent;
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Trusted file paths must not traverse reparse points.");
        }
    }
}
''')

replace_once(
    "src/DropSpace.Infrastructure/Storage/StagedFileImportService.cs",
    '''    private string ResolveStagedPath(string path)
    {
        var fullPath = PayloadPathPolicy.ResolveContainedPath(paths.Staging, Path.GetRelativePath(paths.Staging, path));
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Staging paths must not traverse reparse points.");
            if (string.Equals(current, paths.Staging, StringComparison.OrdinalIgnoreCase)) break;
        }
        return fullPath;
    }
''',
    '''    private string ResolveStagedPath(string path) =>
        ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Staging, path);
''')

# AUD-003/AUD-010: reparse-aware destination preparation and all-or-nothing multi-file finalization.
replace_once(
    "src/DropSpace.Infrastructure/Network/DropLinkHost.cs",
    '''            foreach (var item in receive.Manifest.Items)
            {
                await CommitItemAsync(
                    receive,
                    item,
                    finalizationDeadline.Token).ConfigureAwait(false);
            }
''',
    '''            foreach (var item in receive.Manifest.Items)
            {
                await CommitItemAsync(
                    receive,
                    item,
                    finalizationDeadline.Token).ConfigureAwait(false);
            }
''')
# The block above is intentionally asserted as an anchor for the rollback patch below.
replace_once(
    "src/DropSpace.Infrastructure/Network/DropLinkHost.cs",
    '''        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "DropLink transfer finalization failed for session {SessionId}.",
                receive.Session.Id);
            return await MarkFinalizationFailedAsync(receive, exception).ConfigureAwait(false);
        }
''',
    '''        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            RollbackCompletedItems(receive);
            logger.LogWarning(
                exception,
                "DropLink transfer finalization failed for session {SessionId}; completed outputs were rolled back.",
                receive.Session.Id);
            return await MarkFinalizationFailedAsync(receive, exception).ConfigureAwait(false);
        }
''')

replace_once(
    "src/DropSpace.Infrastructure/Network/DropLinkHost.cs",
    '''        var relative = TransferManifestPolicy.NormalizeRelativePath(item.RelativePath);
        var destination = Path.GetFullPath(Path.Combine(receive.DestinationRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(receive.DestinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The transfer destination escaped its root.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = string.Concat(destination, ".", receive.Session.Id.ToString("N"), ".tmp");
''',
    '''        var relative = TransferManifestPolicy.NormalizeRelativePath(item.RelativePath);
        var destination = ReparseSafePathPolicy.PrepareContainedFileDestination(receive.DestinationRoot, relative);
        var temporary = string.Concat(destination, ".", receive.Session.Id.ToString("N"), ".tmp");
''')

replace_once(
    "src/DropSpace.Infrastructure/Network/DropLinkHost.cs",
    '''            File.Move(temporary, destination, overwrite: false);
            receive.CompletedPaths.Enqueue(relative);
''',
    '''            ReparseSafePathPolicy.RevalidatePreparedDestination(receive.DestinationRoot, destination);
            File.Move(temporary, destination, overwrite: false);
            ReparseSafePathPolicy.RevalidatePreparedDestination(receive.DestinationRoot, destination);
            receive.CompletedPaths.Enqueue(relative);
''')

insert_before(
    "src/DropSpace.Infrastructure/Network/DropLinkHost.cs",
    '''    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
''',
    '''    private static void RollbackCompletedItems(ReceiveTransfer receive)
    {
        while (receive.CompletedPaths.TryDequeue(out var relative))
        {
            try
            {
                var destination = ReparseSafePathPolicy.PrepareContainedFileDestination(receive.DestinationRoot, relative);
                ReparseSafePathPolicy.RevalidatePreparedDestination(receive.DestinationRoot, destination);
                if (File.Exists(destination)) File.Delete(destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                System.Diagnostics.Debug.WriteLine($"DropLink rollback deferred: {exception.GetType().Name}");
            }
        }
    }

''')

# AUD-011: bound server/finalizer shutdown instead of waiting forever.
replace_once(
    "src/DropSpace.Infrastructure/Network/DropLinkHost.cs",
    '''    private async Task StopCoreAsync()
    {
        _endpoint = null;
''',
    '''    private async Task StopCoreAsync()
    {
        var shutdownBudget = TimeSpan.FromSeconds(10);
        _endpoint = null;
''')
replace_once(
    "src/DropSpace.Infrastructure/Network/DropLinkHost.cs",
    '''            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
''',
    '''            try
            {
                await _app.StopAsync(CancellationToken.None).WaitAsync(shutdownBudget).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("DropLink host graceful stop exceeded {ShutdownBudget}; disposal will continue.", shutdownBudget);
            }
            await _app.DisposeAsync().AsTask().WaitAsync(shutdownBudget).ConfigureAwait(false);
''')
replace_once(
    "src/DropSpace.Infrastructure/Network/DropLinkHost.cs",
    '''                await Task.WhenAll(finalizers).ConfigureAwait(false);
''',
    '''                await Task.WhenAll(finalizers).WaitAsync(shutdownBudget).ConfigureAwait(false);
''')

# AUD-005: durable best-effort payload delete journal, drained on service construction and writes.
replace_once(
    "src/DropSpace.Infrastructure/Storage/FilePayloadStore.cs",
    '''public sealed class FilePayloadStore(AppStoragePaths paths) : IPayloadStore
{
''',
    '''public sealed class FilePayloadStore : IPayloadStore
{
    private const int MaximumDeferredDeletes = 10_000;
    private readonly AppStoragePaths paths;
    private readonly object _deferredDeleteGate = new();
    private readonly string _deferredDeletePath;

    public FilePayloadStore(AppStoragePaths paths)
    {
        this.paths = paths;
        _deferredDeletePath = Path.Combine(paths.Data, "payload-delete.queue");
        TryDrainDeferredDeletes();
    }
''')
replace_once(
    "src/DropSpace.Infrastructure/Storage/FilePayloadStore.cs",
    '''        paths.EnsureCreated();
        var id = Guid.NewGuid();
''',
    '''        paths.EnsureCreated();
        TryDrainDeferredDeletes();
        var id = Guid.NewGuid();
''')
replace_once(
    "src/DropSpace.Infrastructure/Storage/FilePayloadStore.cs",
    '''    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
''',
    '''    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(relativePath);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            QueueDeferredDelete(relativePath);
            throw;
        }

        TryDrainDeferredDeletes();
        return Task.CompletedTask;
    }
''')
insert_before(
    "src/DropSpace.Infrastructure/Storage/FilePayloadStore.cs",
    '''    private static void TryDelete(string path)
''',
    '''    private void QueueDeferredDelete(string relativePath)
    {
        lock (_deferredDeleteGate)
        {
            try
            {
                paths.EnsureCreated();
                var existing = File.Exists(_deferredDeletePath)
                    ? File.ReadAllLines(_deferredDeletePath).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
                    : [];
                if (!existing.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
                {
                    existing.Add(relativePath);
                }
                if (existing.Count > MaximumDeferredDeletes)
                {
                    existing = existing.TakeLast(MaximumDeferredDeletes).ToList();
                }
                RewriteDeferredDeleteJournal(existing);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                System.Diagnostics.Debug.WriteLine($"Payload delete journal write deferred: {exception.GetType().Name}");
            }
        }
    }

    private void TryDrainDeferredDeletes()
    {
        lock (_deferredDeleteGate)
        {
            try
            {
                if (!File.Exists(_deferredDeletePath)) return;
                var remaining = new List<string>();
                foreach (var relativePath in File.ReadLines(_deferredDeletePath)
                             .Where(line => !string.IsNullOrWhiteSpace(line))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Take(MaximumDeferredDeletes))
                {
                    try
                    {
                        var path = ResolvePath(relativePath);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                    {
                        remaining.Add(relativePath);
                    }
                }
                RewriteDeferredDeleteJournal(remaining);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Payload delete journal drain deferred: {exception.GetType().Name}");
            }
        }
    }

    private void RewriteDeferredDeleteJournal(IReadOnlyCollection<string> entries)
    {
        paths.EnsureCreated();
        if (entries.Count == 0)
        {
            if (File.Exists(_deferredDeletePath)) File.Delete(_deferredDeletePath);
            return;
        }

        var temporary = string.Concat(_deferredDeletePath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            File.WriteAllLines(temporary, entries);
            File.Move(temporary, _deferredDeletePath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

''')

# AUD-004: rollback reports failures and MainViewModel performs a second reconciliation
# pass instead of simply claiming the old settings are active.
write(
    "src/DropSpace.App/Services/SettingsTransactionRollbackCoordinator.cs",
    '''namespace DropSpace.App.Services;

internal sealed record SettingsRollbackFailure(string Category, Exception Exception);

internal sealed class SettingsTransactionRollbackCoordinator
{
    private readonly Stack<(string Category, Func<Task> Undo)> _committed = new();

    internal void Committed(string category, Func<Task> undo) => _committed.Push((category, undo));

    internal async Task<IReadOnlyList<SettingsRollbackFailure>> RollbackAsync(Action<string, Exception> report)
    {
        var failures = new List<SettingsRollbackFailure>();
        while (_committed.TryPop(out var step))
        {
            try { await step.Undo(); }
            catch (Exception exception)
            {
                failures.Add(new SettingsRollbackFailure(step.Category, exception));
                try { report(step.Category, exception); }
                catch { }
            }
        }
        return failures;
    }
}
''')
replace_once(
    "src/DropSpace.App/ViewModels/MainViewModel.cs",
    '''        catch
        {
            await rollback.RollbackAsync((category, exception) =>
                _logger.LogError("Settings rollback failed in {Category}: {FailureType}.", category, exception.GetType().Name));
            Settings = previous;
            StatusMessage = previousStatus;
            throw;
        }
    }
''',
    '''        catch (Exception updateException)
        {
            var rollbackFailures = await rollback.RollbackAsync((category, exception) =>
                _logger.LogError("Settings rollback failed in {Category}: {FailureType}.", category, exception.GetType().Name));
            if (rollbackFailures.Count > 0)
            {
                var reconciliationFailures = await ReconcileSettingsStateAsync(previous).ConfigureAwait(false);
                if (reconciliationFailures.Count > 0)
                {
                    _logger.LogCritical(
                        "Settings update rollback and reconciliation both had failures. Rollback={RollbackFailures}, Reconciliation={ReconciliationFailures}.",
                        rollbackFailures.Count,
                        reconciliationFailures.Count);
                    try { Settings = await _settingsService.LoadAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { Settings = previous; }
                    StatusMessage = previousStatus;
                    throw new AggregateException(
                        "The settings update failed and the previous runtime state could not be fully reconciled. Restart DropSpace before changing settings again.",
                        new[] { updateException }.Concat(rollbackFailures.Select(failure => failure.Exception)).Concat(reconciliationFailures));
                }
            }

            try { Settings = await _settingsService.LoadAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { Settings = previous; }
            StatusMessage = previousStatus;
            throw;
        }
    }

    private async Task<IReadOnlyList<Exception>> ReconcileSettingsStateAsync(AppSettings previous)
    {
        var failures = new List<Exception>();
        async Task AttemptAsync(string category, Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception exception)
            {
                failures.Add(exception);
                _logger.LogError(exception, "Settings reconciliation failed in {Category}.", category);
            }
        }

        var preflight = UiSettingsPreflightAsync;
        if (preflight is not null)
            await AttemptAsync("ui-preflight", () => preflight(previous, CancellationToken.None));
        await AttemptAsync("startup", () => _startupRegistration.SetEnabledAsync(previous.StartWithWindows, CancellationToken.None));
        await AttemptAsync("clipboard", () => _clipboard.UpdateSettingsAsync(previous, CancellationToken.None));
        await AttemptAsync("handoff", () => _deviceHandoff.UpdateSettingsAsync(previous, CancellationToken.None));
        await AttemptAsync("cross-device-clipboard", () => _crossDeviceClipboard.UpdateSettingsAsync(previous, CancellationToken.None));
        await AttemptAsync("settings-store", () => _settingsService.SaveAsync(previous, CancellationToken.None));
        return failures;
    }
''')

# AUD-006: crash marker uses the same privacy redaction policy as normal logs.
replace_once(
    "src/DropSpace.App/App.xaml.cs",
    '''            var marker = $"{DateTimeOffset.UtcNow:O} stage={stage} exception={SummarizeExceptionChain(exception)}";
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                marker += $"{Environment.NewLine}stack={exception.StackTrace.ReplaceLineEndings(" | ")}";
            }
''',
    '''            var marker = $"{DateTimeOffset.UtcNow:O} stage={stage} exception={LogRedactor.Redact(SummarizeExceptionChain(exception))}";
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                marker += $"{Environment.NewLine}stack={LogRedactor.Redact(exception.StackTrace.ReplaceLineEndings(" | "))}";
            }
''')

# AUD-007: do online whole-chain revocation checking for unattended update trust.
replace_once(
    "src/DropSpace.App/Services/AuthenticodeTrustedUpdateVerifier.cs",
    '''                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00001000,
''',
    '''                // WTD_REVOKE_WHOLECHAIN + WTD_REVOCATION_CHECK_CHAIN. Do not use
                // WTD_CACHE_ONLY_URL_RETRIEVAL for auto-install trust: a revoked publisher
                // certificate must be able to fail closed when revocation data is online.
                RevocationChecks = 1,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00000040,
''')

# AUD-015: preserve non-blocking logging while making overload drops measurable.
replace_once(
    "src/DropSpace.Infrastructure/Logging/RedactingFileLoggerProvider.cs",
    '''        FullMode = BoundedChannelFullMode.DropWrite,
''',
    '''        FullMode = BoundedChannelFullMode.Wait,
''')
replace_once(
    "src/DropSpace.Infrastructure/Logging/RedactingFileLoggerProvider.cs",
    '''    private long _writeFailureCount;
''',
    '''    private long _writeFailureCount;
    private long _droppedMessageCount;
''')
replace_once(
    "src/DropSpace.Infrastructure/Logging/RedactingFileLoggerProvider.cs",
    '''    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RedactingFileLogger(name, _messages.Writer));
''',
    '''    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RedactingFileLogger(name, TryEnqueue));
''')
replace_once(
    "src/DropSpace.Infrastructure/Logging/RedactingFileLoggerProvider.cs",
    '''    public long WriteFailureCount => Interlocked.Read(ref _writeFailureCount);
''',
    '''    public long WriteFailureCount => Interlocked.Read(ref _writeFailureCount);

    public long DroppedMessageCount => Interlocked.Read(ref _droppedMessageCount);

    private bool TryEnqueue(string line)
    {
        if (_messages.Writer.TryWrite(line)) return true;
        Interlocked.Increment(ref _droppedMessageCount);
        return false;
    }
''')
replace_once(
    "src/DropSpace.Infrastructure/Logging/RedactingFileLoggerProvider.cs",
    '''            await foreach (var message in _messages.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                try
                {
                    if (await TryWriteMessageAsync(message).ConfigureAwait(false))
                    {
                        Volatile.Write(ref _consecutiveWriteFailures, 0);
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A single malformed filesystem state must not terminate the logger worker.
                    RecordWriteFailure(exception);
                }
            }
''',
    '''            await foreach (var message in _messages.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                try
                {
                    if (await TryWriteMessageAsync(message).ConfigureAwait(false))
                    {
                        Volatile.Write(ref _consecutiveWriteFailures, 0);
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A single malformed filesystem state must not terminate the logger worker.
                    RecordWriteFailure(exception);
                }
            }

            var dropped = DroppedMessageCount;
            if (dropped > 0)
            {
                await TryWriteMessageAsync(
                    $"{DateTimeOffset.UtcNow:O} level=Warning event=0 category=DropSpace.Infrastructure.Logging message=bounded-log-queue-dropped count={dropped}")
                    .ConfigureAwait(false);
            }
''')
replace_once(
    "src/DropSpace.Infrastructure/Logging/RedactingFileLoggerProvider.cs",
    '''    private sealed class RedactingFileLogger(string category, ChannelWriter<string> writer) : ILogger
''',
    '''    private sealed class RedactingFileLogger(string category, Func<string, bool> enqueue) : ILogger
''')
replace_once(
    "src/DropSpace.Infrastructure/Logging/RedactingFileLoggerProvider.cs",
    '''            writer.TryWrite(line);
''',
    '''            _ = enqueue(line);
''')

# AUD-011 Nearby Share: bound disposal wait and Kestrel stop/disposal.
replace_once(
    "src/DropSpace.Infrastructure/Sharing/NearbyShareServer.cs",
    '''public sealed class NearbyShareServer(ShareLimits? limits = null) : IAsyncDisposable
{
''',
    '''public sealed class NearbyShareServer(ShareLimits? limits = null) : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
''')
replace_once(
    "src/DropSpace.Infrastructure/Sharing/NearbyShareServer.cs",
    '''        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
''',
    '''        await _startGate.WaitAsync(CancellationToken.None).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
''')
replace_once(
    "src/DropSpace.Infrastructure/Sharing/NearbyShareServer.cs",
    '''                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
''',
    '''                await app.StopAsync(CancellationToken.None).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
                await app.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
''')

# AUD-008/AUD-009: bounded streaming JSON and fail-closed per-source + global creation admission.
replace_once(
    "share-worker/src/index.js",
    '''async function createShare(request, env) {
  requireHttps(request);
  const body = await readJson(request, MAX_CREATE_REQUEST_BYTES);
''',
    '''async function createShare(request, env) {
  requireHttps(request);
  await requireCreateAdmission(request, env);
  const body = await readJson(request, MAX_CREATE_REQUEST_BYTES);
''')
insert_before(
    "share-worker/src/index.js",
    '''async function createShare(request, env) {
''',
    '''const CREATE_WINDOW_MS = 60_000;
const CREATE_SOURCE_LIMIT = 30;
const CREATE_GLOBAL_LIMIT = 600;

async function requireCreateAdmission(request, env) {
  const binding = env.SHARE_CREATION_LIMITER;
  if (!binding || typeof binding.idFromName !== "function" || typeof binding.get !== "function") {
    throw new HttpError("creation-limiter-unavailable", 503);
  }
  const address = request.headers.get("cf-connecting-ip") || "unknown";
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(address)));
  const sourceKey = [...digest.subarray(0, 16)].map(value => value.toString(16).padStart(2, "0")).join("");
  await admitCreation(binding, "global", CREATE_GLOBAL_LIMIT);
  await admitCreation(binding, "source-" + sourceKey, CREATE_SOURCE_LIMIT);
}

async function admitCreation(binding, key, limit) {
  const stub = binding.get(binding.idFromName(key));
  const response = await stub.fetch("https://dropspace-creation-limiter/admit", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ limit, windowMs: CREATE_WINDOW_MS }),
  });
  if (!response.ok) {
    let result = {};
    try { result = await response.json(); } catch { result = {}; }
    throw new HttpError(result.error || "creation-rate-limited", response.status === 429 ? 429 : 503);
  }
}

''')
replace_once(
    "share-worker/src/index.js",
    '''async function readJson(request, maximum) { const text = await request.text(); if (text.length > maximum) throw new HttpError("body-too-large", 413); try { return JSON.parse(text); } catch { throw new HttpError("json-invalid", 400); } }
''',
    '''async function readJson(request, maximum) {
  const declared = request.headers.get("content-length");
  if (declared !== null) {
    const length = Number(declared);
    if (!Number.isSafeInteger(length) || length < 0) throw new HttpError("body-length-invalid", 400);
    if (length > maximum) throw new HttpError("body-too-large", 413);
  }
  if (!request.body) throw new HttpError("body-missing", 400);
  const reader = request.body.getReader();
  const chunks = [];
  let total = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > maximum) {
        await reader.cancel("body-too-large").catch(() => {});
        throw new HttpError("body-too-large", 413);
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  let text;
  try { text = new TextDecoder("utf-8", { fatal: true }).decode(bytes); }
  catch { throw new HttpError("json-invalid", 400); }
  try { return JSON.parse(text); } catch { throw new HttpError("json-invalid", 400); }
}
''')
insert_before(
    "share-worker/src/index.js",
    '''export class ShareUsageCoordinator {
''',
    '''export class ShareCreationLimiter {
  constructor(state) {
    this.state = state;
  }

  async fetch(request) {
    try {
      const body = await request.json();
      return await this.state.blockConcurrencyWhile(async () => {
        const limit = Number(body?.limit);
        const windowMs = Number(body?.windowMs);
        if (!Number.isInteger(limit) || limit < 1 || limit > 10_000 ||
            !Number.isInteger(windowMs) || windowMs < 1_000 || windowMs > 60 * 60 * 1000) {
          throw new HttpError("creation-limiter-request-invalid", 400);
        }
        const now = Date.now();
        let current = await this.state.storage.get("state");
        if (!current || !Number.isSafeInteger(current.windowStartedAt) ||
            now - current.windowStartedAt >= windowMs || now < current.windowStartedAt) {
          current = { windowStartedAt: now, count: 0 };
        }
        if (!Number.isInteger(current.count) || current.count < 0) current.count = 0;
        if (current.count >= limit) return coordinatorJson({ error: "creation-rate-limited" }, 429);
        current.count += 1;
        await this.state.storage.put("state", current);
        return coordinatorJson({ ok: true, remaining: Math.max(0, limit - current.count) });
      });
    } catch (error) {
      const status = error instanceof HttpError ? error.status : 500;
      return coordinatorJson({ error: error instanceof HttpError ? error.code : "creation-limiter-failed" }, status);
    }
  }
}

''')

replace_once(
    "share-worker/wrangler.toml.example",
    '''[[durable_objects.bindings]]
name = "SHARE_COORDINATOR"
class_name = "ShareUsageCoordinator"

[[migrations]]
tag = "v1"
new_classes = ["ShareUsageCoordinator"]
''',
    '''[[durable_objects.bindings]]
name = "SHARE_COORDINATOR"
class_name = "ShareUsageCoordinator"

[[durable_objects.bindings]]
name = "SHARE_CREATION_LIMITER"
class_name = "ShareCreationLimiter"

[[migrations]]
tag = "v1"
new_classes = ["ShareUsageCoordinator"]

[[migrations]]
tag = "v2"
new_classes = ["ShareCreationLimiter"]
''')
replace_once(
    "share-worker/README.md",
    '''The Worker requires the `SHARE_COORDINATOR` Durable Object binding shown in `wrangler.toml.example`. It is the authoritative concurrency-safe ledger for the token's aggregate item and plaintext-byte limits; requests fail closed when the binding is absent. The revoke path marks the coordinator first and paginates R2 deletion, so a large share cannot leave undeleted objects after the first listing page.
''',
    '''The Worker requires the `SHARE_COORDINATOR` Durable Object binding shown in `wrangler.toml.example`. It is the authoritative concurrency-safe ledger for the token's aggregate item and plaintext-byte limits; requests fail closed when the binding is absent. The revoke path marks the coordinator first and paginates R2 deletion, so a large share cannot leave undeleted objects after the first listing page. `SHARE_CREATION_LIMITER` is also mandatory: creation is admitted through a per-source hashed bucket and a global one-minute budget before the request body is read. Keep Cloudflare WAF/rate-limiting enabled as an additional edge layer rather than relying on application admission alone.
''')
replace_once(
    "share-worker/test/worker.test.mjs",
    '''import worker, { ShareUsageCoordinator } from "../src/index.js";
''',
    '''import worker, { ShareCreationLimiter, ShareUsageCoordinator } from "../src/index.js";
''')
insert_before(
    "share-worker/test/worker.test.mjs",
    '''test("the coordinator reserves concurrent plaintext byte usage atomically", async () => {
''',
    '''test("share creation limiter enforces a bounded window", async () => {
  const values = new Map();
  const limiter = new ShareCreationLimiter({
    storage: {
      async get(key) { return values.get(key); },
      async put(key, value) { values.set(key, value); },
    },
    async blockConcurrencyWhile(callback) { return callback(); },
  });
  const request = () => new Request("https://limiter/admit", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ limit: 2, windowMs: 60_000 }),
  });
  assert.equal((await limiter.fetch(request())).status, 200);
  assert.equal((await limiter.fetch(request())).status, 200);
  const limited = await limiter.fetch(request());
  assert.equal(limited.status, 429);
  assert.equal((await limited.json()).error, "creation-rate-limited");
});

''')

# AUD-002/AUD-017: pushes to main validate but cannot publish; publication is explicit,
# and Stable publication fails closed without signing credentials.
replace_once(
    ".github/workflows/release.yml",
    '''      - name: Validate Stable and Preview release metadata
        shell: pwsh
        run: |
          ./scripts/Test-ReleaseVersion.ps1
          ./scripts/Test-ReleaseConsistency.ps1
''',
    '''      - name: Validate Stable and Preview release metadata
        shell: pwsh
        run: |
          ./scripts/Test-ReleaseVersion.ps1
          ./scripts/Test-ReleaseConsistency.ps1

      - name: Require signing credentials for explicit Stable publication
        if: github.event_name == 'workflow_dispatch' && steps.version.outputs.channel == 'Stable'
        shell: pwsh
        run: |
          if ($env:SIGNING_ENABLED -ne 'true') {
            throw "Stable publication requires configured Artifact Signing credentials."
          }
''')
replace_once(
    ".github/workflows/release.yml",
    '''    if: github.event_name != 'pull_request'
''',
    '''    if: github.event_name == 'workflow_dispatch'
''')

print("Applied architecture audit fixes.")
