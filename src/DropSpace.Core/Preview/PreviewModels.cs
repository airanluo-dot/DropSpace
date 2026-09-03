using DropSpace.Core.Models;

namespace DropSpace.Core.Preview;

public enum PreviewKind
{
    Unknown = 0,
    Image = 1,
    Pdf = 2,
    Text = 3,
    Code = 4,
    Json = 5,
    Markdown = 6,
    Audio = 7,
    Video = 8,
    Url = 9,
}

public sealed record PreviewLimits
{
    public const int DefaultInlinePixelSize = 512;
    public const long DefaultTextBytes = 2L * 1024 * 1024;
    public const long DefaultInitialTextBytes = 256L * 1024;
    public const long DefaultImagePixels = 100_000_000;
    public const int DefaultPdfNeighborPages = 1;
    public const int DefaultMaxMediaMetadataBytes = 64 * 1024;

    public int InlinePixelSize { get; init; } = DefaultInlinePixelSize;
    public long MaxTextBytes { get; init; } = DefaultTextBytes;
    public long InitialTextBytes { get; init; } = DefaultInitialTextBytes;
    public long MaxImagePixels { get; init; } = DefaultImagePixels;
    public int PdfNeighborPages { get; init; } = DefaultPdfNeighborPages;
    public int MaxMediaMetadataBytes { get; init; } = DefaultMaxMediaMetadataBytes;

    public PreviewLimits Validate()
    {
        if (InlinePixelSize is < 64 or > 4096 ||
            MaxTextBytes is < 1024 or > 16L * 1024 * 1024 ||
            InitialTextBytes < 1024 || InitialTextBytes > MaxTextBytes ||
            MaxImagePixels is < 1_000_000 or > 200_000_000 ||
            PdfNeighborPages is < 0 or > 4 ||
            MaxMediaMetadataBytes is < 1024 or > 1_024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(PreviewLimits));
        }

        return this;
    }
}

public sealed record PreviewCapability(
    bool CanPreview,
    PreviewKind Kind,
    string ProviderId,
    string? MimeType,
    string? Reason,
    long? KnownBytes,
    int? PixelWidth,
    int? PixelHeight,
    int? PageCount);

public sealed record PreviewDescriptor(
    Guid ItemId,
    PreviewKind Kind,
    string Title,
    string? MimeType,
    string? Text,
    byte[]? Bytes,
    int? PixelWidth,
    int? PixelHeight,
    int? PageCount,
    TimeSpan? Duration,
    IReadOnlyDictionary<string, string> Metadata)
{
    public bool HasBytes => Bytes is { Length: > 0 };
}

public sealed record PreviewRequest(
    DropItemSnapshot Item,
    int Page = 1,
    int TargetPixelWidth = PreviewLimits.DefaultInlinePixelSize,
    bool Inline = true);

public sealed record DropItemSnapshot(
    Guid Id,
    ItemKind Kind,
    ItemStatus Status,
    string Title,
    string? OriginalPath,
    string? Extension,
    long? KnownSize,
    string? MimeType,
    string? Text,
    UrlMetadata? Url,
    int Revision,
    PayloadRecord? Payload = null)
{
    public static DropItemSnapshot FromItem(DropItem item) => new(
        item.Id,
        item.Kind,
        item.Status,
        item.Title,
        item.File?.OriginalPath,
        item.File?.Extension,
        item.File?.KnownSize,
        item.Image?.MimeType,
        item.Text?.InlineText,
        item.Url,
        item.Revision,
        item.Payload);
}

public interface IPreviewProvider
{
    string Id { get; }

    int Priority { get; }

    ValueTask<PreviewCapability> ProbeAsync(
        DropItemSnapshot item,
        CancellationToken cancellationToken = default);

    Task<PreviewDescriptor> LoadAsync(
        PreviewRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPreviewProviderRegistry
{
    IReadOnlyList<IPreviewProvider> Providers { get; }

    Task<PreviewCapability> ProbeAsync(
        DropItemSnapshot item,
        CancellationToken cancellationToken = default);

    Task<PreviewDescriptor> LoadAsync(
        PreviewRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPreviewCache
{
    Task<PreviewDescriptor?> TryGetAsync(
        Guid itemId,
        int revision,
        PreviewKind kind,
        int page,
        int targetPixelWidth,
        CancellationToken cancellationToken = default);

    Task PutAsync(
        PreviewRequest request,
        PreviewDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
