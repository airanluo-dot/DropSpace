using DropSpace.Core.Content;
using DropSpace.Core.Preview;

namespace DropSpace.Infrastructure.Preview;

public sealed class MediaPreviewProvider(IItemContentResolver contentResolver, PreviewLimits? limits = null) : FilePreviewProviderBase(limits ?? new PreviewLimits(), contentResolver), IPreviewProvider
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
        var content = ContentResolver.Resolve(item);
        var extension = content.Extension ?? string.Empty;
        var kind = AudioExtensions.Contains(extension) ? PreviewKind.Audio : VideoExtensions.Contains(extension) ? PreviewKind.Video : PreviewKind.Unknown;
        var canPreview = kind != PreviewKind.Unknown && content.HasReadablePath;
        return ValueTask.FromResult(new PreviewCapability(canPreview, kind, Id, MimeFor(extension), canPreview ? null : "Unsupported or unreadable media type.", content.KnownBytes, null, null, null));
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
            Metadata(("capability", "candidate-requires-platform-decoder"), ("autoPlay", "false"), ("extension", request.Item.Extension ?? string.Empty)));
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
