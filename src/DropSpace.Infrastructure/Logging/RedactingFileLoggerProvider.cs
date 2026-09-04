using System.Collections.Concurrent;
using System.Threading.Channels;
using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Logging;

public sealed class RedactingFileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private const int MaximumWriteAttempts = 4;
    private const int RetryDelayMilliseconds = 50;
    private readonly ConcurrentDictionary<string, RedactingFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Channel<string> _messages = Channel.CreateBounded<string>(new BoundedChannelOptions(1_024)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _logPath;
    private readonly Task _writer;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;
    private int _consecutiveWriteFailures;
    private long _writeFailureCount;

    public RedactingFileLoggerProvider(AppStoragePaths paths)
    {
        paths.EnsureCreated();
        _logPath = Path.Combine(paths.Logs, "dropspace.log");
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RedactingFileLogger(name, _messages.Writer));

    public bool IsDegraded => Volatile.Read(ref _consecutiveWriteFailures) > 0;

    public int ConsecutiveWriteFailures => Volatile.Read(ref _consecutiveWriteFailures);

    public long WriteFailureCount => Interlocked.Read(ref _writeFailureCount);

    public void Dispose()
    {
        ObserveDisposeTask(DisposeAsync().AsTask());
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _messages.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The synchronous compatibility surface never waits. Cancel the writer after the
            // bounded asynchronous drain interval and leave its CTS for GC if it is still running.
            _cancellation.Cancel();
            System.Diagnostics.Debug.WriteLine("DropSpace log writer shutdown timed out.");
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected when the writer is stopped after a bounded drain.
        }
        finally
        {
            _cancellation.Cancel();
            if (_writer.IsCompleted)
            {
                _cancellation.Dispose();
            }
        }
    }

    private static void ObserveDisposeTask(Task task)
    {
        task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    System.Diagnostics.Debug.WriteLine(
                        completed.Exception?.GetBaseException().GetType().Name);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var message in _messages.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                try
                {
                    if (await TryWriteMessageAsync(message).ConfigureAwait(false))
                    {
                        Volatile.Write(ref _consecutiveWriteFailures, 0);
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A single malformed filesystem state must not terminate the logger worker.
                    RecordWriteFailure(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    private async Task<bool> TryWriteMessageAsync(string message)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < MaximumWriteAttempts; attempt++)
        {
            try
            {
                RotateIfNeeded();
                await File.AppendAllTextAsync(
                        _logPath,
                        string.Concat(message, Environment.NewLine),
                        _cancellation.Token)
                    .ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException exception)
            {
                lastException = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastException = exception;
            }

            if (attempt + 1 < MaximumWriteAttempts)
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(RetryDelayMilliseconds * (attempt + 1)),
                        _cancellation.Token)
                    .ConfigureAwait(false);
            }
        }

        RecordWriteFailure(lastException);
        return false;
    }

    private void RecordWriteFailure(Exception? exception)
    {
        Interlocked.Increment(ref _writeFailureCount);
        Interlocked.Increment(ref _consecutiveWriteFailures);
        if (exception is not null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"DropSpace log write deferred after {MaximumWriteAttempts} attempts: {exception.GetType().Name}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("DropSpace log message was dropped after bounded retries.");
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath) || new FileInfo(_logPath).Length < MaximumLogBytes)
        {
            return;
        }

        var previousPath = string.Concat(_logPath, ".1");
        File.Move(_logPath, previousPath, true);
    }

    private sealed class RedactingFileLogger(string category, ChannelWriter<string> writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = LogRedactor.Redact(formatter(state, exception));
            var exceptionSummary = exception is null
                ? string.Empty
                : $" exception={exception.GetType().Name}:{LogRedactor.Redact(exception.Message)}";
            var line = $"{DateTimeOffset.UtcNow:O} level={logLevel} event={eventId.Id} category={category} message={message}{exceptionSummary}";
            writer.TryWrite(line);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
