using DropSpace.Core.Transfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DropSpace.App.Services;

internal static class ImageDecoderPreflight
{
    internal static async Task<BitmapDecoder> ValidateAsync(
        IRandomAccessStream stream, long maxBytes, long maxPixels,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Size == 0 || stream.Size > (ulong)maxBytes)
            throw new InvalidDataException("Image encoded byte budget exceeded.");
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        var assessment = ClipboardImageBudgetPolicy.Create(maxBytes, maxPixels)
            .Assess(checked((long)stream.Size), decoder.PixelWidth, decoder.PixelHeight);
        if (!assessment.IsWithinBudget) throw new InvalidDataException("Image decoded memory budget exceeded.");
        return decoder;
    }
}
