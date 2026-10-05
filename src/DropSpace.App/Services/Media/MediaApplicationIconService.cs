using System.Diagnostics;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Media;

public sealed class MediaApplicationIconService(MediaProcessResolver processes, MediaArtworkService artwork, ILogger<MediaApplicationIconService> logger)
{
    private readonly BoundedMediaOperation _reads = new(2, 2);

    public async Task<BitmapImage?> LoadAsync(string source, CancellationToken token)
    {
        try
        {
            return await _reads.RunAsync(this, nativeToken => LoadCoreAsync(source, nativeToken),
                TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or OperationCanceledException or TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            logger.LogDebug("Media application icon unavailable ({Category}).", exception.GetType().Name);
            return null;
        }
    }
    private async Task<BitmapImage?> LoadCoreAsync(string source, CancellationToken token)
    {
        var cancellation = token;
        var id = await processes.ResolveAsync(source, cancellation).ConfigureAwait(false);
        if (id is null) return null;
        var path = await Task.Run(() => { using var process = Process.GetProcessById((int)id); return process.MainModule?.FileName; }, cancellation).ConfigureAwait(false);
        if (path is null) return null;
        cancellation.ThrowIfCancellationRequested();
        var file = await NativeAsyncLifetime.AwaitAsync(StorageFile.GetFileFromPathAsync(path), cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        using var thumbnail = await NativeAsyncLifetime.AwaitAsync(file.GetThumbnailAsync(ThumbnailMode.ListView, 48, ThumbnailOptions.UseCurrentScale), cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (thumbnail is null || thumbnail.Size is 0 or > 1_048_576) return null;
        using var reader = new DataReader(thumbnail);
        var count = checked((uint)thumbnail.Size);
        if (await NativeAsyncLifetime.AwaitAsync(reader.LoadAsync(count), cancellation).ConfigureAwait(false) != count) return null;
        cancellation.ThrowIfCancellationRequested();
        var bytes = new byte[count]; reader.ReadBytes(bytes);
        return await artwork.DecodeAsync(bytes, cancellation).ConfigureAwait(false);
    }
}
