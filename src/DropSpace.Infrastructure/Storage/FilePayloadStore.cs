using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;

namespace DropSpace.Infrastructure.Storage;

public sealed class FilePayloadStore : IPayloadStore
{
    // A segment is bounded so recovery never needs to rewrite an unbounded file. The
    // journal itself is intentionally not capped: an owned delete obligation is retained
    // until the file is gone or an operator repairs the storage state.
    private const int DeferredDeleteSegmentEntries = 1_024;
    private readonly AppStoragePaths paths;
    private readonly object _deferredDeleteGate = new();
    private readonly string _deferredDeletePath;

    public FilePayloadStore(AppStoragePaths paths)
    {
        this.paths = paths;
        _deferredDeletePath = Path.Combine(paths.Data, "payload-delete.queue");
        TryDrainDeferredDeletes();
    }
    public async Task<PayloadRecord> WriteAsync(
        string kind,
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default) =>
        await WriteFileAsync(kind, null, source, maximumBytes, cancellationToken).ConfigureAwait(false);

    public async Task<PayloadRecord> WriteFileAsync(
        string kind,
        string? extension,
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        paths.EnsureCreated();
        TryDrainDeferredDeletes();
        var id = Guid.NewGuid();
        var storedExtension = string.IsNullOrWhiteSpace(extension)
            ? string.Equals(kind, "images", StringComparison.OrdinalIgnoreCase) ? ".png" : ".txt"
            : extension;
        var relativePath = PayloadPathPolicy.CreateRelativePath(kind, id, storedExtension);
        var destinationPath = ResolvePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var temporaryPath = string.Concat(destinationPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        long total = 0;
        byte[] hash;

        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81_920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81_920];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > maximumBytes)
                    {
                        throw new InvalidDataException($"Payload exceeded the {maximumBytes} byte limit.");
                    }

                    hasher.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            hash = hasher.GetHashAndReset();
            File.Move(temporaryPath, destinationPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(destinationPath);
            throw;
        }

