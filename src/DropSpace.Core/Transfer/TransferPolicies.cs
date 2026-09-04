using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Models;

namespace DropSpace.Core.Transfer;

public static class TransferManifestPolicy
{
    public static TransferManifest Create(
        Guid sessionId,
        IReadOnlyList<TransferItemManifest> items,
        TransferLimits? limits = null,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        limits ??= new TransferLimits();
        limits.Validate();
        if (sessionId == Guid.Empty)
        {
            throw new InvalidDataException("A transfer session identifier is required.");
        }
        if (items.Count is < 1 || items.Count > limits.MaxItems)
        {
            throw new InvalidDataException("The transfer item count exceeds the configured limit.");
        }

        var total = 0L;
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            ValidateItem(item, limits);
            if (!ids.Add(item.Id) || !paths.Add(item.RelativePath))
            {
                throw new InvalidDataException("Transfer item identifiers and relative paths must be unique.");
            }

            checked { total += item.Size; }
        }

        if (total > limits.MaxTotalBytes)
        {
            throw new InvalidDataException("The transfer byte count exceeds the configured limit.");
        }

        return new TransferManifest(
            sessionId,
            DropLinkProtocolVersion.V1,
            items.ToArray(),
            total,
            (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
    }

    public static void Validate(TransferManifest manifest, TransferLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.Protocol.IsCompatibleWith(DropLinkProtocolVersion.V1))
        {
            throw new InvalidDataException("The transfer protocol version is not supported.");
        }

        var expected = Create(manifest.SessionId, manifest.Items, limits, manifest.CreatedAtUtc);
        if (expected.TotalBytes != manifest.TotalBytes || expected.CreatedAtUtc != manifest.CreatedAtUtc.ToUniversalTime())
        {
            throw new InvalidDataException("The transfer manifest total or timestamp is invalid.");
        }
    }

    public static string NormalizeRelativePath(string path, int maxLength = TransferLimits.DefaultMaxRelativePathLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized.Length > maxLength || normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException("A transfer path must be relative and bounded.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains('\0') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException("A transfer path contains an invalid segment.");
        }

        return string.Join('/', segments);
    }

    public static string SafeDisplayName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var value = name.Replace('\0', ' ').Trim();
        value = Path.GetFileName(value.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Length > 512)
        {
            throw new InvalidDataException("A transfer display name is invalid.");
        }

        return value;
    }

    private static void ValidateItem(TransferItemManifest item, TransferLimits limits)
    {
        if (item.Id == Guid.Empty || item.Size < 0 || item.RelativePath.Length > limits.MaxRelativePathLength ||
            item.Sha256.Length != 64 || item.Sha256.Any(value => !Uri.IsHexDigit(value)))
        {
            throw new InvalidDataException("A transfer item manifest is invalid.");
        }

        if (string.IsNullOrWhiteSpace(item.MimeType) || item.MimeType.Length > 128)
        {
            throw new InvalidDataException("A transfer item MIME type is invalid.");
        }

        _ = SafeDisplayName(item.DisplayName);
        _ = NormalizeRelativePath(item.RelativePath, limits.MaxRelativePathLength);
        if (item.ChunkCount is < 0)
        {
            throw new InvalidDataException("A transfer chunk count cannot be negative.");
        }

        var expectedChunks = item.Size == 0 ? 0 : checked((int)Math.Ceiling(item.Size / (double)limits.ChunkBytes));
        if (item.ChunkCount != expectedChunks)
        {
            throw new InvalidDataException("The transfer chunk count does not match the item size.");
        }
    }
}

public static class ClipboardEnvelopePolicy
{
    public const long AutomaticTextLimitBytes = 256 * 1024;
    public const long HardTextLimitBytes = 2 * 1024 * 1024;
    public const long AutomaticImageLimitBytes = 10 * 1024 * 1024;
    public const long HardImageLimitBytes = 50 * 1024 * 1024;

