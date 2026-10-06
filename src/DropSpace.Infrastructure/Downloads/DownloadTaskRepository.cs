using System.Text.Json;
using DropSpace.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Downloads;

/// <summary>Operational task journal, independent of the existing preferences transaction.</summary>
public sealed class DownloadTaskRepository(string root, ILogger<DownloadTaskRepository>? logger = null) : IDownloadTaskRepository
{
    public string? RecoveryError { get; private set; }
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(DownloadStorage.Safe(root, Path.Combine(root, id.ToString("N") + ".json")));
        return Task.CompletedTask;
    }
    public Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
        DownloadStorage.WriteAsync(Path.Combine(root, snapshot.Id.ToString("N") + ".json"), snapshot, cancellationToken);

    public async Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        RecoveryError = null;
        var result = new List<DownloadTaskSnapshot>();
        try
        {
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
                DownloadStorage.Safe(root, path);
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Oversized task journal.");
                var item = JsonSerializer.Deserialize<DownloadTaskSnapshot>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                if (item?.Id == id && item.Request is { } request && request.TaskId == id && DirectFileRequestFactory.TryParseUrl(request.Url, out _) &&
                    Path.IsPathFullyQualified(item.Request.OutputDirectory) &&
                    !string.IsNullOrWhiteSpace(item.Request.OutputFileName) &&
                    item.Request.OutputFileName == new FileNameSanitizer().Sanitize(item.Request.OutputFileName) &&
                    Enum.IsDefined(item.State) &&
                    Path.GetDirectoryName(item.OutputPath) is { } parent && DownloadStorage.SameDirectory(parent, request.OutputDirectory))
                    result.Add(item);
                else { RecoveryError = "InvalidJournal"; logger?.LogWarning("Download journal rejected: invalid task identity or directory."); }
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            { RecoveryError = error.GetType().Name; logger?.LogWarning("Download journal recovery skipped an entry: {Reason}", error.GetType().Name); }
        }
        }
        catch (DirectoryNotFoundException) { /* A new installation has no task journal yet. */ }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { RecoveryError = error.GetType().Name; logger?.LogWarning("Download journal enumeration stopped: {Reason}; recovered {Count} entries", error.GetType().Name, result.Count); }
        return result;
    }
}
