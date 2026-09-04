using System.Collections.Generic;
using System.Runtime.InteropServices;
using DropSpace.Core.Content;
using Windows.Graphics.Imaging;

namespace DropSpace.App.Services;

internal static class WindowsImageCodecPreflight
{
    private const int HeaderBytes = 16;

    public static bool CanDecode(string path, string? extension, string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                HeaderBytes,
                FileOptions.SequentialScan);
            var read = input.Read(header);
            var detectedExtension = DetectExtension(header[..read]);
            if (detectedExtension is null)
            {
                return false;
            }

            var pathExtension = NormalizeCodecExtension(Path.GetExtension(path));
            var declaredExtension = NormalizeCodecExtension(extension);
            var declaredMime = NormalizeRequestedFormat(mimeType);
            if ((!string.IsNullOrEmpty(pathExtension) &&
                 !string.Equals(pathExtension, detectedExtension, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(declaredExtension) &&
                 !string.Equals(declaredExtension, detectedExtension, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(declaredMime) &&
                 !string.Equals(declaredMime, detectedExtension, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return HasRegisteredExtension(
                BitmapDecoder.GetDecoderInformationEnumerator(),
                detectedExtension);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    public static bool CanEncode(string? outputFormat)
    {
        var normalizedExtension = NormalizeRequestedFormat(outputFormat);
        if (string.IsNullOrEmpty(normalizedExtension))
        {
            return false;
        }

        try
        {
            return HasRegisteredExtension(
                BitmapEncoder.GetEncoderInformationEnumerator(),
                normalizedExtension);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    internal static string? DetectExtension(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 &&
            header[0] == 0x89 &&
            header[1] == 0x50 &&
            header[2] == 0x4E &&
            header[3] == 0x47 &&
            header[4] == 0x0D &&
            header[5] == 0x0A &&
            header[6] == 0x1A &&
            header[7] == 0x0A)
        {
            return ".png";
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ".jpg";
        }

        if (header.Length >= 4 &&
            header[0] == (byte)'G' &&
            header[1] == (byte)'I' &&
            header[2] == (byte)'F' &&
            header[3] == (byte)'8')
        {
            return ".gif";
        }

        if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M')
        {
            return ".bmp";
        }

        if (header.Length >= 4 &&
            ((header[0] == (byte)'I' && header[1] == (byte)'I' && header[2] == 0x2A && header[3] == 0x00) ||
             (header[0] == (byte)'M' && header[1] == (byte)'M' && header[2] == 0x00 && header[3] == 0x2A)))
        {
            return ".tiff";
        }

        if (header.Length >= 12 &&
            header[0] == (byte)'R' &&
            header[1] == (byte)'I' &&
            header[2] == (byte)'F' &&
            header[3] == (byte)'F' &&
            header[8] == (byte)'W' &&
            header[9] == (byte)'E' &&
            header[10] == (byte)'B' &&
            header[11] == (byte)'P')
        {
            return ".webp";
        }

        if (header.Length >= 4 &&
            header[0] == 0x00 &&
            header[1] == 0x00 &&
            header[2] == 0x01 &&
            header[3] == 0x00)
        {
            return ".ico";
        }

        return null;
    }

    private static string NormalizeCodecExtension(string? value) =>
        NormalizeRequestedFormat(value) switch
        {
            ".jpeg" => ".jpg",
            ".tif" => ".tiff",
            var extension => extension,
        };

    private static string NormalizeRequestedFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            "image/tiff" => ".tiff",
            "image/webp" => ".webp",
            "image/x-icon" => ".ico",
            _ => NormalizeCodecExtensionFromPath(trimmed),
        };
    }

    private static string NormalizeCodecExtensionFromPath(string value)
    {
        var extension = ItemContentPolicy.NormalizeExtension(value);
        return extension is ".jpeg" or ".tif"
            ? extension == ".jpeg" ? ".jpg" : ".tiff"
            : extension;
    }

    private static bool HasRegisteredExtension(
        IEnumerable<BitmapCodecInformation> codecs,
        string extension)
    {
        foreach (var codec in codecs)
        {
            foreach (var candidate in codec.FileExtensions)
            {
                if (string.Equals(
                        NormalizeCodecExtensionFromPath(candidate),
                        extension,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

