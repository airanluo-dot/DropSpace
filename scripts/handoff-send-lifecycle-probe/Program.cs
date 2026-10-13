using System.Collections.Concurrent;
using DropSpace.App.Services;
using DropSpace.Infrastructure.Network;
using Microsoft.Extensions.Logging;

if (args.Length > 0)
{
    if (args.Length != 1 || args[0] != "tracked")
        throw new ArgumentException("Only the optional 'tracked' scenario is supported.");
    await TrackedScenario.RunAsync();
    return;
}

var deadline = TimeSpan.FromSeconds(5);
var logger = new LifecycleLog();
var lifetime = new DeviceHandoffSendLifetime(logger);
var progress = new ProgressSink();
using var first = new ControlledSend(blockCancellation: true);
using var second = new ControlledSend(blockCancellation: false);
using var fresh = new ControlledSend(blockCancellation: false);
var callers = new List<Task<int>>();
var accepted = 0;
try
{
    await ExpectUnavailableAsync(HandoffSendUnavailableReason.Disabled);
    lifetime.Enable();
    var firstCaller = lifetime.RunAsync(first.SendAsync, progress);
    callers.Add(firstCaller); accepted++;
    var secondCaller = lifetime.RunAsync(second.SendAsync, progress);
    callers.Add(secondCaller); accepted++;
    await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(deadline);
    first.Report();
    Check(progress.Count == 1, "An active send's progress was not forwarded.");
    await ExpectUnavailableAsync(HandoffSendUnavailableReason.Busy);

    await Task.Run(lifetime.Disable).WaitAsync(deadline);
    await Task.WhenAll(ExpectCancelledAsync(firstCaller), ExpectCancelledAsync(secondCaller)).WaitAsync(deadline);
    await first.CancelEntered.Task.WaitAsync(deadline);
    Check(first.WorkToken.IsCancellationRequested && second.WorkToken.IsCancellationRequested,
        "Disable did not cancel the actual send tokens.");
    Check(!first.ActualTask.IsCompleted && !second.ActualTask.IsCompleted && logger.RetiredCount == 0,
        "A canceled caller released capacity while its actual send was still pending.");
    lifetime.Enable();
    await ExpectUnavailableAsync(HandoffSendUnavailableReason.Busy);
    first.Report(); second.Report();
    Check(progress.Count == 1, "An old scope published progress after disable/re-enable.");

    first.Complete(7); // Its actual result finishes, while its Cancel callback stays blocked.
    var lateFailure = new IOException("controlled late send failure");
    second.Fail(lateFailure);
    await logger.WaitForRetirementsAsync(1).WaitAsync(deadline);
    Check(logger.LastOutstanding == 1 && logger.Exceptions.Contains(lateFailure),
        "Late failure was not observed or blocked cancellation lost its remaining slot.");
    Check(first.ActualTask.IsCompleted && !first.ReleaseCancel.Task.IsCompleted,
        "Fixture did not separate actual completion from cancellation completion.");

    var freshCaller = lifetime.RunAsync(fresh.SendAsync, progress);
    callers.Add(freshCaller); accepted++;
    await fresh.Started.Task.WaitAsync(deadline);
    await ExpectUnavailableAsync(HandoffSendUnavailableReason.Busy);
    fresh.Report();
    Check(progress.Count == 2, "New scope progress was suppressed along with stale progress.");
    first.ReleaseCancel.TrySetResult();
    await logger.WaitForRetirementsAsync(2).WaitAsync(deadline);
    Check(logger.LastOutstanding == 1, "Old callback completion did not release exactly its own slot.");
    fresh.Complete(11);
    Check(await freshCaller.WaitAsync(deadline) == 11, "Fresh send did not recover after old ownership drained.");
    await logger.WaitForRetirementsAsync(3).WaitAsync(deadline);
    Check(logger.LastOutstanding == 0 && firstCaller.IsCanceled && secondCaller.IsCanceled,
        "A late result revived a retired caller or capacity did not drain.");
    first.Report(); fresh.Report();
    Check(progress.Count == 2, "A settled send still published progress.");

    var activeFailure = new InvalidDataException("controlled active send failure");
    var failureCaller = lifetime.RunAsync<int>((_, _) => Task.FromException<int>(activeFailure));
    callers.Add(failureCaller); accepted++;
    try
    {
        await failureCaller.WaitAsync(deadline);
        throw new InvalidOperationException("Active send failure was hidden.");
    }
    catch (InvalidDataException error) when (ReferenceEquals(error, activeFailure)) { }
    await logger.WaitForRetirementsAsync(4).WaitAsync(deadline);
    Check(logger.LastOutstanding == 0, "Active send failure leaked capacity.");

    var providerCancellation = new TaskCanceledException("controlled provider timeout");
    var timeoutCaller = lifetime.RunAsync<int>((_, _) => Task.FromException<int>(providerCancellation));
    callers.Add(timeoutCaller); accepted++;
    try
    {
        await timeoutCaller.WaitAsync(deadline);
        throw new InvalidOperationException("Active provider cancellation was hidden.");
    }
    catch (IOException error) when (ReferenceEquals(error.InnerException, providerCancellation)) { }
    await logger.WaitForRetirementsAsync(5).WaitAsync(deadline);
    Check(logger.LastOutstanding == 0 && timeoutCaller.IsFaulted,
        "Active provider cancellation became an intentional caller cancellation or leaked capacity.");
    lifetime.Disable();
    await ExpectUnavailableAsync(HandoffSendUnavailableReason.Disabled);
    Console.WriteLine("PASS one handoff-send scenario: two actual owners survive caller cancellation; blocked Cancel keeps capacity after actual completion; disabled/busy reject admission; stale and settled progress stop; late fault is logged; fresh success, original failure and provider-timeout failure recover with zero outstanding sends.");
}
finally
{
    lifetime.Disable();
    first.ReleaseCancel.TrySetResult(); second.ReleaseCancel.TrySetResult(); fresh.ReleaseCancel.TrySetResult();
    first.Complete(7); second.Complete(9); fresh.Complete(11);
    foreach (var caller in callers)
    {
        try { await caller.WaitAsync(deadline); }
        catch (OperationCanceledException) { }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (HandoffSendUnavailableException) { }
    }
    await logger.WaitForRetirementsAsync(accepted).WaitAsync(deadline);
}

