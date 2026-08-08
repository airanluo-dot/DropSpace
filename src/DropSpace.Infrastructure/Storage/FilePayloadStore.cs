using System.Security.Cryptography;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;

namespace DropSpace.Infrastructure.Storage;

public sealed class FilePayloadStore(AppStoragePaths paths) : IPayloadStore
{
    public async Task<PayloadRecord> WriteAsync(
        string kind,
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        paths.EnsureCreated();
        var id = Guid.NewGuid();
        var extension = string.Equals(kind, "images", StringComparison.OrdinalIgnoreCase) ? ".png" : ".txt";
        var relativePath = PayloadPathPolicy.CreateRelativePath(kind, id, extension);
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
        if (File.Exists(path))
        {
            File.Delete(path);
        }

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
