using DropSpace.Core.Preview;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Preview;

public sealed class UrlPreviewProvider : IPreviewProvider
{
    public string Id => "url";

    public int Priority => 110;

    public ValueTask<PreviewCapability> ProbeAsync(DropItemSnapshot item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canPreview = item.Kind == ItemKind.Url && item.Url is not null;
        return ValueTask.FromResult(new PreviewCapability(canPreview, PreviewKind.Url, Id, "text/uri-list", canPreview ? null : "Not a URL item.", null, null, null, null));
    }

    public Task<PreviewDescriptor> LoadAsync(PreviewRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.Item.Url ?? throw new InvalidDataException("The URL metadata is missing.");
        return Task.FromResult(new PreviewDescriptor(
            request.Item.Id,
            PreviewKind.Url,
            request.Item.Title,
            "text/uri-list",
            url.DisplayUrl,
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = url.Host,
                ["scheme"] = url.Scheme,
                ["remoteFetch"] = "false",
            }));
    }
}
