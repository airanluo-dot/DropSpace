using DropSpace.Core.Abstractions;
using DropSpace.Core.Policies;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Storage;

/// <summary>
/// Reconciles files below the app-owned payload root after a process crash.
/// Unknown final payloads are moved into the app-owned quarantine after a grace
/// period; temporary write files are deleted after their shorter TTL.  The
/// reconciler never traverses a reparse point or any path outside the payload root.
/// </summary>
public sealed class OwnedPayloadReconciler(
    AppStoragePaths paths,
    IPayloadCleanupRepository ownership,
    ILogger<OwnedPayloadReconciler> logger)
{
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TemporaryFileTtl = TimeSpan.FromMinutes(10);

    public async Task<int> ReconcileAsync(
        TimeSpan? gracePeriod = null,
        CancellationToken cancellationToken = default)
    {
        var grace = gracePeriod ?? DefaultGracePeriod;
        if (grace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        }

        paths.EnsureCreated();
        var ownedPaths = await ownership.GetOwnedPayloadPathsAsync(cancellationToken).ConfigureAwait(false);
        var normalizedOwned = new HashSet<string>(
            ownedPaths.Select(NormalizeRelativePath),
            StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var reconciled = 0;

        foreach (var file in EnumerateFilesSafely(paths.Payloads, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(Path.GetRelativePath(paths.Payloads, file));
            if (normalizedOwned.Contains(relative))
            {
                continue;
            }

            DateTime lastWrite;
            try
            {
                lastWrite = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                logger.LogWarning(exception, "Payload orphan age could not be inspected; leaving it in place.");
                continue;
            }

            var age = now - new DateTimeOffset(lastWrite, TimeSpan.Zero);
            var temporary = file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            if (age < (temporary ? TemporaryFileTtl : grace))
            {
                continue;
            }

            try
            {
                ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(paths.Payloads, relative);
                if (temporary)
                {
                    File.Delete(file);
                }
                else
                {
                    Quarantine(file, relative);
                }

                reconciled++;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                logger.LogWarning(
                    exception,
                    "Unreferenced app-owned payload could not be reconciled; it will be retried.");
            }
        }

        return reconciled;
    }

    private void Quarantine(string file, string relative)
    {
        var generation = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var quarantineRelative = Path.Combine("payload-orphans", generation, relative);
        var destination = ReparseSafePathPolicy.PrepareContainedFileDestination(paths.Quarantine, quarantineRelative);
        File.Move(file, destination, overwrite: false);
    }

    private IEnumerable<string> EnumerateFilesSafely(string root, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            FileAttributes currentAttributes;
            try
            {
                currentAttributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                logger.LogWarning(exception, "Payload reconciliation could not enumerate one directory; leaving it in place.");
                continue;
            }

            if (currentAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                logger.LogWarning("Payload reconciliation skipped a reparse-point directory.");
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                logger.LogWarning(exception, "Payload reconciliation could not enumerate one directory; leaving it in place.");
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (IsFileFailure(exception))
                {
                    logger.LogWarning(exception, "Payload reconciliation could not inspect one entry; leaving it in place.");
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    logger.LogWarning("Payload reconciliation skipped a reparse-point entry.");
                }
                else if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return files;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException;
}
