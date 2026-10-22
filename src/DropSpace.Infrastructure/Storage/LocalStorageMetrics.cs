using DropSpace.Core.Abstractions;

namespace DropSpace.Infrastructure.Storage;

public sealed class LocalStorageMetrics(AppStoragePaths paths) : ILocalStorageMetrics
{
    public string RootPath => paths.Root;

    // AI lyric results remain cache. Packages, partial downloads and module-owned
    // data belong to DLC, including retained versions awaiting cleanup.
    private readonly string[] _dlcRoots =
    [
        Path.Combine(paths.Root, "AiLyrics", "Models"),
        Path.Combine(paths.Root, "AiLyrics", "Runtime"),
        Path.Combine(paths.Root, "AiLyrics", "Staging"),
        Path.Combine(paths.Root, "NeteaseEnhancement"),
        Path.Combine(paths.Root, "Modules"),
    ];

    public Task<long?> GetByteLengthAsync(CancellationToken cancellationToken = default) => MeasureAsync(false, cancellationToken);

    public Task<long?> GetDlcByteLengthAsync(CancellationToken cancellationToken = default) => MeasureAsync(true, cancellationToken);

    private Task<long?> MeasureAsync(bool dlc, CancellationToken cancellationToken) => Task.Run<long?>(() =>
    {
        try
        {
            if (!Directory.Exists(paths.Root)) return 0;
            if (File.GetAttributes(paths.Root).HasFlag(FileAttributes.ReparsePoint)) return null;
            var enumeration = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            };
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(paths.Root);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDlc = _dlcRoots.Any(root => string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) ||
                    directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (!dlc && isDlc) continue;
                foreach (var child in Directory.EnumerateDirectories(directory, "*", enumeration))
                {
                    if (!dlc || isDlc || _dlcRoots.Any(root => string.Equals(root, child, StringComparison.OrdinalIgnoreCase) ||
                        root.StartsWith(child + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) pending.Push(child);
                }
                if (isDlc != dlc) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "*", enumeration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { total = checked(total + new FileInfo(path).Length); }
                    catch (FileNotFoundException) { /* A cache/transfer may finish during this snapshot. */ }
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
