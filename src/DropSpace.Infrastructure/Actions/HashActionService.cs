using System.Security.Cryptography;
using DropSpace.Core.Actions;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Actions;

public sealed class HashActionService(AppStoragePaths paths) : IItemAction
{
    public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.HashSha256, "ActionHashSha256.Text", "Hash", ItemActionGroup.General, 40, true, false);

    public ItemActionCapability Evaluate(ItemSelectionSnapshot selection)
    {
        var available = selection.IsSingle && selection.Single.OriginalPath is not null &&
            selection.Single.Status == Core.Models.ItemStatus.Available &&
            selection.Single.Kind != Core.Models.ItemKind.Folder;
        return new ItemActionCapability(available, available ? null : "A readable single file is required.", Descriptor);
    }

    public async Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default)
    {
        var capability = Evaluate(context.Selection);
        if (!capability.IsAvailable) return ItemActionResult.Failure("not-available", "ActionUnavailable");
        var item = context.Selection.Single;
        var path = item.OriginalPath!;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var outputDirectory = ActionOutputPolicy.ResolveDirectory(paths, context.DestinationDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = string.Empty;
        try
        {
            await using var output = ActionOutputPolicy.CreateNewFile(
                outputDirectory,
                string.Concat(Path.GetFileName(path), ".sha256"),
                ".txt",
                out outputPath);
            var contents = string.Concat(
                Convert.ToHexString(hash).ToLowerInvariant(),
                "  ",
                Path.GetFileName(path),
                Environment.NewLine);
            await using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), 1_024, leaveOpen: true);
            await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ActionOutputPolicy.TryDeleteIncompleteOutput(outputPath);
            throw;
        }

        return ItemActionResult.Success([outputPath], messageResourceKey: "ActionCompleted");
    }

}
