namespace DropSpace.Core.Transfer;

/// <summary>A captured clipboard receiver no longer owns an active import lifetime.</summary>
public sealed class ClipboardReceiverUnavailableException() : InvalidOperationException("Clipboard synchronization receiver is unavailable.")
{
    public const string ErrorCategory = "clipboard-receiver-unavailable";
}
