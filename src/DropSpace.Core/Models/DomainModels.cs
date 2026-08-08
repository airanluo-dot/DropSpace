namespace DropSpace.Core.Models;

public enum ItemSource
{
    Space = 1,
    Clipboard = 2,
}

public enum ItemKind
{
    Unknown = 0,
    File = 1,
    Folder = 2,
    Text = 3,
    Image = 4,
    Url = 5,
    Color = 6,
    Code = 7,
}

public enum ItemStatus
{
    Available = 1,
    Missing = 2,
    Unavailable = 3,
    Processing = 4,
    Error = 5,
}

public enum FileEntryKind
{
    File = 1,
    Folder = 2,
    Shortcut = 3,
    Other = 4,
}

public enum DetectedSubtype
{
    Plain = 1,
    Url = 2,
    Color = 3,
    Code = 4,
    Json = 5,
    Path = 6,
    Unknown = 7,
}

public enum DetectionConfidence
{
    Low = 1,
    Medium = 2,
    High = 3,
}

public sealed record DropItem(
    Guid Id,
    ItemSource Source,
    ItemKind Kind,
    string Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    bool IsPinned,
    ItemStatus Status,
    string SearchText,
    int Revision,
    string? Fingerprint,
    string? MetadataJson,
    FileReference? File,
    TextPayload? Text,
    ImagePayload? Image,
    UrlMetadata? Url,
    PayloadRecord? Payload);

public sealed record FileReference(
    string OriginalPath,
    string NormalizedPath,
    FileEntryKind EntryKind,
    string? Extension,
    long? KnownSize,
    DateTimeOffset? KnownModifiedAtUtc,
    DateTimeOffset? LastCheckedAtUtc,
    string? AvailabilityReason);

public sealed record TextPayload(
    string? InlineText,
    int CharacterCount,
    DetectedSubtype DetectedSubtype,
    DetectionConfidence DetectionConfidence,
    string? LanguageHint);

public sealed record ImagePayload(
    int PixelWidth,
    int PixelHeight,
    long EncodedBytes,
    string MimeType,
    bool? HasAlpha,
    int ThumbnailRevision);

public sealed record UrlMetadata(
    string NormalizedUrl,
    string DisplayUrl,
    string Host,
    string Scheme);

public sealed record PayloadRecord(
    Guid Id,
    string Kind,
    string RelativePath,
    long ByteLength,
    string ContentHash,
    DateTimeOffset CreatedAtUtc,
    int StorageVersion);

public sealed record FileCandidate(
    string OriginalPath,
    string NormalizedPath,
    FileEntryKind EntryKind,
    string Title,
    string? Extension,
    long? KnownSize,
    DateTimeOffset? KnownModifiedAtUtc,
    ItemStatus Status,
    string? AvailabilityReason);

public sealed record FileAvailabilityCheck(ItemStatus Status, string? Reason);

public sealed record TextCandidate(
    string Text,
    string Fingerprint,
    ItemKind Kind,
    string Title,
    DetectedSubtype Subtype,
    DetectionConfidence Confidence,
    UrlMetadata? Url);

public sealed record ImageCandidate(
    string Fingerprint,
    int PixelWidth,
    int PixelHeight,
    long EncodedBytes,
    string MimeType,
    bool? HasAlpha,
    PayloadRecord Payload);

public sealed record ItemQuery(
    ItemSource? Source = null,
    bool PinnedOnly = false,
    ItemKind? Kind = null,
    ItemStatus? Status = null,
    string? Search = null,
    int Limit = 250,
    int Offset = 0);

public enum ClearRange
{
    LastHour,
    Today,
    All,
}

public sealed record ClearResult(int RemovedCount, IReadOnlyList<string> PayloadPaths);

public sealed record RetentionResult(int RemovedCount, IReadOnlyList<string> PayloadPaths);
