using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Media;
using DropSpace.Infrastructure.Storage;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Media;

public sealed class MediaApplicationIconService(MediaProcessResolver processes, MediaArtworkService artwork,
    AppStoragePaths paths, ILogger<MediaApplicationIconService> logger)
{
    private const int MaximumIconBytes = 1_048_576;
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
        source = MediaProcessIdentityPolicy.ApplicationIdentity(source);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())));
        var cachePath = Path.Combine(paths.MediaApplicationIcons, key + ".img");
        if (File.Exists(cachePath))
        {
            try
            {
                using var cached = File.OpenRead(cachePath);
                if (cached.Length is > 0 and <= MaximumIconBytes)
                {
                    var bytes = new byte[(int)cached.Length];
                    await cached.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                    if (await artwork.DecodeAsync(bytes, token).ConfigureAwait(false) is { } image) return image;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                logger.LogDebug("Media application icon cache unreadable ({Category}).", error.GetType().Name);
            }
        }

        using var icon = await OpenIconAsync(source, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (icon is null || icon.Size is 0 or > MaximumIconBytes) return null;
        using var reader = new DataReader(icon);
        var count = checked((uint)icon.Size);
        if (await NativeAsyncLifetime.AwaitAsync(reader.LoadAsync(count), token).ConfigureAwait(false) != count) return null;
        token.ThrowIfCancellationRequested();
        var data = new byte[count];
        reader.ReadBytes(data);
        var decoded = await artwork.DecodeAsync(data, token).ConfigureAwait(false);
        if (decoded is null) return null;
        try
        {
            Directory.CreateDirectory(paths.MediaApplicationIcons);
            var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, data, token).ConfigureAwait(false);
                File.Move(temporary, cachePath, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("Media application icon could not be saved ({Category}).", error.GetType().Name);
        }
        return decoded;
    }

    private async Task<IRandomAccessStream?> OpenIconAsync(string source, CancellationToken token)
    {
        var path = RegisteredApplicationPaths.ForExecutable(source).FirstOrDefault(File.Exists);
        if (path is null)
        {
            var id = await processes.ResolveAsync(source, token).ConfigureAwait(false);
            if (id is not null)
            {
                using var process = Process.GetProcessById((int)id);
                path = process.MainModule?.FileName;
            }
        }
        token.ThrowIfCancellationRequested();
        if (path is null)
        {
            if (!source.Contains('!')) return null;
            var logo = AppInfo.GetFromAppUserModelId(source)?.DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48));
            return logo is null ? null : await NativeAsyncLifetime.AwaitAsync(logo.OpenReadAsync(), token).ConfigureAwait(false);
        }
        var file = await NativeAsyncLifetime.AwaitAsync(StorageFile.GetFileFromPathAsync(path), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await NativeAsyncLifetime.AwaitAsync(file.GetThumbnailAsync(ThumbnailMode.ListView, 48, ThumbnailOptions.UseCurrentScale), token).ConfigureAwait(false);
    }
}
