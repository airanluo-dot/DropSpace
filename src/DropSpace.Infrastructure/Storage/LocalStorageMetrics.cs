using DropSpace.Core.Abstractions;

namespace DropSpace.Infrastructure.Storage;

public sealed class LocalStorageMetrics(AppStoragePaths paths) : ILocalStorageMetrics
{
    public string RootPath => paths.Root;

    public Task<long?> GetByteLengthAsync(CancellationToken cancellationToken = default) => Task.Run<long?>(() =>
    {
        try
        {
            long total = 0;
            foreach (var path in Directory.EnumerateFiles(paths.Root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    total = checked(total + new FileInfo(path).Length);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            return total;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }
    }, cancellationToken);
}
