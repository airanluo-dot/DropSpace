using System.Text.Json;
using System.Text.Json.Serialization;
using DropSpace.Core.Policies;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Storage;

public sealed record StagingLease(
    int SchemaVersion,
    string LeaseId,
    string Kind,
    string RelativeRoot,
    string OwnerId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool SensitivePlaintext)
{
    [JsonIgnore]
    public string RootPath { get; internal init; } = string.Empty;
}

/// <summary>
/// Owns crash-persistent staging directories. Lease files are written before a
/// caller starts producing bytes; recovery only removes a validated, confined
/// tree and retains the lease when any part of cleanup fails.
/// </summary>
public sealed class StagingLeaseStore(
    AppStoragePaths paths,
    ILogger<StagingLeaseStore> logger)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumLeaseCount = 1_024;
    public static readonly TimeSpan DefaultLeaseLifetime = TimeSpan.FromHours(2);
    private const long MaximumLeaseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _ownerId = string.Concat(Environment.ProcessId, "-", Guid.NewGuid().ToString("N"));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<StagingLease> AcquireAsync(
        string kind,
        string relativeRoot,
        bool sensitivePlaintext,
        TimeSpan? lifetime = null,
        bool allowExistingRoot = true,
        CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        var root = ResolveAndValidateRelativeRoot(relativeRoot);
        var leaseLifetime = lifetime ?? DefaultLeaseLifetime;
        if (leaseLifetime <= TimeSpan.Zero || leaseLifetime > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            paths.EnsureCreated();
            EnsureNotReparsePoint(paths.StagingLeases);
            var count = Directory.EnumerateFiles(paths.StagingLeases, "*.json", SearchOption.TopDirectoryOnly).Take(MaximumLeaseCount + 1).Count();
            if (count >= MaximumLeaseCount)
            {
                throw new InvalidOperationException("The staging lease limit has been reached.");
            }

            if (!allowExistingRoot && Directory.Exists(root))
            {
                throw new InvalidOperationException("The staging lease root is already owned or retained by another operation.");
            }

            EnsureDirectoryConfined(root);
            var lease = new StagingLease(
                CurrentSchemaVersion,
                Guid.NewGuid().ToString("N"),
                kind,
                NormalizeRelativePath(relativeRoot),
                _ownerId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.Add(leaseLifetime),
                sensitivePlaintext)
            {
                RootPath = root,
            };
            await WriteAtomicAsync(lease, overwrite: false, cancellationToken).ConfigureAwait(false);
            return lease;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StagingLease> RenewAsync(
        StagingLease lease,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLease(lease);
        if (!string.Equals(lease.OwnerId, _ownerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only the active staging owner may renew a lease.");
        }

        var leaseLifetime = lifetime ?? DefaultLeaseLifetime;
        if (leaseLifetime <= TimeSpan.Zero || leaseLifetime > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var renewed = lease with { ExpiresAtUtc = DateTimeOffset.UtcNow.Add(leaseLifetime) };
            var root = ResolveAndValidateRelativeRoot(renewed.RelativeRoot);
            EnsureDirectoryConfined(root);
            renewed = renewed with { RootPath = root };
            await WriteAtomicAsync(renewed, overwrite: true, cancellationToken).ConfigureAwait(false);
            return renewed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CompleteAsync(
        StagingLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLease(lease);
        if (!string.Equals(lease.OwnerId, _ownerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only the active staging owner may complete a lease.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = ResolveAndValidateRelativeRoot(lease.RelativeRoot);
            if (!TryDeleteConfinedTree(root, cancellationToken))
            {
                logger.LogWarning("Staging lease cleanup retained a lease because its root could not be removed.");
                return false;
            }

            var leasePath = GetLeasePath(lease.LeaseId);
            try
            {
                if (File.Exists(leasePath))
                {
                    File.Delete(leasePath);
                }

                return true;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                logger.LogWarning(exception, "Staging root was removed but the lease record could not be removed; recovery will retry it.");
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RecoverAbandonedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            paths.EnsureCreated();
            EnsureNotReparsePoint(paths.StagingLeases);
            var recovered = 0;
            var now = DateTimeOffset.UtcNow;
            foreach (var leasePath in Directory.EnumerateFiles(paths.StagingLeases, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                StagingLease? lease;
                try
                {
                    lease = ReadLease(leasePath);
                }
                catch (Exception exception) when (IsFileFailure(exception) || exception is JsonException)
                {
                    QuarantineMalformedLease(leasePath, exception);
                    continue;
                }
                if (lease is null ||
                    string.Equals(lease.OwnerId, _ownerId, StringComparison.Ordinal) && lease.ExpiresAtUtc > now)
                {
                    continue;
                }

                var root = ResolveAndValidateRelativeRoot(lease.RelativeRoot);
                if (!TryDeleteConfinedTree(root, cancellationToken))
                {
                    logger.LogWarning("Abandoned staging lease was retained because its root could not be removed.");
                    continue;
                }

                try
                {
                    File.Delete(leasePath);
                    recovered++;
                }
                catch (Exception exception) when (IsFileFailure(exception))
                {
                    logger.LogWarning(exception, "Abandoned staging root was removed but its lease record remains for retry.");
                }
            }

            return recovered;
        }
        finally
        {
            _gate.Release();
        }
    }

    private StagingLease? ReadLease(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumLeaseBytes)
        {
            throw new InvalidDataException("The staging lease file size is invalid.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096, FileOptions.SequentialScan);
        var lease = JsonSerializer.Deserialize<StagingLease>(stream, JsonOptions)
            ?? throw new InvalidDataException("The staging lease is empty.");
        ValidateLease(lease);
        if (!string.Equals(Path.GetFileName(path), string.Concat(lease.LeaseId, ".json"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The staging lease filename does not match its lease ID.");
        }

        var root = ResolveAndValidateRelativeRoot(lease.RelativeRoot);
        return lease with { RootPath = root };
    }

    private async Task WriteAtomicAsync(StagingLease lease, bool overwrite, CancellationToken cancellationToken)
    {
        var leasePath = GetLeasePath(lease.LeaseId);
        var temporary = string.Concat(leasePath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(lease with { RootPath = string.Empty }, JsonOptions);
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4_096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (overwrite && File.Exists(leasePath))
            {
                File.Replace(temporary, leasePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, leasePath, overwrite: false);
            }
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private string ResolveAndValidateRelativeRoot(string relativeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeRoot);
        if (Path.IsPathRooted(relativeRoot) || relativeRoot.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new InvalidDataException("A staging lease root must be relative.");
        }

        var segments = relativeRoot.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("A staging lease root must not contain traversal segments.");
        }

        var root = Path.GetFullPath(paths.Staging);
        var candidate = PayloadPathPolicy.ResolveContainedPath(root, relativeRoot);
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A staging lease cannot own the staging root itself.");
        }

        return candidate;
    }

    private void EnsureDirectoryConfined(string directory)
    {
        var root = Path.GetFullPath(paths.Staging);
        EnsureNotReparsePoint(root);
        var relative = Path.GetRelativePath(root, directory);
        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) && !Directory.Exists(current))
            {
                throw new InvalidDataException("A staging lease root collides with a file.");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            EnsureNotReparsePoint(current);
        }
    }

    private bool TryDeleteConfinedTree(string root, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return true;
            }

            EnsureDirectoryConfined(root);
            DeleteDirectoryContents(root, cancellationToken);
            Directory.Delete(root, recursive: false);
            return true;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            logger.LogWarning(exception, "Confined staging cleanup failed; the lease remains durable.");
            return false;
        }
    }

    private void DeleteDirectoryContents(string directory, CancellationToken cancellationToken)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("A staging cleanup tree contains a reparse point.");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                DeleteDirectoryContents(entry, cancellationToken);
                Directory.Delete(entry, recursive: false);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private void QuarantineMalformedLease(string path, Exception exception)
    {
        logger.LogWarning(exception, "A malformed staging lease was quarantined without touching its unknown root.");
        try
        {
            var quarantineRoot = Path.Combine(paths.Quarantine, "staging-leases");
            Directory.CreateDirectory(quarantineRoot);
            var destination = Path.Combine(quarantineRoot, string.Concat(Path.GetFileName(path), ".", Guid.NewGuid().ToString("N")));
            File.Move(path, destination, overwrite: false);
        }
        catch (Exception quarantineException) when (IsFileFailure(quarantineException))
        {
            logger.LogError(quarantineException, "The malformed staging lease could not be quarantined; it remains for retry.");
        }
    }

    private string GetLeasePath(string leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId) || leaseId.Length > 64 || leaseId.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidDataException("The staging lease ID is invalid.");
        }

        paths.EnsureCreated();
        return Path.Combine(paths.StagingLeases, string.Concat(leaseId, ".json"));
    }

    private static void ValidateKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 64 || kind.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("The staging lease kind is invalid.", nameof(kind));
        }
    }

    private static void ValidateLease(StagingLease lease)
    {
        if (lease.SchemaVersion != CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(lease.LeaseId) ||
            string.IsNullOrWhiteSpace(lease.Kind) ||
            string.IsNullOrWhiteSpace(lease.RelativeRoot) ||
            string.IsNullOrWhiteSpace(lease.OwnerId) ||
            lease.CreatedAtUtc == default ||
            lease.ExpiresAtUtc < lease.CreatedAtUtc)
        {
            throw new InvalidDataException("The staging lease fields are invalid.");
        }

        ValidateKind(lease.Kind);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException;

    private static void EnsureNotReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Staging paths must not traverse reparse points.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            System.Diagnostics.Debug.WriteLine(exception.GetType().Name);
        }
    }
}
