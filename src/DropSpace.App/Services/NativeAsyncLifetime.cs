using Windows.Foundation;

namespace DropSpace.App.Services;

/// <summary>Retains native input ownership through Completed and any in-flight Cancel call.</summary>
internal static class NativeAsyncLifetime
{
    // AsTask(token) can cancel its projected Task before the native operation has
    // completed. Owners of streams/readers must await actual completion instead.
    // A late successful result is returned so the caller can acquire/dispose it,
    // then check its token before using it or starting another native operation.
    internal static Task<T> AwaitAsync<T>(IAsyncOperation<T> operation, CancellationToken token) =>
        AwaitCompletionAsync(operation.AsTask(CancellationToken.None), operation.Cancel, token);

    internal static Task AwaitAsync(IAsyncAction operation, CancellationToken token) =>
        AwaitCompletionAsync(CompleteActionAsync(operation), operation.Cancel, token);

    private static async Task<bool> CompleteActionAsync(IAsyncAction operation)
    {
        await operation.AsTask(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static async Task<T> AwaitCompletionAsync<T>(Task<T> completion, Action cancel, CancellationToken token)
    {
        Task cancellation = Task.CompletedTask;
        var registration = token.Register(() =>
        {
            // Register can invoke inline for an already-canceled token, including
            // on the UI dispatcher. Publisher Cancel must never run on that thread.
            Volatile.Write(ref cancellation, Task.Run(() =>
            {
                try { cancel(); }
                catch (Exception) { /* Failed cancellation is not completion. */ }
            }));
        });
        try { return await completion.ConfigureAwait(false); }
        finally
        {
            registration.Dispose();
            await Volatile.Read(ref cancellation).ConfigureAwait(false);
        }
    }
}