async Task ExpectUnavailableAsync(HandoffSendUnavailableReason reason)
{
    var started = false;
    try
    {
        await lifetime.RunAsync((_, _) =>
        {
            started = true;
            return Task.FromResult(99);
        }).WaitAsync(deadline);
        throw new InvalidOperationException("Unavailable send admission was accepted.");
    }
    catch (HandoffSendUnavailableException error)
    {
        Check(error.Reason == reason && !started, "Unavailable send reason changed or its delegate started.");
    }
}

static async Task ExpectCancelledAsync(Task task)
{
    try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Retired caller returned a result.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class ControlledSend(bool blockCancellation) : IDisposable
{
    private readonly TaskCompletionSource<int> _actual = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _registration;
    private IProgress<TransferProgress>? _progress;
    internal TaskCompletionSource Started { get; } = Signal();
    internal TaskCompletionSource CancelEntered { get; } = Signal();
    internal TaskCompletionSource ReleaseCancel { get; } = Signal();
    internal Task<int> ActualTask => _actual.Task;
    internal CancellationToken WorkToken { get; private set; }

    internal Task<int> SendAsync(CancellationToken token, IProgress<TransferProgress>? progress)
    {
        WorkToken = token; _progress = progress;
        _registration = token.Register(() =>
        {
            CancelEntered.TrySetResult();
            if (blockCancellation) ReleaseCancel.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        });
        Started.TrySetResult();
        return _actual.Task;
    }

    internal void Report() => _progress?.Report(new TransferProgress(Guid.NewGuid(), Guid.NewGuid(), 1, 2));
    internal void Complete(int result) => _actual.TrySetResult(result);
    internal void Fail(Exception error) => _actual.TrySetException(error);
    public void Dispose() { ReleaseCancel.TrySetResult(); _registration.Dispose(); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ProgressSink : IProgress<TransferProgress>
{
    private int _count;
    internal int Count => Volatile.Read(ref _count);
    public void Report(TransferProgress value) => Interlocked.Increment(ref _count);
}

internal sealed class LifecycleLog : ILogger
{
    private readonly object _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private int _retired;
    private int _lastOutstanding;
    internal int RetiredCount { get { lock (_gate) return _retired; } }
    internal int LastOutstanding { get { lock (_gate) return _lastOutstanding; } }
    internal ConcurrentQueue<Exception> Exceptions { get; } = new();

    internal Task WaitForRetirementsAsync(int count)
    {
        lock (_gate)
        {
            if (_retired >= count) return Task.CompletedTask;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
            return signal.Task;
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (exception is not null) Exceptions.Enqueue(exception);
        if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
        var fields = values.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (!fields.TryGetValue("{OriginalFormat}", out var format) ||
            !Equals(format, "Device handoff send retired; {Outstanding} sends remain.") ||
            !fields.TryGetValue("Outstanding", out var remaining) || remaining is not int outstanding) return;
        lock (_gate)
        {
            _retired++; _lastOutstanding = outstanding;
            foreach (var waiter in _waiters.Where(waiter => waiter.Count <= _retired)) waiter.Signal.TrySetResult();
            _waiters.RemoveAll(waiter => waiter.Count <= _retired);
        }
    }
}
