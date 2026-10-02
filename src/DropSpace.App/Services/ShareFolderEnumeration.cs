namespace DropSpace.App.Services;

internal static class ShareFolderEnumeration
{
    internal static Task<IReadOnlyList<string>> EnumerateAsync(string root, CancellationToken token,
        int maximumEntries = 4096, int maximumDepth = 32) => Task.Run<IReadOnlyList<string>>(() =>
    {
        token.ThrowIfCancellationRequested();
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new DirectoryNotFoundException("The selected folder is unavailable.");
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A share folder must not be a reparse point.");
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((fullRoot, 0));
        var files = new List<string>();
        var entries = 0;
        while (pending.TryDequeue(out var current))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > maximumEntries) throw new InvalidDataException("The share folder traversal limit was exceeded.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (current.Depth >= maximumDepth) throw new InvalidDataException("The share folder depth limit was exceeded.");
                    pending.Enqueue((entry, current.Depth + 1));
                }
                else
                {
                    files.Add(entry);
                    if (files.Count > 100) throw new InvalidDataException("The share item limit was exceeded.");
                }
            }
        }
        return files;
    }, token);
}
