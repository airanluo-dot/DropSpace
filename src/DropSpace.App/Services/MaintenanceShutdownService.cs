using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services;

/// <summary>
/// Cross-process, event-driven maintenance shutdown used by Setup and the independent uninstaller.
/// It requests the same graceful disposal path as Exit DropSpace and never force-terminates the app.
/// </summary>
public sealed class MaintenanceShutdownService(
    DispatcherQueue dispatcher,
    ILogger<MaintenanceShutdownService> logger)
{
    private const string RequestEventName = "Local\\DropSpace.MaintenanceShutdown.v1";
    private const string StoppedEventName = "Local\\DropSpace.MaintenanceStopped.v1";
    private RegisteredWaitHandle? _registration;
    private EventWaitHandle? _requestEvent;
    private EventWaitHandle? _stoppedEvent;
    private Mutex? _runningMutex;
    private int _requestHandled;

    public void Start(Func<Task> shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        if (_registration is not null)
        {
            return;
        }

        _requestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RequestEventName);
        _stoppedEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StoppedEventName);
        _runningMutex = new Mutex(initiallyOwned: true, "Local\\DropSpace.Running.v1", out _);
        _stoppedEvent.Reset();
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _requestEvent,
            async (_, timedOut) =>
            {
                if (timedOut || Interlocked.Exchange(ref _requestHandled, 1) != 0)
                {
                    return;
                }

                try
                {
                    logger.LogInformation("A maintenance shutdown was requested by Setup or the uninstaller.");
                    await dispatcher.EnqueueAsync(shutdown);
                    // ShutdownAsync disposes the application services, but the named
                    // process mutex is owned by this cross-process coordinator rather
                    // than by the DI container. Release it before acknowledging the
                    // stopped event so Setup never mistakes the final CLR/WinUI teardown
                    // window for a still-running DropSpace instance.
                    ReleaseRunningMutex();
                    _stoppedEvent.Set();
                    Environment.Exit(0);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Graceful maintenance shutdown failed.");
                    Interlocked.Exchange(ref _requestHandled, 0);
                }
            },
            null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    private void ReleaseRunningMutex()
    {
        var runningMutex = Interlocked.Exchange(ref _runningMutex, null);
        runningMutex?.Dispose();
    }

    public static async Task<int> RequestRunningInstanceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!EventWaitHandle.TryOpenExisting(RequestEventName, out var requestEvent))
        {
            return 0;
        }

        using (requestEvent)
        {
            if (!EventWaitHandle.TryOpenExisting(StoppedEventName, out var stoppedEvent))
            {
                return 2;
            }

            using (stoppedEvent)
            {
                requestEvent.Set();
                var stopped = await Task.Run(
                    () => stoppedEvent.WaitOne(timeout),
                    cancellationToken).ConfigureAwait(false);
                return stopped ? 0 : 3;
            }
        }
    }
}
