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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                if (item.Kind == ItemKind.Image && item.Payload is not null)
                {
                    var file = await StorageFile.GetFileFromPathAsync(payloadStore.ResolvePath(item.Payload.RelativePath));
                    using var stream = await file.OpenReadAsync();
                    var settings = await settingsService.LoadAsync(cancellationToken);
                    await ImageDecoderPreflight.ValidateAsync(stream, settings.MaxImageBytes, settings.MaxImagePixels, cancellationToken);
                    stream.Seek(0);
                    var image = new BitmapImage
                    {
                        DecodePixelWidth = checked((int)size),
                    };
                    await image.SetSourceAsync(stream);
                    return image;
                }

                if (item.File is not null && item.Status == ItemStatus.Available)
                {
                    StorageItemThumbnail? thumbnail;
                    if (item.File.EntryKind == FileEntryKind.Folder)
                    {
                        var folder = await StorageFolder.GetFolderFromPathAsync(item.File.OriginalPath);
                        thumbnail = await folder.GetThumbnailAsync(ThumbnailMode.ListView, size, ThumbnailOptions.UseCurrentScale);
                    }
                    else
                    {
                        var file = await StorageFile.GetFileFromPathAsync(item.File.OriginalPath);
                        thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, size, ThumbnailOptions.UseCurrentScale);
                    }

                    if (thumbnail is null)
                    {
                        return null;
                    }

                    using (thumbnail)
                    {
                        var image = new BitmapImage
                        {
                            DecodePixelWidth = checked((int)size),
                        };
                        await image.SetSourceAsync(thumbnail);
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
