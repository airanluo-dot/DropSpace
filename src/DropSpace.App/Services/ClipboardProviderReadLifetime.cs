namespace DropSpace.App.Services;

/// <summary>
/// Cancellation releases the managed snapshot waiter, not the native operation's resources.
/// A provider can block Cancel or finish late; Close must follow both native owners.
/// </summary>
internal static class ClipboardProviderReadLifetime
{
    internal static Task<bool> TryReserveSlotAsync(SemaphoreSlim slots, CancellationToken token) => slots.WaitAsync(0, token);

    internal static async Task<T> CompleteAsync<T>(Task<T> nativeRead, Action cancel, Action close,
        CancellationToken cancellationToken, Action<Exception>? report = null)
    {
        var gate = new object();
        var cancellation = Task.CompletedTask;
        var registration = cancellationToken.Register(() =>
        {
            lock (gate) cancellation = Task.Run(() => TryCancel());
        });
        try { return await nativeRead.ConfigureAwait(false); }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            Task pendingCancellation;
            lock (gate) pendingCancellation = cancellation;
            await pendingCancellation.ConfigureAwait(false);
            // Native teardown must not block the UI queue that reads a replacement.
            await Task.Run(async () =>
            {
                while (true)
                {
                    try { close(); return; }
                    catch (Exception exception) when (exception.HResult == unchecked((int)0x80000013)) { return; }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception); }
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        void TryCancel()
        {
            try { cancel(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception); }
        }
        void Report(Exception exception)
        {
            try { report?.Invoke(exception); }
            catch (Exception callbackFailure) when (callbackFailure is not OutOfMemoryException) { }
        }
    }
}
