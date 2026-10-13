using DropSpace.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Foundation;

internal static class ImageNativeScenario
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync()
    {
        var lifetime = new ClipboardImageReadLifetime(NullLogger.Instance);
        var native = new DeferredNativeOperation();
        var input = new OwnedInput();
        var entered = Signal();
        using var caller = new CancellationTokenSource();
        var work = lifetime.RunAsync(async token =>
        {
            using (input)
            {
                entered.TrySetResult();
                var value = await NativeAsyncLifetime.AwaitAsync(native, token);
                token.ThrowIfCancellationRequested();
                return value;
            }
        }, Deadline, caller.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await caller.CancelAsync();
            try
            {
                await work.WaitAsync(Deadline);
                throw new InvalidOperationException("Canceled image caller received a result.");
            }
            catch (OperationCanceledException) { }
            await native.CancelEntered.Task.WaitAsync(Deadline);
            Check(!input.Disposed && lifetime.Outstanding == 1,
                "Cancellation released still-running native input or capacity.");
            var drain = lifetime.DrainAsync();
            Check(!drain.IsCompleted, "Native retirement reported complete before native completion.");

            var extraStarts = 0;
            try
            {
                await lifetime.RunAsync(_ =>
                {
                    Interlocked.Increment(ref extraStarts);
                    return Task.FromResult(2);
                }, Deadline, CancellationToken.None).WaitAsync(Deadline);
                throw new InvalidOperationException("Native capacity exhaustion was hidden.");
            }
            catch (IOException) { }
            Check(extraStarts == 0 && lifetime.Outstanding == 1,
                "Excess admission started additional native work.");

            native.Complete(7);
            await native.CompletionObserved.Task.WaitAsync(Deadline);
            Check(!input.Disposed && !drain.IsCompleted,
                "Native completion disposed its input while Cancel was still executing.");
            native.ReleaseCancel.TrySetResult();
            await drain.WaitAsync(Deadline);
            Check(input.Disposed && lifetime.Outstanding == 0,
                "Actual native/cancellation completion did not release input and capacity.");
            Check(work.IsCanceled, "A late result revived the canceled caller.");

            var freshInput = new OwnedInput();
            var fresh = await lifetime.RunAsync(async token =>
            {
                using (freshInput)
                {
                    return await NativeAsyncLifetime.AwaitAsync(
                        new Windows.Foundation.AsyncOperation<int>(Task.FromResult(11)), token);
                }
            }, Deadline, CancellationToken.None).WaitAsync(Deadline);
            Check(fresh == 11 && freshInput.Disposed && lifetime.Outstanding == 0,
                "Fresh image work returned without releasing its input and slot.");
            await lifetime.DrainAsync().WaitAsync(Deadline);
            Console.WriteLine("PASS image-native: canceled caller exits; input and one slot survive native completion and blocked Cancel; excess work rejected; late result stays canceled; fresh work recovers.");
        }
        finally
        {
            native.Complete(7);
            native.ReleaseCancel.TrySetResult();
            await caller.CancelAsync();
            try { await work.WaitAsync(Deadline); }
            catch (OperationCanceledException) { }
            await lifetime.DrainAsync().WaitAsync(Deadline);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class OwnedInput : IDisposable
    {
        private int _disposed;
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class DeferredNativeOperation : IAsyncOperation<int>
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelEntered { get; } = Signal();
        internal TaskCompletionSource ReleaseCancel { get; } = Signal();
        internal TaskCompletionSource CompletionObserved { get; } = Signal();

        public async Task<int> AsTask(CancellationToken token)
        {
            Check(!token.CanBeCanceled, "Native completion was projected with a cancelable token.");
            var result = await _completion.Task;
            CompletionObserved.TrySetResult();
            return result;
        }

        public void Cancel()
        {
            CancelEntered.TrySetResult();
            ReleaseCancel.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
        }

        internal void Complete(int value) => _completion.TrySetResult(value);
    }
}