        return new PayloadRecord(
            id,
            kind,
            relativePath,
            total,
            Convert.ToHexString(hash).ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            1);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            ResolvePath(relativePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
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

    public async Task ExportAsync(
        string relativePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var sourcePath = ResolvePath(relativePath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var temporaryPath = string.Concat(fullDestinationPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             81_920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81_920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullDestinationPath, true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public string ResolvePath(string relativePath) => PayloadPathPolicy.ResolveContainedPath(paths.Payloads, relativePath);

    private void QueueDeferredDelete(string relativePath)
    {
        lock (_deferredDeleteGate)
        {
            try
            {
                paths.EnsureCreated();
                // Failed deletes are appended to a bounded segment and flushed before
                // the caller is told that deletion failed. Duplicates are harmless and
                // are coalesced during recovery; avoiding a whole-journal rewrite here
                // keeps the failure path bounded even when the backlog is large.
                var segment = GetAppendSegmentPath();
                var bytes = Encoding.UTF8.GetBytes(string.Concat(relativePath, Environment.NewLine));
                using var stream = new FileStream(
                    segment,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4_096,
                    FileOptions.WriteThrough);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Returning from here would lose the only durable cleanup obligation. Surface
                // the journal failure so the caller can keep the item in a retryable state.
                throw new IOException("The owned payload delete journal could not be persisted.", exception);
            }
        }
    }

    private void TryDrainDeferredDeletes()
    {
        lock (_deferredDeleteGate)
        {
            try
            {
                if (!EnumerateJournalPaths().Any()) return;
                var remaining = new List<string>();
                foreach (var relativePath in ReadDeferredDeleteJournal()
                             .Where(line => !string.IsNullOrWhiteSpace(line))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var path = ResolvePath(relativePath);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        remaining.Add(relativePath);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
                    {
                        // A malformed journal line is not an owned path and must never be
                        // retried as if it were. Valid owned obligations remain durable.
                        System.Diagnostics.Debug.WriteLine($"Discarded malformed payload delete journal entry: {exception.GetType().Name}");
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
            foreach (var path in EnumerateJournalPaths().ToArray()) TryDelete(path);
            return;
        }

        var ordered = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var oldJournalPaths = EnumerateJournalPaths().ToArray();
        var generation = Guid.NewGuid().ToString("N");
        var segmentPaths = ordered
            .Chunk(DeferredDeleteSegmentEntries)
            .Select((_, index) => GetCompactionSegmentPath(generation, index))
            .ToArray();
        var temporaryPaths = new List<string>(segmentPaths.Length);
        try
        {
            for (var index = 0; index < segmentPaths.Length; index++)
            {
                var temporary = string.Concat(segmentPaths[index], ".", Guid.NewGuid().ToString("N"), ".tmp");
                temporaryPaths.Add(temporary);
                using var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4_096,
                    FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4_096, leaveOpen: true);
                foreach (var entry in ordered
                             .Skip(index * DeferredDeleteSegmentEntries)
                             .Take(DeferredDeleteSegmentEntries))
                {
                    writer.WriteLine(entry);
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Publish each complete segment under a fresh generation before removing any
            // old segment. A crash at any point therefore leaves either the old complete
            // journal or a complete replacement (or both); recovery reads the union and
            // coalesces duplicates.
            for (var index = 0; index < segmentPaths.Length; index++)
            {
                File.Move(temporaryPaths[index], segmentPaths[index]);
            }

            foreach (var oldPath in oldJournalPaths)
            {
                TryDelete(oldPath);
            }
        }
        finally
        {
            foreach (var temporary in temporaryPaths) TryDelete(temporary);
        }
    }

    private List<string> ReadDeferredDeleteJournal()
    {
        var result = new List<string>();
        foreach (var path in EnumerateJournalPaths())
        {
            result.AddRange(File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)));
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> EnumerateJournalPaths()
    {
        if (File.Exists(_deferredDeletePath))
        {
            yield return _deferredDeletePath;
        }

        if (!Directory.Exists(paths.Data)) yield break;
        foreach (var path in Directory.EnumerateFiles(paths.Data, "payload-delete.queue.*", SearchOption.TopDirectoryOnly)
                     .Where(path => IsSegmentPath(path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return path;
        }
    }

    private string GetSegmentPath(int index) =>
        string.Concat(_deferredDeletePath, ".", index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));

    private string GetAppendSegmentPath()
    {
        var appendSegments = EnumerateJournalPaths()
            .Where(IsAppendSegmentPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (appendSegments.Length == 0)
        {
            return GetSegmentPath(0);
        }

        var current = appendSegments[^1];
        var count = File.ReadLines(current).Count();
        if (count < DeferredDeleteSegmentEntries && EndsWithLineBreak(current))
        {
            return current;
        }

        var index = int.Parse(Path.GetFileName(current)[^8..], System.Globalization.CultureInfo.InvariantCulture);
        return GetSegmentPath(checked(index + 1));
    }

    private static bool EndsWithLineBreak(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return true;
        stream.Position = stream.Length - 1;
        return stream.ReadByte() is '\n' or '\r';
    }

    private string GetCompactionSegmentPath(string generation, int index) =>
        string.Concat(_deferredDeletePath, ".", generation, ".", index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));

    private static bool IsSegmentPath(string path)
    {
        var name = Path.GetFileName(path);
        var suffix = name.Split('.').LastOrDefault();
        return name.StartsWith("payload-delete.queue.", StringComparison.Ordinal) &&
            suffix is { Length: 8 } && suffix.All(static character => character is >= '0' and <= '9');
    }

    private static bool IsAppendSegmentPath(string path) =>
        IsSegmentPath(path) && Path.GetFileName(path).Length == "payload-delete.queue.".Length + 8;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            System.Diagnostics.Debug.WriteLine(exception.GetType().Name);
        }
        catch (UnauthorizedAccessException exception)
        {
            System.Diagnostics.Debug.WriteLine(exception.GetType().Name);
        }
    }
}
