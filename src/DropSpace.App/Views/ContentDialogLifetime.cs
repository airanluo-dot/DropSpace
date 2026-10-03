using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views;

/// <summary>Cancellation releases an owned dialog and never depends on a live UI dispatcher.</summary>
internal static class ContentDialogLifetime
{
    private sealed class RootState
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal CancellationTokenSource Stop { get; } = new();
    }
    private static readonly ConditionalWeakTable<object, RootState> RootGates = new();

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        cancellationToken.ThrowIfCancellationRequested();
        var root = dialog.XamlRoot ?? throw new InvalidOperationException("A dialog requires a live XamlRoot.");
        var state = RootGates.GetValue(root, _ => new RootState());
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Stop.Token);
        return await RunSerializedAsync(state.Gate,
            () => dialog.ShowAsync().AsTask(), () => Dismiss(dialog), lifetime.Token).ConfigureAwait(false);
    }

    internal static void RetireRoot(Microsoft.UI.Xaml.XamlRoot? root)
    {
        if (root is not null) RootGates.GetValue(root, _ => new RootState()).Stop.Cancel();
    }

    internal static async Task<T> RunSerializedAsync<T>(SemaphoreSlim gate, Func<Task<T>> show,
        Action dismiss, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        Task<T>? nativeCompletion = null;
        try
        {
            return await RunAsync(() => nativeCompletion = show(), dismiss, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Caller cancellation must return promptly, but another modal cannot enter
            // until the old native dialog really closes (Hide may still be queued).
            if (nativeCompletion is { IsCompleted: false }) _ = ReleaseAfterCloseAsync(nativeCompletion, gate);
            else gate.Release();
        }
    }

    private static async Task ReleaseAfterCloseAsync(Task completion, SemaphoreSlim gate)
    {
        try { await completion.ConfigureAwait(false); }
        catch (Exception) { /* The original owner observes presentation errors. */ }
        finally { gate.Release(); }
    }

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
