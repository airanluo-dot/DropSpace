using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views;

/// <summary>Cancellation releases an owned dialog and never depends on a live UI dispatcher.</summary>
internal static class ContentDialogLifetime
{
    public static Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken cancellationToken) =>
        RunAsync(() => dialog.ShowAsync().AsTask(), () => Dismiss(dialog), cancellationToken);

    internal static async Task<T> RunAsync<T>(Func<Task<T>> show, Action dismiss, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = show();
        try
        {
            // Do not capture the UI context: it can already be retiring. The caller's
            // cancellation must finish even when the queue cannot accept a dismissal.
            var result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dismiss in this path, not a token callback: WaitAsync's own cancellation
            // callback can resume/dispose ownership before another registration runs.
            dismiss();
            throw;
        }
    }

    private static void Dismiss(ContentDialog dialog)
    {
        void Hide()
        {
            try { dialog.Hide(); }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
                Debug.WriteLine($"DropSpace dialog dismissal failed: {exception.GetType().Name}");
            }
        }

        if (dialog.DispatcherQueue.HasThreadAccess) Hide();
        else dialog.DispatcherQueue.TryEnqueue(Hide);
    }
}
