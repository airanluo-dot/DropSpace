using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Network;

/// <summary>
/// DropLink's wire-level contract. Routes, headers, and byte-level limits are protocol ownership,
/// not user settings. Both the client and the host consume this source.
/// </summary>
public static class DropLinkProtocolRoutes
{
    public const string VersionPrefix = "/v1";
    public const string Device = VersionPrefix + "/device";
    public const string PairingHello = VersionPrefix + "/pairing/hello";
    public const string PairingConfirm = VersionPrefix + "/pairing/confirm";
    public const string Clipboard = VersionPrefix + "/clipboard";
    public const string HandoffText = VersionPrefix + "/handoff/text";
    public const string TransferOffers = VersionPrefix + "/transfers/offers";

    public static string TransferStatus(Guid sessionId) =>
        string.Concat(VersionPrefix, "/transfers/", sessionId.ToString("D"), "/status");

    public static string TransferAccept(Guid sessionId) =>
        string.Concat(VersionPrefix, "/transfers/", sessionId.ToString("D"), "/accept");

    public static string TransferCancel(Guid sessionId) =>
        string.Concat(VersionPrefix, "/transfers/", sessionId.ToString("D"), "/cancel");

    public static string TransferComplete(Guid sessionId) =>
        string.Concat(VersionPrefix, "/transfers/", sessionId.ToString("D"), "/complete");

    public static string TransferChunk(Guid sessionId, Guid itemId, int index) =>
        string.Concat(
            VersionPrefix,
            "/transfers/",
            sessionId.ToString("D"),
            "/items/",
            itemId.ToString("D"),
            "/chunks/",
            index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static bool IsPairing(string path) =>
        string.Equals(path, PairingHello, StringComparison.Ordinal) ||
        string.Equals(path, PairingConfirm, StringComparison.Ordinal);

    public static bool RequiresAuthentication(string path) =>
        string.Equals(path, Clipboard, StringComparison.Ordinal) ||
        string.Equals(path, HandoffText, StringComparison.Ordinal) ||
        path.StartsWith(VersionPrefix + "/transfers/", StringComparison.Ordinal);
}

public static class DropLinkProtocolHeaders
{
    public const string Device = "X-DropLink-Device";
    public const string Nonce = "X-DropLink-Nonce";
    public const string Auth = "X-DropLink-Auth";
    public const string BodySha256 = "X-DropLink-Body-SHA256";
    public const string ChunkSha256 = "X-DropLink-Chunk-SHA256";
    public const string JsonContentType = "application/json";
}

public static class DropLinkProtocolPolicy
{
    public const int AuthenticationNonceBytes = 24;
    public const int PairingNonceBytes = 32;
    public const int BodyHashBytes = 32;
    public const int BodyHashHexLength = BodyHashBytes * 2;
    public const int MaximumPairingBodyBytes = 64 * 1024;
    public const int MaximumAuthenticatedBodyBytes = TransferLimits.DefaultChunkBytes + 64 * 1024;
    public const int MaximumHandoffReplayEntries = 4_096;
    public const int MaximumHandoffReplayEntriesPerPeer = 256;
    public static readonly TimeSpan HandoffReplayRetention = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AuthenticationNonceRetention = DropLinkNonceCache.Retention;

    public static bool IsLowerHexHash(string value)
    {
        if (value.Length != BodyHashHexLength)
        {
            return false;
        }

        return value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    public static bool IsAuthenticationNonce(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(value).Length == AuthenticationNonceBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class DropLinkPairingPolicy
{
    public const int MaximumPending = 16;
    public const int MaximumPendingPerAddress = 4;
    public const int MaximumPendingPerDevice = 1;
    public const int MaximumRateEntries = 1_024;
    public const int MaximumAttemptsPerWindow = 20;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);
}

public static class DropLinkSessionPolicy
{
    public const int MaximumActiveSessions = 64;
    public static readonly TimeSpan ActiveSessionLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RejectedRetention = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    public static bool IsTerminal(TransferSessionState state) =>
        state is TransferSessionState.Completed or
            TransferSessionState.Rejected or
            TransferSessionState.Cancelled or
            TransferSessionState.Failed;

    public static TimeSpan RetentionFor(TransferSessionState state) =>
        state == TransferSessionState.Completed ? CompletedRetention : RejectedRetention;
}

public sealed class PairingAdmissionException : InvalidOperationException
{
    public PairingAdmissionException(string errorCategory)
        : base(errorCategory)
    {
        ErrorCategory = errorCategory;
    }

    public string ErrorCategory { get; }
}
