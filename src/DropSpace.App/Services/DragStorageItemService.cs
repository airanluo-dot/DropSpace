using DropSpace.Core.Models;
using DropSpace.Core.Abstractions;
using Windows.Storage;

namespace DropSpace.App.Services;

public sealed class DragStorageItemService(IPayloadStore payloadStore)
{
    private readonly SemaphoreSlim _gate = new(8, 8);

    public async Task<IStorageItem?> ResolveAsync(
        DropItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (item.Status != ItemStatus.Available)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (item.File is { } file)
            {
                return file.EntryKind == FileEntryKind.Folder
                    ? await StorageFolder.GetFolderFromPathAsync(file.OriginalPath)
                    : await StorageFile.GetFileFromPathAsync(file.OriginalPath);
            }

            if (item.Kind == ItemKind.Image && item.Payload is { } payload)
            {
                return await StorageFile.GetFileFromPathAsync(payloadStore.ResolvePath(payload.RelativePath));
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
