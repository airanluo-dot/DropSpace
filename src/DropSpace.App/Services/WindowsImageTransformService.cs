using DropSpace.Core.Actions;
using DropSpace.Core.Content;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Actions;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace DropSpace.App.Services;

public sealed class WindowsImageTransformService(IItemContentResolver contentResolver) : IImageTransformService
{
    private const int MaximumDimension = 16_384;
    private const long MaximumPixels = 64L * 1024 * 1024;
    private const int MaximumEncodedBytes = 256 * 1024 * 1024;

    public Task<ItemActionResult> ResizeAsync(
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
        var content = contentResolver.Resolve(item);
        var validation = ValidateImage(content, destinationDirectory, width, height, outputFormat, requireFormat: false);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        // A new SoftwareBitmap is encoded, so source metadata is not copied. Keep the flag in
        // the interface for callers that explicitly request the privacy-preserving path.
        _ = stripMetadata;
        return TransformAsync(
            item,
            content,
            destinationDirectory,
            outputFormat,
            width,
            height,
            keepAspectRatio,
            "resized",
            cancellationToken);
    }

    public Task<ItemActionResult> ConvertAsync(
        DropItemSnapshot item,
        string destinationDirectory,
        string outputFormat,
        int? width = null,
        int? height = null,
        bool keepAspectRatio = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var content = contentResolver.Resolve(item);
        var validation = ValidateImage(
            content,
            destinationDirectory,
            width,
            height,
            outputFormat,
            requireFormat: true);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        return TransformAsync(
            item,
            content,
            destinationDirectory,
            outputFormat,
            width,
            height,
            keepAspectRatio,
            "converted",
            cancellationToken);
    }

    public Task<ItemActionResult> StripMetadataAsync(
        DropItemSnapshot item,
        string destinationDirectory,
        string? outputFormat = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var content = contentResolver.Resolve(item);
        var validation = ValidateImage(content, destinationDirectory, null, null, outputFormat, requireFormat: false);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        return TransformAsync(
            item,
            content,
            destinationDirectory,
            outputFormat,
            width: null,
            height: null,
            keepAspectRatio: true,
            outputSuffix: "clean",
            cancellationToken: cancellationToken);
    }

    private static async Task<ItemActionResult> TransformAsync(
        DropItemSnapshot item,
        ResolvedItemContent content,
        string destinationDirectory,
        string? outputFormat,
        int? width,
        int? height,
        bool keepAspectRatio,
        string outputSuffix,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var source = await StorageFile.GetFileFromPathAsync(content.ReadablePath!);
        cancellationToken.ThrowIfCancellationRequested();
        using var input = await source.OpenReadAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var decoder = await ImageDecoderPreflight.ValidateAsync(input, MaximumEncodedBytes, MaximumPixels, cancellationToken);
        if (decoder.PixelWidth is 0 or > MaximumDimension || decoder.PixelHeight is 0 or > MaximumDimension ||
            (long)decoder.PixelWidth * decoder.PixelHeight > MaximumPixels)
        {
            throw new InvalidDataException("The source image dimensions are not supported.");
        }

        var target = width.HasValue && height.HasValue
            ? CalculateSize(decoder.PixelWidth, decoder.PixelHeight, width.Value, height.Value, keepAspectRatio)
            : (Width: (int)decoder.PixelWidth, Height: (int)decoder.PixelHeight);
        if ((long)target.Width * target.Height > MaximumPixels)
        {
            throw new InvalidDataException("The requested image dimensions are not supported.");
        }
        var encoder = ResolveEncoder(outputFormat, content.Extension, content.MimeType);

        string? outputPath = null;
        try
        {
            // Reserve the final path atomically before encoding, then let the WinRT encoder write
            // directly to that file. This avoids a second full-size encoded byte buffer and keeps
            // an incomplete export inside the shared cleanup policy.
            using (ActionOutputPolicy.CreateNewFile(
                destinationDirectory,
                string.Concat(Path.GetFileNameWithoutExtension(item.Title), "-", outputSuffix),
                encoder.Extension,
                out outputPath))
            {
            }

            var outputFile = await StorageFile.GetFileFromPathAsync(outputPath!);
            using var destination = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            cancellationToken.ThrowIfCancellationRequested();
            var imageEncoder = await BitmapEncoder.CreateAsync(encoder.EncoderId, destination);
            imageEncoder.SetSoftwareBitmap(bitmap);
            imageEncoder.BitmapTransform.ScaledWidth = checked((uint)target.Width);
            imageEncoder.BitmapTransform.ScaledHeight = checked((uint)target.Height);
            imageEncoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            // Re-encoding through a SoftwareBitmap intentionally omits arbitrary source metadata.
            await imageEncoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();

            if (destination.Size is 0 or > MaximumEncodedBytes)
            {
                throw new InvalidDataException("The encoded image is too large to export safely.");
            }

            return ItemActionResult.Success([outputPath!], messageResourceKey: "ActionCompleted");
        }
        catch
        {
            ActionOutputPolicy.TryDeleteIncompleteOutput(outputPath);
            throw;
        }
    }

