// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DropSpace.Core.Downloads;

namespace DropSpace.Infrastructure.Downloads;

public sealed class OutputReservationService : IOutputReservationService
{
    private const string MarkerSuffix = ".dropspace-reservation";
    private readonly object _gate = new();
    private bool _disposed;

    public Task<OutputReservation> ReserveAsync(Guid taskId, string directory, string fileName, CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("The task ID cannot be empty.", nameof(taskId));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!Path.IsPathRooted(directory)) throw new ArgumentException("The output directory must be absolute.", nameof(directory));
        if (Path.GetFileName(fileName) != fileName || fileName is "." or ".." || fileName.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ArgumentException("The output file name must be a single file name.", nameof(fileName));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Directory.CreateDirectory(directory);
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 0; index < 100_000; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateName = index == 0 ? fileName : $"{baseName} ({index}){extension}";
            var outputPath = Path.Combine(directory, candidateName);
            DownloadStorage.Safe(directory, outputPath);
            if (PathExists(outputPath)) continue;

            var markerPath = outputPath + MarkerSuffix;
            DownloadStorage.Safe(directory, markerPath);
            try
            {
                using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 256, FileOptions.WriteThrough);
                var content = $"{taskId:D}|{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}|{DateTimeOffset.UtcNow:O}";
                var bytes = Encoding.UTF8.GetBytes(content);
                marker.Write(bytes);
                marker.Flush(true);
                return Task.FromResult(new OutputReservation(taskId, outputPath, markerPath));
            }
            catch (IOException) when (IsMarkerCollision(markerPath) || PathExists(outputPath))
            {
                // Another task may own the marker. Reclaim only markers whose owner process is gone.
                // Retry the same candidate after a stale marker is removed.
                if (TryReclaimStaleMarker(markerPath)) index--;
            }
            catch (UnauthorizedAccessException) when (IsMarkerCollision(markerPath) || PathExists(outputPath))
            {
                // A concurrent commit may remove its marker between CreateNew failing and
                // this filter running. Its committed output is also collision evidence.
                // Real directory permission failures still propagate when neither exists.
                if (TryReclaimStaleMarker(markerPath)) index--;
            }
        }

        throw new IOException("Unable to reserve a unique output path.");
    }

    public async Task<OutputReservation> CommitAsync(OutputReservation reservation, string stagingPath, CancellationToken cancellationToken = default)
        => await CommitTrackedAsync(reservation, stagingPath, _ => Task.CompletedTask, cancellationToken).ConfigureAwait(false);

    public async Task<OutputReservation> CommitTrackedAsync(OutputReservation reservation, string stagingPath,
        Func<OutputReservation, Task> beforeCommit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        if (!Path.IsPathRooted(stagingPath) || !File.Exists(stagingPath)) throw new FileNotFoundException("The staging output does not exist.", stagingPath);
        cancellationToken.ThrowIfCancellationRequested();

        var current = reservation;
        for (var attempt = 0; attempt < 100_000; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOwnership(current);
            if (PathExists(current.OutputPath))
            {
                await ReleaseAsync(current, cancellationToken).ConfigureAwait(false);
                current = await ReserveAsync(current.TaskId, Path.GetDirectoryName(current.OutputPath)!, Path.GetFileName(current.OutputPath), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await beforeCommit(current).ConfigureAwait(false);
                File.Move(stagingPath, current.OutputPath, overwrite: false);
                TryDeleteMarker(current.MarkerPath);
                return current;
            }
            catch (IOException) when (PathExists(current.OutputPath))
            {
                await ReleaseAsync(current, cancellationToken).ConfigureAwait(false);
                current = await ReserveAsync(current.TaskId, Path.GetDirectoryName(current.OutputPath)!, Path.GetFileName(current.OutputPath), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("Unable to commit the output without replacing an existing file.");
    }

    public Task ReleaseAsync(OutputReservation reservation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        cancellationToken.ThrowIfCancellationRequested();
        if (reservation.TaskId == Guid.Empty) return Task.CompletedTask;
        try
        {
            if (File.Exists(reservation.MarkerPath)) EnsureOwnership(reservation);
            TryDeleteMarker(reservation.MarkerPath);
        }
        catch (FileNotFoundException)
        {
            // A committed reservation has no marker.
        }
        return Task.CompletedTask;
    }

    private static void EnsureOwnership(OutputReservation reservation)
    {
        if (!File.Exists(reservation.MarkerPath)) throw new InvalidOperationException("The output reservation is no longer owned by this task.");
        var content = File.ReadAllText(reservation.MarkerPath, Encoding.UTF8);
        if (!content.StartsWith(reservation.TaskId.ToString("D", CultureInfo.InvariantCulture) + "|", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The output reservation belongs to another task.");
        }
    }

    private static bool TryReclaimStaleMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var parts = File.ReadAllText(path, Encoding.UTF8).Split('|');
            var validTimestamp = parts.Length == 3 && DateTimeOffset.TryParse(
                parts[2],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _);
            var ownerAlive = parts.Length == 3 &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) &&
                IsProcessAlive(processId);
            if (!validTimestamp || !Guid.TryParse(parts[0], out _) || ownerAlive) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            // The owner may be completing a commit or another process may be reclaiming it.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the marker when its ownership cannot be established.
        }

        return false;
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        if (processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsMarkerCollision(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void TryDeleteMarker(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        GC.SuppressFinalize(this);
    }
}
