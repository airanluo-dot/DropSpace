using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.Media;

public sealed class MediaArtworkService(DispatcherQueue dispatcher)
{
    public async Task<BitmapImage?> DecodeAsync(byte[]? bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes is null || bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) return null;
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await NativeAsyncLifetime.AwaitAsync(writer.StoreAsync(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            writer.DetachStream();
        }
        var decoder = await NativeAsyncLifetime.AwaitAsync(BitmapDecoder.CreateAsync(stream), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || decoder.PixelWidth > 8192 || decoder.PixelHeight > 8192 ||
            (ulong)decoder.PixelWidth * decoder.PixelHeight > 16_000_000) return null;
        stream.Seek(0);
        return await dispatcher.EnqueueAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            var image = new BitmapImage { DecodePixelWidth = 192 };
            // BitmapImage and SetSourceAsync remain on the UI dispatcher. Only the
            // cancellation request is off-thread; the stream lives until native completion.
            await NativeAsyncLifetime.AwaitAsync(image.SetSourceAsync(stream), token);
            token.ThrowIfCancellationRequested();
            return image;
        }).ConfigureAwait(false);
    }
}
