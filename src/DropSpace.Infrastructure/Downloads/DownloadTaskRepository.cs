using System.Text.Json;
using DropSpace.Core.Downloads;

namespace DropSpace.Infrastructure.Downloads;

/// <summary>Operational task journal, independent of the existing preferences transaction.</summary>
public sealed class DownloadTaskRepository(string root) : IDownloadTaskRepository
{
    public Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
        DownloadStorage.WriteAsync(Path.Combine(root, snapshot.Id.ToString("N") + ".json"), snapshot, cancellationToken);

    public async Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root)) return [];
        var result = new List<DownloadTaskSnapshot>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
            DownloadStorage.Safe(root, path);
            if (new FileInfo(path).Length > 65536) continue;
            try
            {
                var item = JsonSerializer.Deserialize<DownloadTaskSnapshot>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                if (item?.Id == id && item.Request is { } request && request.TaskId == id && DirectFileRequestFactory.TryParseUrl(request.Url, out _) &&
                    Path.IsPathFullyQualified(item.Request.OutputDirectory) &&
                    !string.IsNullOrWhiteSpace(item.Request.OutputFileName) &&
                    item.Request.OutputFileName == new FileNameSanitizer().Sanitize(item.Request.OutputFileName) &&
                    Enum.IsDefined(item.State) &&
                    Path.GetDirectoryName(item.OutputPath)?.Equals(item.Request.OutputDirectory, StringComparison.OrdinalIgnoreCase) == true)
                    result.Add(item);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            { /* Keep damaged journals for diagnosis; never erase user outputs. */ }
        }
        return result;
    }
}
