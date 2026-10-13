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

        var owned = ResolveOwnedAsync(item, cancellationToken);
        // The caller can retire while the native lookup still owns its admission
        // slot. Observe late faults without changing errors seen by an active caller.
        _ = owned.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await owned.WaitAsync(cancellationToken);
    }

    private async Task<IStorageItem?> ResolveOwnedAsync(DropItem item, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IStorageItem? resolved = null;
            if (item.File is { } file)
            {
                resolved = file.EntryKind == FileEntryKind.Folder
                    ? await NativeAsyncLifetime.AwaitAsync(StorageFolder.GetFolderFromPathAsync(file.OriginalPath), cancellationToken)
                    : await NativeAsyncLifetime.AwaitAsync(StorageFile.GetFileFromPathAsync(file.OriginalPath), cancellationToken);
            }
            else if (item.Kind == ItemKind.Image && item.Payload is { } payload)
            {
                resolved = await NativeAsyncLifetime.AwaitAsync(
                    StorageFile.GetFileFromPathAsync(payloadStore.ResolvePath(payload.RelativePath)), cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return resolved;
        }
        finally
        {
            _gate.Release();
        }
    }
}
