using DropSpace.Core.Models;
using Windows.Storage;

namespace DropSpace.App.Services;

public sealed class DragStorageItemService
{
    private readonly SemaphoreSlim _gate = new(8, 8);

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

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return item.File.EntryKind == FileEntryKind.Folder
                ? await StorageFolder.GetFolderFromPathAsync(item.File.OriginalPath)
                : await StorageFile.GetFileFromPathAsync(item.File.OriginalPath);
        }
        finally
        {
            _gate.Release();
        }
    }
}
