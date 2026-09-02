using System.IO.Compression;
using DropSpace.Core.Actions;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Actions;

public sealed class ZipActionService(AppStoragePaths paths) : IItemAction
{
    private const int MaximumEntries = 10_000;
    private const long MaximumBytes = 64L * 1024 * 1024 * 1024;

    public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.CompressZip, "ActionCompressZip.Text", "ZipFolder", ItemActionGroup.Transform, 30, false, false);

    public ItemActionCapability Evaluate(ItemSelectionSnapshot selection)
    {
        var available = !selection.IsEmpty && selection.Items.All(item =>
            item.OriginalPath is not null &&
            item.Status == Core.Models.ItemStatus.Available &&
            (item.Kind is Core.Models.ItemKind.File or Core.Models.ItemKind.Folder));
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
                context.Selection.Items.Count == 1 ? Path.GetFileName(context.Selection.Items[0].OriginalPath!) : "DropSpace files",
                ".zip",
                out archivePath);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            var budget = new ArchiveBudget();
            foreach (var selected in context.Selection.Items)
            {
                var source = selected.OriginalPath!;
                var baseName = SanitizeEntryName(Path.GetFileName(source));
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
            if (archivePath is not null)
            {
                TryDelete(archivePath);
            }

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
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = entry.Open();
        await input.CopyToAsync(output, 81_920, cancellationToken).ConfigureAwait(false);
    }

    private static string SanitizeEntryName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "item" : name.Replace('\0', '_').Replace('/', '_').Replace('\\', '_');

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ArchiveBudget
    {
        public int Entries { get; set; }

        public long Bytes { get; set; }
    }
}