    private static ItemActionResult? ValidateImage(
        ResolvedItemContent content,
        string destinationDirectory,
        int? width,
        int? height,
        string? outputFormat,
        bool requireFormat)
    {
        if (!content.IsImage || !content.HasReadablePath ||
            !WindowsImageCodecPreflight.CanDecode(content.ReadablePath!, content.Extension, content.MimeType))
        {
            return ItemActionResult.Failure("image-codec-unavailable", "ActionSourceUnavailable");
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return ItemActionResult.Failure("output-directory-unavailable", "ActionOutputUnavailable");
        }

        if (width.HasValue != height.HasValue ||
            (width.HasValue && (width.Value is < 1 or > MaximumDimension || height!.Value is < 1 or > MaximumDimension)))
        {
            return ItemActionResult.Failure("invalid-image-parameters", "ActionParametersRequired");
        }

        if (requireFormat && string.IsNullOrWhiteSpace(outputFormat))
        {
            return ItemActionResult.Failure("image-format-required", "ActionParametersRequired");
        }

        if (!string.IsNullOrWhiteSpace(outputFormat) && !IsSupportedFormat(outputFormat))
        {
            return ItemActionResult.Failure("unsupported-image-format", "ActionParametersRequired");
        }

        return null;
    }

    private static bool IsSupportedFormat(string format) => format.Trim().ToLowerInvariant() switch
    {
        ".png" or "png" or
        ".jpg" or ".jpeg" or "jpg" or "jpeg" or
        ".bmp" or "bmp" => true,
        _ => false,
    };

    private static (Guid EncoderId, string Extension) ResolveEncoder(
        string? requestedFormat,
        string? sourceExtension,
        string? mimeType)
    {
        var format = string.IsNullOrWhiteSpace(requestedFormat)
            ? sourceExtension ?? mimeType
            : requestedFormat;
        (Guid EncoderId, string Extension) encoder = format?.Trim().ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or "jpg" or "jpeg" or "image/jpeg" =>
                (BitmapEncoder.JpegEncoderId, ".jpg"),
            ".bmp" or "bmp" or "image/bmp" =>
                (BitmapEncoder.BmpEncoderId, ".bmp"),
            ".png" or "png" or "image/png" =>
                (BitmapEncoder.PngEncoderId, ".png"),
            _ when !string.IsNullOrWhiteSpace(requestedFormat) =>
                throw new InvalidDataException("The requested image format is not supported."),
            _ => (BitmapEncoder.PngEncoderId, ".png"),
        };
        if (!WindowsImageCodecPreflight.CanEncode(encoder.Extension))
        {
            throw new InvalidDataException("The requested image encoder is unavailable.");
        }

        return encoder;
    }

    private static (int Width, int Height) CalculateSize(
        uint sourceWidth,
        uint sourceHeight,
        int width,
        int height,
        bool keepAspectRatio)
    {
        if (!keepAspectRatio)
        {
            return (width, height);
        }

        var scale = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
        return (
            Math.Clamp((int)Math.Round(sourceWidth * scale), 1, MaximumDimension),
            Math.Clamp((int)Math.Round(sourceHeight * scale), 1, MaximumDimension));
    }

}