    public static ClipboardEnvelope CreateText(
        Guid originDeviceId,
        long originSequence,
        string text,
        ClipboardPayloadKind kind = ClipboardPayloadKind.Text,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (kind is not (ClipboardPayloadKind.Text or ClipboardPayloadKind.Url))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.LongLength > HardTextLimitBytes)
        {
            throw new InvalidDataException("Clipboard text exceeds the hard transfer limit.");
        }

        return new ClipboardEnvelope(
            Guid.NewGuid(),
            originDeviceId,
            originSequence,
            kind,
            (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.Length,
            kind == ClipboardPayloadKind.Url ? "text/uri-list" : "text/plain",
            text,
            null);
    }

    public static ClipboardEnvelope CreateImage(
        Guid originDeviceId,
        long originSequence,
        ReadOnlySpan<byte> bytes,
        string mime,
        DateTimeOffset? nowUtc = null)
    {
        if (bytes.Length == 0 || bytes.Length > HardImageLimitBytes || string.IsNullOrWhiteSpace(mime))
        {
            throw new InvalidDataException("Clipboard image is empty, oversized, or missing a MIME type.");
        }

        return new ClipboardEnvelope(
            Guid.NewGuid(),
            originDeviceId,
            originSequence,
            ClipboardPayloadKind.Image,
            (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.Length,
            mime,
            null,
            bytes.ToArray());
    }

    public static bool IsAllowedAutomatically(ClipboardEnvelope envelope, ClipboardSyncMode mode) =>
        mode switch
        {
            ClipboardSyncMode.AutomaticTextAndUrl =>
                (envelope.Kind is ClipboardPayloadKind.Text or ClipboardPayloadKind.Url) &&
                envelope.ByteLength <= AutomaticTextLimitBytes,
            ClipboardSyncMode.AutomaticTextUrlAndImage =>
                ((envelope.Kind is ClipboardPayloadKind.Text or ClipboardPayloadKind.Url) &&
                 envelope.ByteLength <= AutomaticTextLimitBytes) ||
                (envelope.Kind == ClipboardPayloadKind.Image &&
                 envelope.ByteLength <= AutomaticImageLimitBytes),
            _ => false,
        };

    public static void Validate(ClipboardEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventId == Guid.Empty || envelope.OriginDeviceId == Guid.Empty || envelope.OriginSequence < 0 ||
            envelope.ByteLength < 1 || string.IsNullOrWhiteSpace(envelope.Sha256) || envelope.Sha256.Length != 64 ||
            string.IsNullOrWhiteSpace(envelope.Mime) || envelope.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The clipboard envelope metadata is invalid.");
        }

        var actual = envelope.Kind switch
        {
            ClipboardPayloadKind.Text or ClipboardPayloadKind.Url when envelope.Text is not null =>
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Text))).ToLowerInvariant(),
            ClipboardPayloadKind.Image when envelope.ImageBytes is not null =>
                Convert.ToHexString(SHA256.HashData(envelope.ImageBytes)).ToLowerInvariant(),
            _ => throw new InvalidDataException("The clipboard envelope payload is missing or mismatched."),
        };

        var actualLength = envelope.Kind is ClipboardPayloadKind.Text or ClipboardPayloadKind.Url
            ? Encoding.UTF8.GetByteCount(envelope.Text!)
            : envelope.ImageBytes!.LongLength;
        var hardLimit = envelope.Kind is ClipboardPayloadKind.Text or ClipboardPayloadKind.Url
            ? HardTextLimitBytes
            : HardImageLimitBytes;
        if (actualLength != envelope.ByteLength || actualLength > hardLimit || envelope.Mime.Length > 128)
        {
            throw new InvalidDataException("The clipboard payload length or MIME type is invalid.");
        }

        if (!string.Equals(actual, envelope.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Clipboard payload integrity validation failed.");
        }
    }
}
