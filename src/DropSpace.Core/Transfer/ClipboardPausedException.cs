namespace DropSpace.Core.Transfer;

public sealed class ClipboardPausedException() : InvalidOperationException("Clipboard synchronization is paused.")
{
    public const string ErrorCategory = "clipboard-paused";
}
