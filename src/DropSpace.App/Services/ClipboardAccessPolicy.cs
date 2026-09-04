using System.Runtime.InteropServices;

namespace DropSpace.App.Services;

internal static class ClipboardAccessPolicy
{
    private const int ClipboardBusyHResult = unchecked((int)0x800401D0);
    private static readonly TimeSpan[] WriteRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
    ];

    public static async Task SetContentAsync(
        Action setContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setContent);

        for (var attempt = 0; attempt < WriteRetryDelays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                setContent();
                return;
            }
            catch (COMException exception) when (
                exception.HResult == ClipboardBusyHResult &&
                attempt + 1 < WriteRetryDelays.Length)
            {
                await Task.Delay(WriteRetryDelays[attempt + 1], cancellationToken).ConfigureAwait(true);
            }
            catch (UnauthorizedAccessException) when (attempt + 1 < WriteRetryDelays.Length)
            {
                await Task.Delay(WriteRetryDelays[attempt + 1], cancellationToken).ConfigureAwait(true);
            }
        }
    }
}
