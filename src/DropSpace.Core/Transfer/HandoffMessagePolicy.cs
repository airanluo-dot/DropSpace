using System.Security.Cryptography;
using System.Text;

namespace DropSpace.Core.Transfer;

public static class HandoffMessagePolicy
{
    public const int MaximumTextBytes = 1 * 1024 * 1024;
    public const int MaximumUrlBytes = 32 * 1024;
    public const int MaximumDisplayLabelCharacters = 160;

    public static HandoffMessage Create(
        Guid senderDeviceId,
        string senderDisplayName,
        HandoffMessageKind kind,
        string payload,
        string? displayLabel = null,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var normalized = kind == HandoffMessageKind.Url ? NormalizeUrl(payload) : NormalizeText(payload);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var message = new HandoffMessage(
            Guid.NewGuid(),
            senderDeviceId,
            SafeDisplayName(senderDisplayName),
            kind,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            normalized,
            NormalizeLabel(displayLabel),
            (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
        Validate(message);
        return message;
    }

    public static void Validate(HandoffMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.SessionId == Guid.Empty || message.SenderDeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(message.SenderDisplayName) || message.SenderDisplayName.Length > 64 ||
            message.SenderDisplayName.Any(char.IsControl) ||
            message.ByteLength < 1 || string.IsNullOrWhiteSpace(message.Sha256) || message.Sha256.Length != 64 || message.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
            message.CreatedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrEmpty(message.Utf8Payload))
        {
            throw new InvalidDataException("The handoff message metadata is invalid.");
        }

        var normalized = message.Kind switch
        {
            HandoffMessageKind.Text => NormalizeText(message.Utf8Payload),
            HandoffMessageKind.Url => NormalizeUrl(message.Utf8Payload),
            _ => throw new InvalidDataException("The handoff message kind is not supported."),
        };
        if (!string.Equals(normalized, message.Utf8Payload, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The handoff message payload is not normalized.");
        }

        var bytes = Encoding.UTF8.GetBytes(message.Utf8Payload);
        var maximum = message.Kind == HandoffMessageKind.Url ? MaximumUrlBytes : MaximumTextBytes;
        byte[] declaredHash;
        try
        {
            declaredHash = Convert.FromHexString(message.Sha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The handoff message hash is invalid.", exception);
        }

        if (bytes.LongLength != message.ByteLength || bytes.LongLength > maximum ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), declaredHash))
        {
            throw new InvalidDataException("The handoff message integrity or size is invalid.");
        }

        if (message.DisplayLabel is { } displayLabel &&
            (string.IsNullOrWhiteSpace(displayLabel) || displayLabel.Contains('\0') || displayLabel.Length > MaximumDisplayLabelCharacters ||
             !string.Equals(displayLabel, displayLabel.Trim(), StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The handoff display label is too long.");
        }
    }

    public static string NormalizeText(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var normalized = payload.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalized)) throw new InvalidDataException("The handoff text cannot be empty.");
        return normalized;
    }

    public static string NormalizeUrl(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (payload.Any(char.IsWhiteSpace) || !Uri.TryCreate(payload, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException("The handoff URL must be an absolute HTTP(S) URL.");
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri;
    }

    private static string SafeDisplayName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\0', ' ').Trim();
        return normalized.Length is < 1 or > 64 ? throw new InvalidDataException("The handoff sender name is invalid.") : normalized;
    }

    private static string? NormalizeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace('\0', ' ').Trim();
        return normalized.Length is < 1 or > MaximumDisplayLabelCharacters
            ? throw new InvalidDataException("The handoff display label is invalid.")
            : normalized;
    }
}
