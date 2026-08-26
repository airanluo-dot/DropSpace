using System.Security.Cryptography;
using DropSpace.Core.Actions;

namespace DropSpace.Infrastructure.Actions;

public sealed class HashActionService : IItemAction
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
        var outputDirectory = context.DestinationDirectory ?? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(outputDirectory);
        var outputPath = CreateUniquePath(outputDirectory, string.Concat(Path.GetFileName(path), ".sha256.txt"));
        await File.WriteAllTextAsync(outputPath, string.Concat(Convert.ToHexString(hash).ToLowerInvariant(), "  ", Path.GetFileName(path), Environment.NewLine), cancellationToken).ConfigureAwait(false);
        return ItemActionResult.Success([outputPath], messageResourceKey: "ActionCompleted");
    }

    private static string CreateUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        for (var index = 1; File.Exists(candidate); index++)
        {
            candidate = Path.Combine(directory, string.Concat(Path.GetFileNameWithoutExtension(fileName), " (", index, ")", Path.GetExtension(fileName)));
        }

        return candidate;
    }
}
