using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>
/// Captures process-level evidence for failures that escape a UI command. It does not suppress
/// AppDomain failures; TaskScheduler notifications are observed after being recorded so a late
/// finalizer escalation cannot terminate an otherwise healthy tray process.
/// </summary>
public sealed class CrashDiagnosticsService : IDisposable
{
    private readonly ILogger<CrashDiagnosticsService> _logger;
    private long _unhandledCount;
    private long _unobservedTaskCount;
    private bool _started;

    public CrashDiagnosticsService(ILogger<CrashDiagnosticsService> logger)
    {
        _logger = logger;
    }

    public long UnhandledCount => Interlocked.Read(ref _unhandledCount);

    public long UnobservedTaskCount => Interlocked.Read(ref _unobservedTaskCount);

    public void Start()
    {
        if (_started)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _started = true;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _started = false;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        Interlocked.Increment(ref _unhandledCount);
        if (args.ExceptionObject is Exception exception)
        {
            _logger.LogCritical(
                exception,
                "AppDomain unhandled exception: type {ExceptionType}, HRESULT {HResult}, thread {ThreadId}, terminating={IsTerminating}.",
                exception.GetType().Name,
                exception.HResult,
                Environment.CurrentManagedThreadId,
                args.IsTerminating);
        }
        else
        {
            _logger.LogCritical(
                "AppDomain unhandled non-Exception object on thread {ThreadId}, terminating={IsTerminating}.",
                Environment.CurrentManagedThreadId,
                args.IsTerminating);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Interlocked.Increment(ref _unobservedTaskCount);
        _logger.LogError(
            args.Exception,
            "Unobserved task exception recorded on thread {ThreadId}; aggregate count {ExceptionCount}.",
            Environment.CurrentManagedThreadId,
            args.Exception.InnerExceptions.Count);
        args.SetObserved();
    }
}
