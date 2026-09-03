using System.IO.Compression;
using DropSpace.Core.Actions;
using DropSpace.Core.Content;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Content;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Actions;

public sealed class ZipActionService(AppStoragePaths paths, IItemContentResolver contentResolver) : IItemAction
{
    public ZipActionService(AppStoragePaths paths) : this(paths, new ItemContentResolver(paths))
    {
    }

    private const int MaximumEntries = 10_000;
    private const long MaximumBytes = 64L * 1024 * 1024 * 1024;

    public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.CompressZip, "ActionCompressZip.Text", "ZipFolder", ItemActionGroup.Transform, 30, false, false);

    public ItemActionCapability Evaluate(ItemSelectionSnapshot selection)
    {
        var available = !selection.IsEmpty && selection.Items.All(item =>
        {
            var content = contentResolver.Resolve(item);
            return content.HasReadablePath && content.Type is (ItemContentType.File or ItemContentType.Folder or ItemContentType.Image);
        });
        return new ItemActionCapability(available, available ? null : "Select readable files or folders.", Descriptor);
    }

    public async Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default)
    {
        var capability = Evaluate(context.Selection);
        if (!capability.IsAvailable) return ItemActionResult.Failure("not-available", "ActionUnavailable");
        var root = ActionOutputPolicy.ResolveDirectory(paths, context.DestinationDirectory);
        Directory.CreateDirectory(root);
        string? archivePath = null;
        try
        {
            await using var output = ActionOutputPolicy.CreateNewFile(
                root,
                context.Selection.Items.Count == 1 ? GetEntryName(context.Selection.Items[0], contentResolver) : "DropSpace files",
                ".zip",
                out archivePath);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            var budget = new ArchiveBudget();
            foreach (var selected in context.Selection.Items)
            {
                var content = contentResolver.Resolve(selected);
                var source = content.ReadablePath!;
                var baseName = GetEntryName(selected, contentResolver);
                if (File.GetAttributes(source).HasFlag(FileAttributes.Directory))
                {
                    await AddDirectoryAsync(archive, source, baseName, archivePath!, budget, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await AddFileAsync(archive, source, baseName, budget, cancellationToken).ConfigureAwait(false);
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return ItemActionResult.Success([archivePath!], messageResourceKey: "ActionCompleted");
        }
        catch
        {
            ActionOutputPolicy.TryDeleteIncompleteOutput(archivePath);
            throw;
        }
    }

    private static async Task AddDirectoryAsync(
        ZipArchive archive,
        string root,
        string prefix,
        string archivePath,
        ArchiveBudget budget,
        CancellationToken cancellationToken)
    {
        var pending = new Queue<(string Path, string Name)>();
        pending.Enqueue((root, prefix));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            var attributes = File.GetAttributes(current.Path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(current.Path))
            {
                if (PathsEqual(child, archivePath)) continue;
                var childName = string.Concat(current.Name, "/", SanitizeEntryName(Path.GetFileName(child)));
                var childAttributes = File.GetAttributes(child);
                if (childAttributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (childAttributes.HasFlag(FileAttributes.Directory)) pending.Enqueue((child, childName));
                else await AddFileAsync(archive, child, childName, budget, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static async Task AddFileAsync(ZipArchive archive, string path, string name, ArchiveBudget budget, CancellationToken cancellationToken)
    {
        if (++budget.Entries > MaximumEntries) throw new InvalidDataException("The ZIP item limit was exceeded.");
        var info = new FileInfo(path);
        checked { budget.Bytes += info.Length; }
        if (budget.Bytes > MaximumBytes) throw new InvalidDataException("The ZIP byte limit was exceeded.");
        var uniqueName = GetUniqueEntryName(name, budget);
        var entry = archive.CreateEntry(uniqueName, CompressionLevel.Fastest);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = entry.Open();
        await input.CopyToAsync(output, 81_920, cancellationToken).ConfigureAwait(false);
    }

    private static string SanitizeEntryName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "item" : name.Replace('\0', '_').Replace('/', '_').Replace('\\', '_');

    private static string GetEntryName(DropItemSnapshot item, IItemContentResolver contentResolver)
    {
        var content = contentResolver.Resolve(item);
        var fallback = content.ReadablePath is { } path ? Path.GetFileName(path) : item.Title;
        return SanitizeEntryName(string.IsNullOrWhiteSpace(item.Title) ? fallback : item.Title);
    }

    private static string GetUniqueEntryName(string name, ArchiveBudget budget)
    {
        var candidate = SanitizeEntryName(name);
        if (budget.EntryNames.Add(candidate))
        {
            return candidate;
        }

        var extension = Path.GetExtension(candidate);
        var stem = extension.Length == 0 ? candidate : candidate[..^extension.Length];
        for (var suffix = 1; suffix <= MaximumEntries; suffix++)
        {
            var alternative = string.Concat(stem, " (", suffix, ")", extension);
            if (budget.EntryNames.Add(alternative))
            {
                return alternative;
            }
        }

        throw new InvalidDataException("The ZIP entry name collision limit was exceeded.");
    }

    private sealed class ArchiveBudget
    {
        public int Entries { get; set; }

        public long Bytes { get; set; }

        public HashSet<string> EntryNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
