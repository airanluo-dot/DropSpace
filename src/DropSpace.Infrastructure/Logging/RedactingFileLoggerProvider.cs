using System.Collections.Concurrent;
using System.Threading.Channels;
using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Logging;

public sealed class RedactingFileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
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

    public RedactingFileLoggerProvider(AppStoragePaths paths)
    {
        paths.EnsureCreated();
        _logPath = Path.Combine(paths.Logs, "dropspace.log");
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RedactingFileLogger(name, _messages.Writer));

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
                RotateIfNeeded();
                await File.AppendAllTextAsync(_logPath, string.Concat(message, Environment.NewLine), _cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException exception)
        {
            System.Diagnostics.Debug.WriteLine(exception.GetType().Name);
        }
        catch (UnauthorizedAccessException exception)
        {
            System.Diagnostics.Debug.WriteLine(exception.GetType().Name);
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
