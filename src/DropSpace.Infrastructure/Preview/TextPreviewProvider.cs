using System.Text;
using System.Text.Json;
using DropSpace.Core.Models;
using DropSpace.Core.Content;
using DropSpace.Core.Preview;

namespace DropSpace.Infrastructure.Preview;

public sealed class TextPreviewProvider(IItemContentResolver contentResolver, PreviewLimits? limits = null) : FilePreviewProviderBase(limits ?? new PreviewLimits(), contentResolver), IPreviewProvider
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".env",
        ".cs", ".cpp", ".c", ".h", ".hpp", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".java", ".kt",
        ".swift", ".rs", ".go", ".py", ".rb", ".php", ".sh", ".ps1", ".sql", ".html", ".css",
        ".json", ".md", ".markdown", ".tex", ".toml",
    };

    public string Id => "text";

    public int Priority => 80;

    public ValueTask<PreviewCapability> ProbeAsync(
        DropItemSnapshot item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = ContentResolver.Resolve(item);
        var extension = content.Extension ?? string.Empty;
        var isTextLike = item.Kind is ItemKind.Text or ItemKind.Code or ItemKind.Color || TextExtensions.Contains(extension);
        var canPreview = content.IsAvailable && isTextLike;
        var kind = extension is ".json" ? PreviewKind.Json : extension is ".md" or ".markdown" ? PreviewKind.Markdown :
            item.Kind == ItemKind.Code ? PreviewKind.Code : PreviewKind.Text;
        return ValueTask.FromResult(new PreviewCapability(
            canPreview,
            kind,
            Id,
            "text/plain",
            canPreview ? null : "The file is not recognized as bounded text.",
            content.KnownBytes,
            null,
            null,
            null));
    }

    public async Task<PreviewDescriptor> LoadAsync(
        PreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Item.Text is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(request.Item.Text);
            if (bytes.LongLength > Limits.MaxTextBytes)
            {
                throw new InvalidDataException("The text preview exceeds the configured limit.");
            }
            return CreateDescriptor(request, bytes);
        }

        await using var source = OpenFile(request.Item);
        var fileBytes = await ReadBoundedAsync(source, Limits.MaxTextBytes, cancellationToken).ConfigureAwait(false);
        return CreateDescriptor(request, fileBytes);
    }

    private PreviewDescriptor CreateDescriptor(PreviewRequest request, byte[] bytes)
    {
        var text = DecodeText(bytes).Replace("\0", string.Empty, StringComparison.Ordinal);
        var extension = Extension(request.Item);
        var kind = extension is ".json" ? PreviewKind.Json : extension is ".md" or ".markdown" ? PreviewKind.Markdown :
            request.Item.Kind == ItemKind.Code ? PreviewKind.Code : PreviewKind.Text;
        if (kind == PreviewKind.Json)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                text = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                kind = PreviewKind.Code;
            }
        }

        return new PreviewDescriptor(
            request.Item.Id,
            kind,
            request.Item.Title,
            "text/plain",
            text,
            null,
            null,
            null,
            null,
            null,
            Metadata(("byteLength", bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }
}
