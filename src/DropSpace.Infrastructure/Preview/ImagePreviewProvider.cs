using DropSpace.Core.Preview;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Preview;

public sealed class ImagePreviewProvider(PreviewLimits? limits = null) : FilePreviewProviderBase(limits ?? new PreviewLimits()), IPreviewProvider
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico",
    };

    public string Id => "image";

    public int Priority => 100;

    public ValueTask<PreviewCapability> ProbeAsync(
        DropItemSnapshot item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Extension(item);
        var canPreview = item.Kind == ItemKind.Image || Extensions.Contains(extension);
        var dimensions = canPreview && !string.IsNullOrWhiteSpace(item.OriginalPath)
            ? TryReadDimensions(item.OriginalPath!, extension)
            : (Width: (int?)null, Height: (int?)null);
        if (dimensions.Width is > 0 && dimensions.Height is > 0 &&
            (long)dimensions.Width.Value * dimensions.Height.Value > Limits.MaxImagePixels)
        {
            canPreview = false;
        }

        return ValueTask.FromResult(new PreviewCapability(
            canPreview,
            PreviewKind.Image,
            Id,
            item.MimeType ?? MimeFor(extension),
            canPreview ? null : "The image is unsupported or exceeds the pixel limit.",
            item.KnownSize,
            dimensions.Width,
            dimensions.Height,
            null));
    }

    public async Task<PreviewDescriptor> LoadAsync(
        PreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var source = OpenFile(request.Item);
        const long maximumBytes = 25L * 1024 * 1024;
        var bytes = await ReadBoundedAsync(source, maximumBytes, cancellationToken).ConfigureAwait(false);
        var extension = Extension(request.Item);
        var dimensions = TryReadDimensions(bytes, extension);
        if (dimensions.Width is > 0 && dimensions.Height is > 0 && (long)dimensions.Width.Value * dimensions.Height.Value > Limits.MaxImagePixels)
        {
            throw new InvalidDataException("The image exceeds the preview pixel limit.");
        }

        return new PreviewDescriptor(
            request.Item.Id,
            PreviewKind.Image,
            request.Item.Title,
            request.Item.MimeType ?? MimeFor(extension),
            null,
            bytes,
            dimensions.Width,
            dimensions.Height,
            null,
            null,
            Metadata(("byteLength", bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static string MimeFor(string extension) => extension switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    private static (int? Width, int? Height) TryReadDimensions(string path, string extension)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bytes = new byte[64 * 1024];
            var count = stream.Read(bytes, 0, bytes.Length);
            return TryReadDimensions(bytes.AsSpan(0, count), extension);
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static (int? Width, int? Height) TryReadDimensions(ReadOnlySpan<byte> bytes, string extension)
    {
        if (extension == ".png" && bytes.Length >= 24 && bytes[..8].SequenceEqual([137, 80, 78, 71, 13, 10, 26, 10]))
        {
            return (ReadInt32BigEndian(bytes[16..20]), ReadInt32BigEndian(bytes[20..24]));
        }

        if (extension == ".bmp" && bytes.Length >= 26 && bytes[..2].SequenceEqual([(byte)'B', (byte)'M']) )
        {
            return (BitConverter.ToInt32(bytes[18..22]), Math.Abs(BitConverter.ToInt32(bytes[22..26])));
        }

        if ((extension is ".jpg" or ".jpeg") && bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var index = 2;
            while (index + 9 < bytes.Length)
            {
                if (bytes[index] != 0xFF)
                {
                    index++;
                    continue;
                }

                var marker = bytes[index + 1];
                index += 2;
                if (marker is 0xD8 or 0xD9)
                {
                    continue;
                }
                if (index + 2 > bytes.Length)
                {
                    break;
                }
                var length = (bytes[index] << 8) | bytes[index + 1];
                if (length < 2 || index + length > bytes.Length)
                {
                    break;
                }
                if ((marker is >= 0xC0 and <= 0xC3) || (marker is >= 0xC5 and <= 0xC7) ||
                    (marker is >= 0xC9 and <= 0xCB) || (marker is >= 0xCD and <= 0xCF))
                {
                    return ((bytes[index + 5] << 8) | bytes[index + 6], (bytes[index + 3] << 8) | bytes[index + 4]);
                }
                index += length;
            }
        }

        if (extension == ".gif" && bytes.Length >= 10 &&
            (bytes[..6].SequenceEqual("GIF89a"u8) || bytes[..6].SequenceEqual("GIF87a"u8)))
        {
            return (BitConverter.ToUInt16(bytes[6..8]), BitConverter.ToUInt16(bytes[8..10]));
        }

        if (extension == ".webp" && bytes.Length >= 30 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            if (bytes[12..16].SequenceEqual("VP8X"u8))
            {
                var width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
                var height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
                return (width, height);
            }
        }

        return (null, null);
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
}
