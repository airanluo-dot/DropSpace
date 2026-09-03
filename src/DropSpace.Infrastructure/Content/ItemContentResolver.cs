using DropSpace.Core.Content;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Content;

public sealed class ItemContentResolver(AppStoragePaths paths) : IItemContentResolver
{
    public ResolvedItemContent Resolve(DropItemSnapshot item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var extension = ItemContentPolicy.NormalizeExtension(
            item.Extension ?? item.Payload?.RelativePath ?? item.OriginalPath);
        var mimeType = item.MimeType ?? ItemContentPolicy.MimeForExtension(extension);
        var type = ItemContentPolicy.Infer(item.Kind, extension, mimeType);

        if (item.Text is not null && item.Kind is (ItemKind.Text or ItemKind.Code or ItemKind.Color))
        {
            return new ResolvedItemContent(
                ItemContentType.Text,
                ItemContentSource.InlineText,
                null,
                extension,
                mimeType ?? "text/plain",
                System.Text.Encoding.UTF8.GetByteCount(item.Text),
                item.Status == ItemStatus.Available,
                item.Status == ItemStatus.Available ? null : "The inline content is unavailable.");
        }

        if (item.Payload is { } payload)
        {
            if (string.Equals(payload.Kind, "images", StringComparison.OrdinalIgnoreCase))
            {
                type = ItemContentType.Image;
                mimeType ??= "image/png";
            }

            try
            {
                var path = PayloadPathPolicy.ResolveContainedPath(paths.Payloads, payload.RelativePath);
                var exists = File.Exists(path);
                return new ResolvedItemContent(
                    type,
                    ItemContentSource.AppPayload,
                    path,
                    extension,
                    mimeType,
                    payload.ByteLength,
                    item.Status == ItemStatus.Available && exists,
                    item.Status != ItemStatus.Available
                        ? "The item is not marked available."
                        : exists ? null : "The app payload is missing.");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException or IOException)
            {
                return new ResolvedItemContent(
                    type,
                    ItemContentSource.AppPayload,
                    null,
                    extension,
                    mimeType,
                    payload.ByteLength,
                    false,
                    exception.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(item.OriginalPath))
        {
            try
            {
                var path = Path.GetFullPath(item.OriginalPath);
                var exists = type == ItemContentType.Folder ? Directory.Exists(path) : File.Exists(path);
                return new ResolvedItemContent(
                    type,
                    ItemContentSource.ExternalPath,
                    path,
                    extension,
                    mimeType,
                    item.KnownSize,
                    item.Status == ItemStatus.Available && exists,
                    item.Status != ItemStatus.Available
                        ? "The item is not marked available."
                        : exists ? null : "The external item is missing.");
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                return new ResolvedItemContent(
                    type,
                    ItemContentSource.ExternalPath,
                    null,
                    extension,
                    mimeType,
                    item.KnownSize,
                    false,
                    "The external item path is invalid.");
            }
        }

        return new ResolvedItemContent(
            type,
            ItemContentSource.None,
            null,
            extension,
            mimeType,
            item.KnownSize,
            false,
            "The item has no readable content source.");
    }
}
