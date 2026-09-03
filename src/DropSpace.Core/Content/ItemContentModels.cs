using DropSpace.Core.Models;
using DropSpace.Core.Preview;

namespace DropSpace.Core.Content;

public enum ItemContentType
{
    Unknown = 0,
    File = 1,
    Folder = 2,
    Image = 3,
    Text = 4,
    Url = 5,
}

public enum ItemContentSource
{
    None = 0,
    ExternalPath = 1,
    AppPayload = 2,
    InlineText = 3,
}

public sealed record ResolvedItemContent(
    ItemContentType Type,
    ItemContentSource Source,
    string? ReadablePath,
    string? Extension,
    string? MimeType,
    long? KnownBytes,
    bool IsAvailable,
    string? UnavailableReason)
{
    public bool IsImage => Type == ItemContentType.Image;

    public bool HasReadablePath => IsAvailable && !string.IsNullOrWhiteSpace(ReadablePath);
}

public interface IItemContentResolver
{
    ResolvedItemContent Resolve(DropItemSnapshot item);
}
