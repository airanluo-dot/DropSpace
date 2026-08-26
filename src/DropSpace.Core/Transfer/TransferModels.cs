using System.Text.Json.Serialization;

namespace DropSpace.Core.Transfer;

public readonly record struct DropLinkProtocolVersion(int Major, int Minor)
{
    public static DropLinkProtocolVersion V1 { get; } = new(1, 0);

    public override string ToString() => $"{Major}.{Minor}";

    public bool IsCompatibleWith(DropLinkProtocolVersion other) => Major == other.Major;
}

public enum DevicePlatform
{
    Unknown = 0,
    Windows = 1,
    MacOS = 2,
    IOS = 3,
    IPadOS = 4,
    Android = 5,
    Linux = 6,
}

[Flags]
public enum PeerCapability
{
    None = 0,
    HandoffFiles = 1 << 0,
    HandoffFolders = 1 << 1,
    HandoffText = 1 << 2,
    HandoffUrl = 1 << 3,
    ClipboardText = 1 << 4,
    ClipboardUrl = 1 << 5,
    ClipboardImage = 1 << 6,
    NearbyBrowserShare = 1 << 7,
}

public enum PeerTrustState
{
    Unknown = 0,
    Pairing = 1,
    Trusted = 2,
    Blocked = 3,
}

public enum PairingState
{
    None = 0,
    Created = 1,
    HelloExchanged = 2,
    AwaitingLocalSasConfirmation = 3,
    LocalConfirmed = 4,
    RemoteConfirmed = 5,
    Trusted = 6,
    Rejected = 7,
    Expired = 8,
    Cancelled = 9,
    Failed = 10,

    // Compatibility names for persisted/client code from Preview.6.
    HelloSent = HelloExchanged,
    AwaitingConfirmation = AwaitingLocalSasConfirmation,
}

public enum PairingDecision
{
    Confirm = 1,
    Reject = 2,
    Cancel = 3,
}

public enum TransferDirection
{
    Send = 1,
    Receive = 2,
}

public enum TransferMode
{
    Handoff = 1,
    Clipboard = 2,
}

public enum TransferSessionState
{
    Offered = 1,
    AwaitingApproval = 2,
    Accepted = 3,
    Transferring = 4,
    Verifying = 5,
    Completed = 6,
    Rejected = 7,
    Cancelled = 8,
    Failed = 9,
}

public enum ClipboardPayloadKind
{
    Text = 1,
    Url = 2,
    Image = 3,
}

public enum TransferItemKind
{
    File = 1,
    Folder = 2,
    Text = 3,
    Url = 4,
    Image = 5,
}

public sealed record PeerDevice(
    Guid Id,
    string DisplayName,
    DevicePlatform Platform,
    string IdentityFingerprint,
    PeerCapability Capabilities,
    PeerTrustState TrustState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSeenAtUtc);

public sealed record DeviceDescriptor(
    DropLinkProtocolVersion Protocol,
    Guid DeviceId,
    string DisplayName,
    DevicePlatform Platform,
    PeerCapability Capabilities,
    string IdentityFingerprint,
    Uri Endpoint);

public sealed record TransferLimits
{
    public const int DefaultChunkBytes = 4 * 1024 * 1024;
    public const int DefaultMaxItems = 100;
    public const long DefaultMaxTotalBytes = 8L * 1024 * 1024 * 1024;
    public const int DefaultMaxRelativePathLength = 1024;

    public int ChunkBytes { get; init; } = DefaultChunkBytes;
    public int MaxItems { get; init; } = DefaultMaxItems;
    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;
    public int MaxRelativePathLength { get; init; } = DefaultMaxRelativePathLength;
    public int MaxConcurrentChunks { get; init; } = 2;

    public TransferLimits Validate()
    {
        if (ChunkBytes is < 64 * 1024 or > 16 * 1024 * 1024 ||
            MaxItems is < 1 or > 10_000 ||
            MaxTotalBytes is < 1 or > 1L * 1024 * 1024 * 1024 * 1024 ||
            MaxRelativePathLength is < 64 or > 4096 ||
            MaxConcurrentChunks is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(TransferLimits));
        }

        return this;
    }
}

public sealed record TransferItemManifest(
    Guid Id,
    TransferItemKind Kind,
    string DisplayName,
    string RelativePath,
    long Size,
    string Sha256,
    string? MimeType,
    int? ChunkCount);

public sealed record TransferManifest(
    Guid SessionId,
    DropLinkProtocolVersion Protocol,
    IReadOnlyList<TransferItemManifest> Items,
    long TotalBytes,
    DateTimeOffset CreatedAtUtc);

public sealed record TransferSession(
    Guid Id,
    TransferDirection Direction,
    TransferMode Mode,
    Guid? PeerId,
    TransferSessionState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int ItemCount,
    long TotalBytes,
    long TransferredBytes,
    string? ErrorCategory);

public sealed record ClipboardEnvelope(
    Guid EventId,
    Guid OriginDeviceId,
    long OriginSequence,
    ClipboardPayloadKind Kind,
    DateTimeOffset CreatedAtUtc,
    string Sha256,
    long ByteLength,
    string Mime,
    string? Text,
    byte[]? ImageBytes)
{
    [JsonIgnore]
    public bool IsTextLike => Kind is ClipboardPayloadKind.Text or ClipboardPayloadKind.Url;
}

public enum HandoffMessageKind
{
    Text = 1,
    Url = 2,
}

public sealed record HandoffMessage(
    Guid SessionId,
    Guid SenderDeviceId,
    string SenderDisplayName,
    HandoffMessageKind Kind,
    long ByteLength,
    string Sha256,
    string Utf8Payload,
    string? DisplayLabel,
    DateTimeOffset CreatedAtUtc);

public sealed record ClipboardSyncPreference(
    Guid PeerId,
    ClipboardSyncMode Mode);

public enum ClipboardSyncMode
{
    Off = 0,
    Manual = 1,
    AutomaticTextAndUrl = 2,
    AutomaticTextUrlAndImage = 3,
}

public sealed record ShareLimits
{
    public const int DefaultNearbyTokenBytes = 24;
    public const int DefaultNearbyTtlMinutes = 10;
    public const int DefaultMaxNearbyReceivers = 2;
    public const long DefaultInternetMaxBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultInternetMaxItems = 100;

    public int NearbyTokenBytes { get; init; } = DefaultNearbyTokenBytes;
    public int NearbyTtlMinutes { get; init; } = DefaultNearbyTtlMinutes;
    public int MaxNearbyReceivers { get; init; } = DefaultMaxNearbyReceivers;
    public long InternetMaxBytes { get; init; } = DefaultInternetMaxBytes;
    public int InternetMaxItems { get; init; } = DefaultInternetMaxItems;

    public ShareLimits Validate()
    {
        if (NearbyTokenBytes is < 24 or > 64 ||
            NearbyTtlMinutes is < 1 or > 60 ||
            MaxNearbyReceivers is < 1 or > 10 ||
            InternetMaxBytes is < 1 or > 10L * 1024 * 1024 * 1024 ||
            InternetMaxItems is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(ShareLimits));
        }

        return this;
    }
}

public sealed record ShareDescriptor(
    Guid ShareId,
    Uri Url,
    DateTimeOffset ExpiresAtUtc,
    int ItemCount,
    long TotalBytes,
    bool IsEncrypted,
    string? KeyFragment);
