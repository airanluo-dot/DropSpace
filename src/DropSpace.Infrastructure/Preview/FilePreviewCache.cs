using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Preview;

public sealed class FilePreviewCache(AppStoragePaths paths) : IPreviewCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public async Task<PreviewDescriptor?> TryGetAsync(
        Guid itemId,
        int revision,
        PreviewKind kind,
        int page,
        int targetPixelWidth,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(itemId, revision, kind, page, targetPixelWidth);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<PreviewDescriptor>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            return null;
        }
    }

    public async Task PutAsync(PreviewRequest request, PreviewDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        Directory.CreateDirectory(paths.Previews);
        var path = GetPath(request.Item.Id, request.Item.Revision, descriptor.Kind, request.Page, request.TargetPixelWidth);
        var temporary = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, descriptor, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(paths.Previews))
        {
            Directory.Delete(paths.Previews, recursive: true);
        }

        return Task.CompletedTask;
    }

    private string GetPath(Guid itemId, int revision, PreviewKind kind, int page, int targetPixelWidth)
    {
        var key = string.Concat(itemId.ToString("N"), "|", revision, "|", (int)kind, "|", page, "|", targetPixelWidth);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(paths.Previews, string.Concat(hash, ".json"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
