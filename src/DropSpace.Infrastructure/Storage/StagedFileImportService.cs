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
    ILogger<StagedFileImportService> logger,
    StagingLeaseStore? stagingLeases = null)
{
    public Task<StagedFileImportResult> ImportBatchAsync(
        IReadOnlyList<string> stagingPaths,
        long? dropSessionId,
        string acquisitionKind,
        long maximumFileBytes,
        CancellationToken cancellationToken = default,
        StagingLease? ownedLease = null)
    {
        ArgumentNullException.ThrowIfNull(stagingPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        if (ownedLease is not null && stagingLeases is null)
        {
            throw new InvalidOperationException("An owned staging lease requires a staging lease store.");
        }

        var admittedPaths = stagingPaths.ToArray();
        // An owned lease must reach ImportCoreAsync even when the caller has already
        // cancelled; its finally block is the handoff's terminal cleanup owner.
        var schedulingCancellation = ownedLease is null ? cancellationToken : CancellationToken.None;
        return Task.Run(
            () => ImportCoreAsync(admittedPaths, dropSessionId, acquisitionKind, maximumFileBytes, cancellationToken, ownedLease),
            schedulingCancellation);
    }

    private async Task<StagedFileImportResult> ImportCoreAsync(
        IReadOnlyList<string> stagingPaths, long? dropSessionId, string acquisitionKind,
        long maximumFileBytes, CancellationToken cancellationToken, StagingLease? ownedLease)
    {
        var admitted = new List<string>();
        var leases = new List<StagingLease>();
        if (ownedLease is not null)
        {
            leases.Add(ownedLease);
        }
        var rejected = 0;
        foreach (var path in stagingPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                admitted.Add(ownedLease is null
                    ? ResolveStagedPath(path)
                    : ResolveOwnedStagedPath(ownedLease, path));
            }
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
            if (stagingLeases is not null && ownedLease is null)
            {
                foreach (var root in admitted
                             .Select(path => Path.GetDirectoryName(path) ?? paths.Staging)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    // A file directly below staging has no independently owned
                    // directory. Keep its existing per-file cleanup semantics;
                    // normal virtual-file batches use a dedicated subdirectory.
                    if (string.Equals(root, paths.Staging, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relativeRoot = Path.GetRelativePath(paths.Staging, root);
                    leases.Add(await stagingLeases.AcquireAsync(
                            "staged-import",
                            relativeRoot,
                            sensitivePlaintext: false,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            for (var index = 0; index < admitted.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PayloadRecord? payload = null;
                try
                {
                    var path = ResolveStagedPath(admitted[index]);
                    var candidate = await references.InspectAsync(path, cancellationToken).ConfigureAwait(false);
                    await using var input = ReparseSafeFileOpen.OpenRead(path);
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

            if (stagingLeases is not null)
            {
                foreach (var lease in leases)
                {
                    await stagingLeases.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private string ResolveStagedPath(string path) =>
        ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Staging, path);

    private string ResolveOwnedStagedPath(StagingLease lease, string path)
    {
        if (string.IsNullOrWhiteSpace(lease.RootPath))
        {
            throw new InvalidDataException("The owned staging lease has no resolved root.");
        }

        var withinLease = ReparseSafePathPolicy.ResolveExistingContainedPath(lease.RootPath, path);
        return ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Staging, withinLease);
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException;
}
