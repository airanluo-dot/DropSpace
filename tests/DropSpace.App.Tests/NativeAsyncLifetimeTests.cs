using DropSpace.App.Services;
using DropSpace.App.Services.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NativeAsyncLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task BoundedPresentationTimesOutWhileNativeReadRetainsItsSlotAndInput()
    {
        var operations = new BoundedMediaOperation(1, 1);
        var read = new DeferredOperation<uint>();
        var input = new OwnedInput();
        var work = operations.RunAsync(new object(), async token =>
        {
            using (input) { return await NativeAsyncLifetime.AwaitAsync(read, token); }
        }, TimeSpan.FromMilliseconds(40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => work.WaitAsync(Deadline));
        await read.CancelRequested.Task.WaitAsync(Deadline);
        Assert.AreEqual(1, operations.Outstanding);
        Assert.IsFalse(input.Disposed);
        read.Complete(1);
        using var timeout = new CancellationTokenSource(Deadline);
        while (operations.Outstanding != 0) await Task.Delay(1, timeout.Token);
        Assert.IsTrue(input.Disposed);
    }

    [TestMethod]
    public async Task CanceledReadRetainsInputUntilNativeCompletion()
    {
        using var stop = new CancellationTokenSource();
        var read = new DeferredOperation<uint>();
        var input = new OwnedInput();
        async Task ReadAsync()
        {
            using (input)
            {
                await NativeAsyncLifetime.AwaitAsync(read, stop.Token);
                stop.Token.ThrowIfCancellationRequested();
            }
        }
        var work = ReadAsync();
        await stop.CancelAsync();
        await read.CancelRequested.Task.WaitAsync(Deadline);
        Assert.AreEqual(AsyncStatus.Started, read.Status);
        Assert.IsFalse(work.IsCompleted);
        Assert.IsFalse(input.Disposed);
        read.Complete(1);
        await Assert.ThrowsAsync<OperationCanceledException>(() => work.WaitAsync(Deadline));
        Assert.IsTrue(input.Disposed);
    }

    [TestMethod]
    public async Task CanceledAcquisitionDisposesLateResultWithoutStartingRead()
    {
        using var stop = new CancellationTokenSource();
        var open = new DeferredOperation<OwnedInput>();
        var input = new OwnedInput();
        var reads = 0;
        async Task OpenAsync()
        {
            using var acquired = await NativeAsyncLifetime.AwaitAsync(open, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            reads++;
        }
        var work = OpenAsync();
        await stop.CancelAsync();
        await open.CancelRequested.Task.WaitAsync(Deadline);
        Assert.IsFalse(work.IsCompleted);
        open.Complete(input);
        await Assert.ThrowsAsync<OperationCanceledException>(() => work.WaitAsync(Deadline));
        Assert.IsTrue(input.Disposed);
        Assert.AreEqual(0, reads);
    }

    [TestMethod]
    public async Task SlowCancelRetainsInputEvenAfterNativeReadCompletes()
    {
        using var releaseCancel = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new DeferredOperation<uint>
        {
            OnCancel = () => { entered.SetResult(); releaseCancel.Wait(Deadline); },
        };
        var input = new OwnedInput();
        async Task ReadAsync()
        {
            using (input) { await NativeAsyncLifetime.AwaitAsync(read, stop.Token); }
        }
        var work = ReadAsync();
        try
        {
            await stop.CancelAsync();
            await entered.Task.WaitAsync(Deadline);
            read.Complete(1);
            Assert.IsFalse(work.IsCompleted);
            Assert.IsFalse(input.Disposed);
        }
        finally { releaseCancel.Set(); }
        await work.WaitAsync(Deadline);
        Assert.IsTrue(input.Disposed);
    }

    [TestMethod]
    public async Task FailedCancelRetainsInputUntilLateNativeFailure()
    {
        using var stop = new CancellationTokenSource();
        var read = new DeferredOperation<uint> { OnCancel = () => throw new InvalidOperationException("Cancel failed") };
        var input = new OwnedInput();
        async Task ReadAsync()
        {
            using (input) { await NativeAsyncLifetime.AwaitAsync(read, stop.Token); }
        }
        var work = ReadAsync();
        await stop.CancelAsync();
        await read.CancelRequested.Task.WaitAsync(Deadline);
        Assert.IsFalse(input.Disposed);
        read.Fail(new IOException("Native read failed later"));
        await Assert.ThrowsExactlyAsync<IOException>(() => work.WaitAsync(Deadline));
        Assert.IsTrue(input.Disposed);
    }

    private sealed class OwnedInput : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class DeferredOperation<T> : IAsyncOperation<T>
    {
        private readonly object _gate = new();
        private AsyncOperationCompletedHandler<T>? _completed;
        private AsyncStatus _status = AsyncStatus.Started;
        private T _result = default!;
        private Exception? _error;
        public Action? OnCancel { get; init; }
        public TaskCompletionSource CancelRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint Id => 1;
        public Exception ErrorCode { get { lock (_gate) return _error!; } }
        public AsyncStatus Status { get { lock (_gate) return _status; } }
        public AsyncOperationCompletedHandler<T> Completed
        {
            get { lock (_gate) return _completed!; }
            set
            {
                AsyncStatus status;
                lock (_gate) { _completed = value; status = _status; }
                if (status != AsyncStatus.Started) value(this, status);
            }
        }
        public T GetResults() { lock (_gate) { if (_error is not null) throw _error; return _result; } }
        public void Cancel() { CancelRequested.TrySetResult(); OnCancel?.Invoke(); }
        public void Close() { }
        public void Complete(T result) => Finish(result, null);
        public void Fail(Exception error) => Finish(default!, error);
        private void Finish(T result, Exception? error)
        {
            AsyncOperationCompletedHandler<T>? callback;
            AsyncStatus status;
            lock (_gate)
            {
                _result = result; _error = error;
                status = _status = error is null ? AsyncStatus.Completed : AsyncStatus.Error;
                callback = _completed;
            }
            callback?.Invoke(this, status);
        }
    }
}
