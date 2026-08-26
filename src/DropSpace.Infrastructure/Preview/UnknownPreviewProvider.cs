using DropSpace.Core.Preview;

namespace DropSpace.Infrastructure.Preview;

public sealed class UnknownPreviewProvider : IPreviewProvider
{
    public string Id => "unknown";

    public int Priority => -100;

    public ValueTask<PreviewCapability> ProbeAsync(DropItemSnapshot item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PreviewCapability(true, PreviewKind.Unknown, Id, item.MimeType, null, item.KnownSize, null, null, null));
    }

    public Task<PreviewDescriptor> LoadAsync(PreviewRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateFallback(request.Item));
    }

    public static PreviewDescriptor CreateFallback(DropItemSnapshot item) => new(
        item.Id,
        PreviewKind.Unknown,
        item.Title,
        item.MimeType,
        null,
        null,
        null,
        null,
        null,
        null,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["extension"] = item.Extension ?? string.Empty,
            ["knownSize"] = item.KnownSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            ["modified"] = string.Empty,
            ["actions"] = "open,show-in-folder,quick-actions",
        });
}
