using DropSpace.Core.Abstractions;

namespace DropSpace.Infrastructure.Storage;

public sealed class LocalStorageMetrics(AppStoragePaths paths) : ILocalStorageMetrics
{
    public string RootPath => paths.Root;

    public Task<long?> GetByteLengthAsync(CancellationToken cancellationToken = default) => Task.Run<long?>(() =>
    {
        try
        {
            if (File.GetAttributes(paths.Root).HasFlag(FileAttributes.ReparsePoint)) return null;
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            };
            long total = 0;
            foreach (var path in Directory.EnumerateFiles(paths.Root, "*", enumeration))
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
