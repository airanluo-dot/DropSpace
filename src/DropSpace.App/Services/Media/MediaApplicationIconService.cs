using System.Diagnostics;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Media;

public sealed class MediaApplicationIconService(MediaProcessResolver processes, MediaArtworkService artwork, ILogger<MediaApplicationIconService> logger)
{
    public async Task<BitmapImage?> LoadAsync(string source, CancellationToken token)
    {
        try { return await LoadCoreAsync(source, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            logger.LogDebug("Media application icon unavailable ({Category}).", exception.GetType().Name);
            return null;
        }
    }
    private async Task<BitmapImage?> LoadCoreAsync(string source, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var cancellation = timeout.Token;
        var id = await processes.ResolveAsync(source, cancellation).ConfigureAwait(false);
        if (id is null) return null;
        var path = await Task.Run(() => { using var process = Process.GetProcessById((int)id); return process.MainModule?.FileName; }, cancellation).ConfigureAwait(false);
        if (path is null) return null;
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellation).ConfigureAwait(false);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, 48, ThumbnailOptions.UseCurrentScale).AsTask(cancellation).ConfigureAwait(false);
        if (thumbnail is null || thumbnail.Size is 0 or > 1_048_576) return null;
        using var reader = new DataReader(thumbnail);
        var count = checked((uint)thumbnail.Size);
        if (await reader.LoadAsync(count).AsTask(cancellation).ConfigureAwait(false) != count) return null;
        var bytes = new byte[count]; reader.ReadBytes(bytes);
        return await artwork.DecodeAsync(bytes, cancellation).ConfigureAwait(false);
    }
}
