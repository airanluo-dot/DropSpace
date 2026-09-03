using DropSpace.Core.Models;

namespace DropSpace.Core.Content;

public static class ItemContentPolicy
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico",
    };

    public static ItemContentType Infer(ItemKind kind, string? extension, string? mimeType)
    {
        if (kind == ItemKind.Folder)
        {
            return ItemContentType.Folder;
        }

        if (kind is ItemKind.Text or ItemKind.Code or ItemKind.Color)
        {
            return ItemContentType.Text;
        }

        if (kind == ItemKind.Url)
        {
            return ItemContentType.Url;
        }

        if (kind == ItemKind.Image ||
            (!string.IsNullOrWhiteSpace(mimeType) && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) ||
            IsImageExtension(extension))
        {
            return ItemContentType.Image;
        }

        return kind == ItemKind.File ? ItemContentType.File : ItemContentType.Unknown;
    }

    public static bool IsImage(ItemKind kind, string? extension, string? mimeType) =>
        Infer(kind, extension, mimeType) == ItemContentType.Image;

    public static bool IsImageExtension(string? extension) =>
        ImageExtensions.Contains(NormalizeExtension(extension));

    public static string NormalizeExtension(string? extensionOrPath)
    {
        if (string.IsNullOrWhiteSpace(extensionOrPath))
        {
            return string.Empty;
        }

        var value = extensionOrPath.Trim();
        string extension;
        try
        {
            extension = value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
                (!value.StartsWith('.') && value.Contains(".", StringComparison.Ordinal))
                ? Path.GetExtension(value)
                : value;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith('.') ? extension.ToLowerInvariant() : string.Concat('.', extension.ToLowerInvariant());
    }

    public static string? MimeForExtension(string? extension) => NormalizeExtension(extension) switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        _ => null,
    };
}
