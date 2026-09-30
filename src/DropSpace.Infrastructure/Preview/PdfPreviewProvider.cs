using DropSpace.Core.Content;
using DropSpace.Core.Preview;

namespace DropSpace.Infrastructure.Preview;

public sealed partial class PdfPreviewProvider(IItemContentResolver contentResolver, PreviewLimits? limits = null) : FilePreviewProviderBase(limits ?? new PreviewLimits(), contentResolver), IPreviewProvider
{
    public string Id => "pdf";

    public int Priority => 90;

    public ValueTask<PreviewCapability> ProbeAsync(
        DropItemSnapshot item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = ContentResolver.Resolve(item);
        var canPreview = string.Equals(content.Extension, ".pdf", StringComparison.OrdinalIgnoreCase) && content.HasReadablePath;
        return ValueTask.FromResult(new PreviewCapability(canPreview, PreviewKind.Pdf, Id, "application/pdf", canPreview ? null : "Not a readable PDF file.", content.KnownBytes, null, null, null));
    }

    public async Task<PreviewDescriptor> LoadAsync(PreviewRequest request, CancellationToken cancellationToken = default)
    {
        await using var source = OpenFile(request.Item);
        // External sources may have grown since their metadata was captured.
        // Enforce the preview budget against the bytes read, not a stale known size.
        const long maximumBytes = 64L * 1024 * 1024;
        var bytes = await ReadBoundedAsync(source, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            throw new InvalidDataException("The PDF signature is invalid.");
        }

        return new PreviewDescriptor(
            request.Item.Id,
            PreviewKind.Pdf,
            request.Item.Title,
            "application/pdf",
            null,
            bytes,
            null,
            null,
            null,
            null,
            Metadata(("page", request.Page.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

}
