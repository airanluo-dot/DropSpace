using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;

namespace DropSpace.App.Services;

public sealed class QuickPreviewService(
    IPreviewProviderRegistry providers,
    IPayloadStore payloads)
{
    public string? ResolveSourcePath(DropItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.File?.OriginalPath is { } filePath) return filePath;
        return item.Payload?.RelativePath is { } relativePath
            ? payloads.ResolvePath(relativePath)
            : null;
    }

    public Task<PreviewDescriptor> LoadAsync(
        DropItem item,
        int page = 1,
        int targetPixelWidth = PreviewLimits.DefaultInlinePixelSize,
        bool inline = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var snapshot = DropItemSnapshot.FromItem(item);
        if (item.File is null && item.Payload is { RelativePath: var relativePath })
        {
            var sourcePath = payloads.ResolvePath(relativePath);
            snapshot = snapshot with { OriginalPath = sourcePath, Extension = Path.GetExtension(sourcePath) };
        }

        return providers.LoadAsync(new PreviewRequest(snapshot, page, targetPixelWidth, inline), cancellationToken);
    }
}
