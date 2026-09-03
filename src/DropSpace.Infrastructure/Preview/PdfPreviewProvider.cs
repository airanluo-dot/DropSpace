using DropSpace.Core.Content;
using System.Text;
using System.Text.RegularExpressions;
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
        var bytes = await ReadBoundedAsync(source, Math.Min(request.Item.KnownSize ?? 16L * 1024 * 1024, 64L * 1024 * 1024), cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            throw new InvalidDataException("The PDF signature is invalid.");
        }

        var header = Encoding.ASCII.GetString(bytes);
        var pageCount = PageCountRegex().Matches(header).Count;
        return new PreviewDescriptor(
            request.Item.Id,
            PreviewKind.Pdf,
            request.Item.Title,
            "application/pdf",
            null,
            bytes,
            null,
            null,
            pageCount > 0 ? pageCount : null,
            null,
            Metadata(("page", request.Page.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [GeneratedRegex("/Type\\s*/Page(?:\\s|/|>)", RegexOptions.CultureInvariant)]
    private static partial Regex PageCountRegex();
}
