using DropSpace.Core.Preview;

namespace DropSpace.Infrastructure.Preview;

public sealed class MediaPreviewProvider(PreviewLimits? limits = null) : FilePreviewProviderBase(limits ?? new PreviewLimits()), IPreviewProvider
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".mpeg", ".mpg",
    };

    public string Id => "media";

    public int Priority => 70;

    public ValueTask<PreviewCapability> ProbeAsync(DropItemSnapshot item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Extension(item);
        var kind = AudioExtensions.Contains(extension) ? PreviewKind.Audio : VideoExtensions.Contains(extension) ? PreviewKind.Video : PreviewKind.Unknown;
        return ValueTask.FromResult(new PreviewCapability(kind != PreviewKind.Unknown, kind, Id, MimeFor(extension), kind == PreviewKind.Unknown ? "Unsupported media type." : null, item.KnownSize, null, null, null));
    }

    public async Task<PreviewDescriptor> LoadAsync(PreviewRequest request, CancellationToken cancellationToken = default)
    {
        var capability = await ProbeAsync(request.Item, cancellationToken).ConfigureAwait(false);
        return new PreviewDescriptor(
            request.Item.Id,
            capability.Kind,
            request.Item.Title,
            capability.MimeType,
            null,
            null,
            null,
            null,
            null,
            null,
            Metadata(("autoPlay", "false"), ("extension", request.Item.Extension ?? string.Empty)));
    }

    private static string MimeFor(string extension) => extension switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        _ => "application/octet-stream",
    };
}
