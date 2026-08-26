using DropSpace.Core.Actions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DropSpace.App.Services;

public sealed class WindowsImageTransformService : IImageTransformService
{
    public async Task<ItemActionResult> ResizeAsync(
        DropItemSnapshot item,
        string destinationDirectory,
        int width,
        int height,
        bool keepAspectRatio,
        string? outputFormat,
        bool stripMetadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.OriginalPath) || item.Kind != ItemKind.Image || width is < 1 or > 16_384 || height is < 1 or > 16_384)
        {
            return ItemActionResult.Failure("image-transform-unavailable", "ActionUnavailable");
        }

        Directory.CreateDirectory(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var source = await StorageFile.GetFileFromPathAsync(item.OriginalPath);
        cancellationToken.ThrowIfCancellationRequested();
        using var input = await source.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var target = CalculateSize(decoder.PixelWidth, decoder.PixelHeight, width, height, keepAspectRatio);
        var encoderId = outputFormat?.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or "jpg" or "jpeg" => BitmapEncoder.JpegEncoderId,
            ".bmp" or "bmp" => BitmapEncoder.BmpEncoderId,
            _ => BitmapEncoder.PngEncoderId,
        };
        var extension = encoderId == BitmapEncoder.JpegEncoderId ? ".jpg" : encoderId == BitmapEncoder.BmpEncoderId ? ".bmp" : ".png";
        var output = CreateUniquePath(destinationDirectory, Path.GetFileNameWithoutExtension(item.Title), extension);
        using var destination = new InMemoryRandomAccessStream();
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using (bitmap)
        {
            var encoder = await BitmapEncoder.CreateAsync(encoderId, destination);
            encoder.SetSoftwareBitmap(bitmap);
            encoder.BitmapTransform.ScaledWidth = checked((uint)target.Width);
            encoder.BitmapTransform.ScaledHeight = checked((uint)target.Height);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            // Re-encoding through a SoftwareBitmap intentionally omits arbitrary source metadata.
            _ = stripMetadata;
            await encoder.FlushAsync();
        }

        destination.Seek(0);
        using var reader = new DataReader(destination.GetInputStreamAt(0));
        var bytes = new byte[checked((int)destination.Size)];
        await reader.LoadAsync((uint)bytes.Length);
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(output, bytes, cancellationToken).ConfigureAwait(false);
        return ItemActionResult.Success([output], messageResourceKey: "ActionCompleted");
    }

    private static (int Width, int Height) CalculateSize(uint sourceWidth, uint sourceHeight, int width, int height, bool keepAspectRatio)
    {
        if (!keepAspectRatio) return (width, height);
        var scale = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
        return (Math.Max(1, (int)Math.Round(sourceWidth * scale)), Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    private static string CreateUniquePath(string directory, string stem, string extension)
    {
        var safeStem = string.Concat((string.IsNullOrWhiteSpace(stem) ? "DropSpace image" : stem).Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        var candidate = Path.Combine(directory, string.Concat(safeStem, extension));
        for (var index = 1; File.Exists(candidate); index++) candidate = Path.Combine(directory, string.Concat(safeStem, " (", index, ")", extension));
        return candidate;
    }
}

public sealed class ImageTransformActionService(IImageTransformService transforms) : IItemAction
{
    public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.ResizeImage, "ActionResizeImageLabel", "ResizeImage", ItemActionGroup.Transform, 20, true, false);

    public ItemActionCapability Evaluate(ItemSelectionSnapshot selection)
    {
        var available = selection.IsSingle && selection.Single.Kind == ItemKind.Image && selection.Single.OriginalPath is not null;
        return new ItemActionCapability(available, available ? null : "Select one available image.", Descriptor);
    }

    public Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default)
    {
        var capability = Evaluate(context.Selection);
        if (!capability.IsAvailable) return Task.FromResult(ItemActionResult.Failure("not-available", "ActionUnavailable"));
        var item = context.Selection.Single;
        var width = context.Width ?? 1024;
        var height = context.Height ?? 1024;
        var directory = context.DestinationDirectory ?? Path.GetDirectoryName(item.OriginalPath!) ?? Environment.CurrentDirectory;
        return transforms.ResizeAsync(item, directory, width, height, context.KeepAspectRatio, context.OutputFormat, stripMetadata: true, cancellationToken: cancellationToken);
    }
}
