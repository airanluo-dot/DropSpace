using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Network;

public sealed record PairingConfirmationRequest(
    Guid SessionId,
    int Sas,
    bool Confirmed,
    PairingDecision Decision = PairingDecision.Confirm);

public sealed record PairingConfirmationResponse(
    bool Trusted,
    Guid PeerId,
    PairingState State,
    string? ErrorCategory);

public sealed record ClipboardSyncRequest(Guid PeerId, ClipboardEnvelope Envelope);

public sealed record ClipboardSyncResponse(bool Accepted, string? ErrorCategory);

public sealed record HandoffMessageRequest(Guid PeerId, HandoffMessage Message);

public sealed record HandoffMessageResponse(Guid SessionId, bool Accepted, string? ErrorCategory);

public sealed record TransferOfferRequest(Guid PeerId, TransferManifest Manifest);

public sealed record TransferOfferResponse(Guid SessionId, TransferSessionState State, string? ErrorCategory);

public sealed record TransferAcceptRequest(bool Accepted);

public sealed record TransferStatusResponse(
    Guid SessionId,
    TransferSessionState State,
    long TransferredBytes,
    IReadOnlyDictionary<Guid, IReadOnlyList<int>> ReceivedChunks,
    IReadOnlyList<string> CompletedRelativePaths,
    string? ErrorCategory);

public sealed record TransferCompleteResponse(
    Guid SessionId,
    TransferSessionState State,
    IReadOnlyList<string> CompletedRelativePaths,
    string? ErrorCategory);
