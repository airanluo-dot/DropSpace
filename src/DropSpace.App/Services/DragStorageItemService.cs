using DropSpace.Core.Models;
using Windows.Storage;

namespace DropSpace.App.Services;

public sealed class DragStorageItemService
{
    public async Task<IStorageItem?> ResolveAsync(
        DropItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (item.File is null || item.Status != ItemStatus.Available)
        {
            return null;
        }

        return item.File.EntryKind == FileEntryKind.Folder
            ? await StorageFolder.GetFolderFromPathAsync(item.File.OriginalPath)
            : await StorageFile.GetFileFromPathAsync(item.File.OriginalPath);
    }
}