public sealed class ImageTransformActionService(
    IImageTransformService transforms,
    DropSpace.Infrastructure.Storage.AppStoragePaths paths,
    IItemContentResolver contentResolver) : IItemAction
{
    public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.ResizeImage, "ActionResizeImageMenuItem.Text", "ResizeImage", ItemActionGroup.Transform, 20, true, false);

    public ItemActionCapability Evaluate(ItemSelectionSnapshot selection)
    {
        var available = IsAvailableImage(selection, contentResolver);
        return new ItemActionCapability(available, available ? null : "Select one available image.", Descriptor);
    }

    internal static bool IsAvailableImage(ItemSelectionSnapshot selection, IItemContentResolver contentResolver)
    {
        if (!selection.IsSingle || contentResolver.Resolve(selection.Single) is not
            { IsImage: true, HasReadablePath: true, ReadablePath: not null } content)
        {
            return false;
        }

        return WindowsImageCodecPreflight.CanDecode(content.ReadablePath, content.Extension, content.MimeType);
    }

    public Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default)
    {
        var capability = Evaluate(context.Selection);
        if (!capability.IsAvailable)
        {
            return Task.FromResult(ItemActionResult.Failure("not-available", "ActionUnavailable"));
        }

        if (context.Width is null || context.Height is null)
        {
            return Task.FromResult(ItemActionResult.Failure("image-size-required", "ActionParametersRequired"));
        }

        var directory = ActionOutputPolicy.ResolveDirectory(paths, context.DestinationDirectory);
        return transforms.ResizeAsync(
            context.Selection.Single,
            directory,
            context.Width.Value,
            context.Height.Value,
            context.KeepAspectRatio,
            context.OutputFormat,
            stripMetadata: true,
            cancellationToken: cancellationToken);
    }
}

public sealed class ConvertImageActionService(
    IImageTransformService transforms,
    DropSpace.Infrastructure.Storage.AppStoragePaths paths,
    IItemContentResolver contentResolver) : IItemAction
{
    public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.ConvertImage, "ActionConvertImage.Text", "Photo2", ItemActionGroup.Transform, 21, true, false);

    public ItemActionCapability Evaluate(ItemSelectionSnapshot selection)
    {
        var available = ImageTransformActionService.IsAvailableImage(selection, contentResolver);
        return new ItemActionCapability(available, available ? null : "Select one available image.", Descriptor);
    }

    public Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default)
    {
        var capability = Evaluate(context.Selection);
        if (!capability.IsAvailable)
        {
            return Task.FromResult(ItemActionResult.Failure("not-available", "ActionUnavailable"));
        }

        if (string.IsNullOrWhiteSpace(context.OutputFormat) || context.Width.HasValue != context.Height.HasValue)
        {
            return Task.FromResult(ItemActionResult.Failure("image-conversion-parameters-required", "ActionParametersRequired"));
        }

        var directory = ActionOutputPolicy.ResolveDirectory(paths, context.DestinationDirectory);
        return transforms.ConvertAsync(
            context.Selection.Single,
            directory,
            context.OutputFormat,
            context.Width,
            context.Height,
            context.KeepAspectRatio,
            cancellationToken);
    }
}

public sealed class StripMetadataActionService(
    IImageTransformService transforms,
    DropSpace.Infrastructure.Storage.AppStoragePaths paths,
    IItemContentResolver contentResolver) : IItemAction
{
    public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.StripMetadata, "ActionStripMetadata.Text", "ProtectiveCover", ItemActionGroup.Transform, 22, true, false);

    public ItemActionCapability Evaluate(ItemSelectionSnapshot selection)
    {
        var available = ImageTransformActionService.IsAvailableImage(selection, contentResolver);
        return new ItemActionCapability(available, available ? null : "Select one available image.", Descriptor);
    }

    public Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default)
    {
        var capability = Evaluate(context.Selection);
        if (!capability.IsAvailable)
        {
            return Task.FromResult(ItemActionResult.Failure("not-available", "ActionUnavailable"));
        }

        var directory = ActionOutputPolicy.ResolveDirectory(paths, context.DestinationDirectory);
        return transforms.StripMetadataAsync(context.Selection.Single, directory, context.OutputFormat, cancellationToken);
    }
}
