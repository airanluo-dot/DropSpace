using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>Owns one PDF session through actual rendering and UI resource cleanup.</summary>
internal sealed class PdfPreviewLifetime
{
    private const int MaximumSessions = 2;
    private static readonly object SessionsGate = new();
    // Retired sessions still occupy capacity and retain their resource owner. A
    // failed dispatcher cleanup cannot silently free a slot or lose that owner.
    private static readonly HashSet<PdfPreviewLifetime> Sessions = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly CancellationToken _parentToken;
    private readonly Func<Task> _releaseResourcesAsync;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _parentRegistration;
    private Task _cancelCallbacks = Task.CompletedTask;
    private Task? _operation;
    private bool _registrationReady;
    private bool _retired;
    private bool _cleanupStarted;

    private PdfPreviewLifetime(CancellationToken parentToken, Func<Task> releaseResourcesAsync, ILogger logger)
    {
        _token = _cancellation.Token;
        _parentToken = parentToken;
        _releaseResourcesAsync = releaseResourcesAsync;
        _logger = logger;
    }

    internal static bool TryAcquire(CancellationToken parentToken, Func<Task> releaseResourcesAsync,
        ILogger logger, out PdfPreviewLifetime? lifetime)
    {
        ArgumentNullException.ThrowIfNull(releaseResourcesAsync);
        ArgumentNullException.ThrowIfNull(logger);
        parentToken.ThrowIfCancellationRequested();
        lock (SessionsGate)
        {
            if (Sessions.Count >= MaximumSessions)
            {
                lifetime = null;
                return false;
            }
            lifetime = new PdfPreviewLifetime(parentToken, releaseResourcesAsync, logger);
            Sessions.Add(lifetime);
        }

        lifetime.AttachParentCancellation();
        return true;
    }

    private void AttachParentCancellation()
    {
        // Registration can invoke inline if retirement won after TryAcquire's
        // initial check. Cleanup waits until the registration itself is owned.
        var registration = _parentToken.UnsafeRegister(static state =>
            _ = ((PdfPreviewLifetime)state!).RetireAsync(), this);
        bool startCleanup;
        lock (_gate)
        {
            _parentRegistration = registration;
            _registrationReady = true;
            startCleanup = TryBeginCleanupLocked();
        }
        if (startCleanup) _ = FinishRetirementAsync();
    }

    internal bool TryRun(Func<CancellationToken, Task> operation, out Task completion)
    {
        ArgumentNullException.ThrowIfNull(operation);
        TaskCompletionSource owner;
        lock (_gate)
        {
            if (_retired || _parentToken.IsCancellationRequested || _operation is not null)
            {
                completion = Task.CompletedTask;
                return false;
            }
            owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operation = completion = owner.Task;
        }

        // Invoke on the caller's apartment, with ownership already established
        // even when the delegate completes or throws synchronously. No work queue.
        _ = ExecuteAsync(operation, owner);
        return true;
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation, TaskCompletionSource owner)
    {
        Exception? failure = null;
        try
        {
            _token.ThrowIfCancellationRequested();
            await operation(_token);
            _token.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        bool startCleanup;
        lock (_gate)
        {
            _operation = null;
            startCleanup = TryBeginCleanupLocked();
        }
        if (failure is OperationCanceledException cancellation) owner.TrySetCanceled(cancellation.CancellationToken);
        else if (failure is not null) owner.TrySetException(failure);
        else owner.TrySetResult();
        if (startCleanup) _ = FinishRetirementAsync();
    }

    internal Task RetireAsync()
    {
        bool startCleanup;
        lock (_gate)
        {
            if (_retired) return _retirement.Task;
            _retired = true;
            // CancelAsync marks our token before returning, but external/native
            // callbacks run outside this lock and remain owned until completion.
            _cancelCallbacks = _cancellation.CancelAsync();
            startCleanup = TryBeginCleanupLocked();
        }
        if (startCleanup) _ = FinishRetirementAsync();
        return _retirement.Task;
    }

    private bool TryBeginCleanupLocked()
    {
        if (!_retired || !_registrationReady || _operation is not null || _cleanupStarted) return false;
        _cleanupStarted = true;
        return true;
    }

    private async Task FinishRetirementAsync()
    {
        try
        {
            try { await _cancelCallbacks.ConfigureAwait(false); }
            catch (Exception exception)
            {
                // Failed cancellation is not evidence of unfinished work: the
                // operation and all callbacks have now actually settled.
                _logger.LogWarning("PDF cancellation callback failed ({ExceptionType}).", exception.GetType().Name);
            }
            await _parentRegistration.DisposeAsync().ConfigureAwait(false);
            // The host marshals this callback to its captured UI dispatcher.
            // Capacity is returned only after its input/document cleanup succeeds.
            await _releaseResourcesAsync().ConfigureAwait(false);
            _cancellation.Dispose();
            lock (SessionsGate) Sessions.Remove(this);
            _retirement.TrySetResult();
        }
        catch (Exception exception)
        {
            _logger.LogWarning("PDF resource cleanup retained its session ({ExceptionType}).", exception.GetType().Name);
            _retirement.TrySetException(exception);
        }
    }
}
