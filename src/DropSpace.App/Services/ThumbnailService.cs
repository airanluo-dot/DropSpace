using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace DropSpace.App.Services;

public sealed class ThumbnailService(
    IPayloadStore payloadStore,
    ILogger<ThumbnailService> logger,
    ISettingsService settingsService)
{
    private readonly SemaphoreSlim _gate = new(4, 4);

    public async Task<BitmapImage?> LoadAsync(DropItem item, uint size = 64, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var owned = LoadOwnedAsync(item, size, cancellationToken);
        // Presentation can stop waiting while this owner retains the stream and its
        // admission slot until native completion. Observe any late failure as well.
        _ = owned.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await owned.WaitAsync(cancellationToken);
    }

    private async Task<BitmapImage?> LoadOwnedAsync(DropItem item, uint size, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                if (item.Kind == ItemKind.Image && item.Payload is not null)
                {
                    var file = await NativeAsyncLifetime.AwaitAsync(StorageFile.GetFileFromPathAsync(payloadStore.ResolvePath(item.Payload.RelativePath)), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    using var stream = await NativeAsyncLifetime.AwaitAsync(file.OpenReadAsync(), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var settings = await settingsService.LoadAsync(cancellationToken);
                    await ImageDecoderPreflight.ValidateAsync(stream, settings.MaxImageBytes, settings.MaxImagePixels, cancellationToken);
                    stream.Seek(0);
                    var image = new BitmapImage
                    {
                        DecodePixelWidth = checked((int)size),
                    };
                    await NativeAsyncLifetime.AwaitAsync(image.SetSourceAsync(stream), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return image;
                }

                if (item.File is not null && item.Status == ItemStatus.Available)
                {
                    StorageItemThumbnail? thumbnail;
                    if (item.File.EntryKind == FileEntryKind.Folder)
                    {
                        var folder = await NativeAsyncLifetime.AwaitAsync(StorageFolder.GetFolderFromPathAsync(item.File.OriginalPath), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        thumbnail = await NativeAsyncLifetime.AwaitAsync(folder.GetThumbnailAsync(ThumbnailMode.ListView, size, ThumbnailOptions.UseCurrentScale), cancellationToken);
                    }
                    else
                    {
                        var file = await NativeAsyncLifetime.AwaitAsync(StorageFile.GetFileFromPathAsync(item.File.OriginalPath), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        thumbnail = await NativeAsyncLifetime.AwaitAsync(file.GetThumbnailAsync(ThumbnailMode.ListView, size, ThumbnailOptions.UseCurrentScale), cancellationToken);
                    }

                    if (thumbnail is null)
                    {
                        return null;
                    }

                    using (thumbnail)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var image = new BitmapImage
                        {
                            DecodePixelWidth = checked((int)size),
                        };
                        await NativeAsyncLifetime.AwaitAsync(image.SetSourceAsync(thumbnail), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        return image;
                    }
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException or IOException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                logger.LogInformation(exception, "Thumbnail unavailable for item {ItemId}.", item.Id);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
