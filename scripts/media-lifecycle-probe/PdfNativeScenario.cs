using DropSpace.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Foundation;

internal static class PdfNativeScenario
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync()
    {
        var owners = new List<PdfPreviewLifetime>();
        var native = new DeferredNativeOperation();
        var callbackEntered = Signal();
        var releaseCallback = Signal();
        var cleanupEntered = Signal();
        var releaseCleanup = Signal();
        var cleanupCount = 0;
        var rejectedStarts = 0;
        Task? actual = null;
        CancellationTokenRegistration blockedCallback = default;
        using var parent = new CancellationTokenSource();
        using var cancelledParent = new CancellationTokenSource();
        cancelledParent.Cancel();
        try
        {
            try
            {
                if (PdfPreviewLifetime.TryAcquire(cancelledParent.Token, () => Task.CompletedTask,
                    NullLogger.Instance, out var unexpected)) owners.Add(unexpected!);
                throw new InvalidOperationException("An already-cancelled parent was admitted.");
            }
            catch (OperationCanceledException) { }

            var first = Acquire(parent.Token, async () =>
            {
                Interlocked.Increment(ref cleanupCount);
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
            });
            _ = Acquire(CancellationToken.None, () => Task.CompletedTask);
            AssertFull("Two live sessions did not exhaust the global bound.");

            Check(first.TryRun(async token =>
            {
                // Keep a genuine owner-token cancellation callback separate from
                // the native Cancel call and from native Completed.
                blockedCallback = token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    releaseCallback.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
                });
                await NativeAsyncLifetime.AwaitAsync(native, token);
                token.ThrowIfCancellationRequested();
            }, out actual), "First render was not admitted.");
            Check(!first.TryRun(_ =>
            {
                Interlocked.Increment(ref rejectedStarts);
                return Task.CompletedTask;
            }, out _), "A busy PDF owner queued or admitted a second render.");

            await parent.CancelAsync();
            await Task.WhenAll(native.CancelEntered.Task, callbackEntered.Task).WaitAsync(Deadline);
            var retirement = first.RetireAsync();
            Check(ReferenceEquals(retirement, first.RetireAsync()), "Retirement is not idempotent.");
            Check(!actual.IsCompleted && !retirement.IsCompleted && cleanupCount == 0,
                "Cancellation retired an owner before native Completed.");
            AssertFull("A retiring native session freed its global slot.");

            native.Complete();
            await native.CompletionObserved.Task.WaitAsync(Deadline);
            Check(!actual.IsCompleted && cleanupCount == 0,
                "Native Completed bypassed its blocked Cancel ownership.");
            native.ReleaseCancel.TrySetResult();
            await ExpectCancelledAsync(actual);
            Check(cleanupCount == 0 && !retirement.IsCompleted,
                "Actual completion bypassed an active owner cancellation callback.");
            AssertFull("Actual completion freed a slot before cancellation callbacks settled.");

            releaseCallback.TrySetResult();
            await cleanupEntered.Task.WaitAsync(Deadline);
            Check(cleanupCount == 1 && !retirement.IsCompleted,
                "Cleanup was skipped or retirement completed before UI resource cleanup.");
            AssertFull("Pending UI resource cleanup freed a global slot.");
            Check(!first.TryRun(_ =>
            {
                Interlocked.Increment(ref rejectedStarts);
                return Task.CompletedTask;
            }, out _), "A retired session admitted a delayed render retry.");

            releaseCleanup.TrySetResult();
            await retirement.WaitAsync(Deadline);
            Check(rejectedStarts == 0 && cleanupCount == 1,
                "Rejected work was queued or resources were cleaned more than once.");
            var fresh = Acquire(CancellationToken.None, () => Task.CompletedTask);
            var synchronousStarts = 0;
            var callingThread = Environment.CurrentManagedThreadId;
            var invocationThread = 0;
            Check(fresh.TryRun(_ =>
            {
                synchronousStarts++;
                invocationThread = Environment.CurrentManagedThreadId;
                return Task.CompletedTask;
            }, out var synchronous), "Fresh session failed to recover after complete retirement.");
            Check(synchronousStarts == 1 && invocationThread == callingThread,
                "PDF invocation moved off the calling context.");
            await synchronous.WaitAsync(Deadline);
            var expected = new IOException("Controlled synchronous render failure.");
            Check(fresh.TryRun(_ => throw expected, out var failed), "Synchronous failure was not admitted.");
            try
            {
                await failed.WaitAsync(Deadline);
                throw new InvalidOperationException("Render failure was hidden.");
            }
            catch (IOException exception) when (ReferenceEquals(exception, expected)) { }
            Check(fresh.TryRun(_ => Task.CompletedTask, out var retry),
                "A synchronous failure left the owner permanently busy.");
            await retry.WaitAsync(Deadline);
            Console.WriteLine("PASS pdf-native: two global sessions; busy/retired render rejection without queue; parent cancellation and idempotent retirement; native Completed, blocked native Cancel, owner callbacks and resource cleanup retain slots; fresh and synchronous-failure recovery.");
        }
        finally
        {
            native.Complete();
            native.ReleaseCancel.TrySetResult();
            releaseCallback.TrySetResult();
            releaseCleanup.TrySetResult();
            foreach (var owner in owners) await owner.RetireAsync().WaitAsync(Deadline);
            if (actual is not null)
            {
                try { await actual.WaitAsync(Deadline); }
                catch (OperationCanceledException) { }
            }
            await blockedCallback.DisposeAsync();
        }

        PdfPreviewLifetime Acquire(CancellationToken token, Func<Task> cleanup)
        {
            Check(PdfPreviewLifetime.TryAcquire(token, cleanup, NullLogger.Instance, out var owner),
                "Expected a free PDF session slot.");
            owners.Add(owner!);
            return owner!;
        }

        void AssertFull(string message)
        {
            var accepted = PdfPreviewLifetime.TryAcquire(CancellationToken.None,
                () => Task.CompletedTask, NullLogger.Instance, out var excess);
            if (excess is not null) owners.Add(excess);
            Check(!accepted && excess is null, message);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectCancelledAsync(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("A retired render published its late result.");
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
            var value = await _completion.Task;
            CompletionObserved.TrySetResult();
            return value;
        }

        public void Cancel()
        {
            CancelEntered.TrySetResult();
            ReleaseCancel.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
        }

        internal void Complete() => _completion.TrySetResult(1);
    }
}
