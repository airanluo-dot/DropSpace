using System.Text.Json;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Storage;

public sealed record StagedFileImportResult(int Accepted, int Rejected);

/// <summary>Transfers custody of confined staging files to durable Space payloads.</summary>
public sealed class StagedFileImportService(
    AppStoragePaths paths,
    IFileReferenceService references,
    IPayloadStore payloads,
    IItemRepository repository,
    ILogger<StagedFileImportService> logger)
{
    public Task<StagedFileImportResult> ImportBatchAsync(
        IReadOnlyList<string> stagingPaths,
        long? dropSessionId,
        string acquisitionKind,
        long maximumFileBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagingPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        var admittedPaths = stagingPaths.ToArray();
        return Task.Run(() => ImportCoreAsync(admittedPaths, dropSessionId, acquisitionKind, maximumFileBytes, cancellationToken), cancellationToken);
    }

    private async Task<StagedFileImportResult> ImportCoreAsync(
        IReadOnlyList<string> stagingPaths, long? dropSessionId, string acquisitionKind,
        long maximumFileBytes, CancellationToken cancellationToken)
    {
        var admitted = new List<string>();
        var rejected = 0;
        foreach (var path in stagingPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { admitted.Add(ResolveStagedPath(path)); }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                rejected++;
                logger.LogWarning(exception, "Staging admission rejected an unowned or unavailable path.");
            }
        }
        var batchId = Guid.NewGuid();
        var accepted = 0;
        try
        {
            for (var index = 0; index < admitted.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PayloadRecord? payload = null;
                try
                {
                    var path = ResolveStagedPath(admitted[index]);
                    var candidate = await references.InspectAsync(path, cancellationToken).ConfigureAwait(false);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    payload = await payloads.WriteFileAsync("files", candidate.Extension, input, maximumFileBytes, cancellationToken).ConfigureAwait(false);
                    var ownedPath = payloads.ResolvePath(payload.RelativePath);
                    var owned = candidate with { OriginalPath = ownedPath, NormalizedPath = Path.GetFullPath(ownedPath) };
                    var metadata = JsonSerializer.Serialize(new DropBatchMetadata(batchId, dropSessionId, index, admitted.Count, acquisitionKind));
                    await repository.AddOwnedSpaceFileAsync(owned, payload, metadata, cancellationToken).ConfigureAwait(false);
                    payload = null;
                    accepted++;
                }
                catch (Exception exception) when (IsFileFailure(exception))
                {
                    rejected++;
                    logger.LogWarning(exception, "An owned staging item could not be imported.");
                }
                finally
                {
                    if (payload is not null)
                    {
                        try { await payloads.DeleteAsync(payload.RelativePath, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception exception) when (IsFileFailure(exception))
                        {
                            logger.LogWarning(exception, "Uncommitted payload cleanup was deferred.");
                        }
                    }
                }
            }
            return new StagedFileImportResult(accepted, rejected);
        }
        finally
        {
            // Once admitted, the whole staging batch is ours, including files not yet
            // visited when cancellation interrupted the import. Never delete references.
            foreach (var path in admitted)
            {
                try
                {
                    File.Delete(ResolveStagedPath(path));
                    var parent = Path.GetDirectoryName(path)!;
                    if (!string.Equals(parent, paths.Staging, StringComparison.OrdinalIgnoreCase) &&
                        !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                }
                catch (Exception exception) when (IsFileFailure(exception))
                {
                    logger.LogWarning(exception, "Consumed staging cleanup was deferred.");
                }
            }
        }
    }

    private string ResolveStagedPath(string path)
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

    private static bool IsFileFailure(Exception exception) =>
        exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException;
}
