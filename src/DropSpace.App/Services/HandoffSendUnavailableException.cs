namespace DropSpace.App.Services;

internal enum HandoffSendUnavailableReason
{
    Disabled,
    Busy,
}

internal sealed class HandoffSendUnavailableException(HandoffSendUnavailableReason reason)
    : InvalidOperationException(reason == HandoffSendUnavailableReason.Disabled
        ? "Device handoff is not available for outgoing sends."
        : "Device handoff sends are still running or retiring.")
{
    public HandoffSendUnavailableReason Reason { get; } = reason;
}
