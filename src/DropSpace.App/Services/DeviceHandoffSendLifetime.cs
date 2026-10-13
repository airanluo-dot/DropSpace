using DropSpace.Infrastructure.Network;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>Owns bounded outgoing handoffs independently of their callers' waits.</summary>
internal sealed class DeviceHandoffSendLifetime(ILogger logger)
{
    private const int MaximumSends = 2;
    private readonly object _gate = new();
    private readonly HashSet<SendOperation> _operations = [];
    private object? _scope;

    internal void Enable()
    {
        lock (_gate) _scope ??= new object();
    }

    internal void Disable()
    {
        SendOperation[] operations;
        lock (_gate)
        {
            _scope = null;
            operations = _operations.ToArray();
        }

        // Neither cancellation callbacks nor a blocked file-system operation may
        // keep settings/shutdown waiting. Their original owners retain the slots.
        foreach (var operation in operations) operation.RequestCancellation();
    }

    internal async Task<T> RunAsync<T>(
        Func<CancellationToken, IProgress<TransferProgress>?, Task<T>> send,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action? onRetired = null)
    {
        SendOperation operation;
        try
        {
            ArgumentNullException.ThrowIfNull(send);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_scope is not { } scope)
                    throw new HandoffSendUnavailableException(HandoffSendUnavailableReason.Disabled);
                if (_operations.Count >= MaximumSends)
                    throw new HandoffSendUnavailableException(HandoffSendUnavailableReason.Busy);
                operation = new SendOperation(scope, cancellationToken, onRetired);
                _operations.Add(operation);
            }
        }
        catch
        {
            NotifyRetired(onRetired);
            throw;
        }

        Task<T> owned;
        try
        {
            // The client also contains synchronous path/open/identity preparation.
            // Admission precedes scheduling, so no unbounded worker queue is made.
            owned = Task.Run(async () =>
            {
                ThrowIfRetired(operation);
                var scopedProgress = progress is null ? null : new ScopedProgress(this, operation, progress);
                T result;
                try { result = await send(operation.WorkToken, scopedProgress).ConfigureAwait(false); }
                catch (OperationCanceledException exception) when (
                    !operation.WorkToken.IsCancellationRequested && IsCurrent(operation))
                {
                    // HttpClient timeouts and other provider-internal cancellation
                    // are failures while our caller/lifetime is still active. Do
                    // not let the UI mistake them for an intentional cancellation.
                    throw new IOException("The device handoff send did not complete.", exception);
                }
                ThrowIfRetired(operation);
                return result;
            }, CancellationToken.None);
        }
        catch
        {
            _ = RetireAsync(Task.CompletedTask, operation);
            throw;
        }

        try
        {
            // This token has no provider callbacks. A blocked callback on the
            // actual work token cannot delay the caller's cancellation signal.
            var result = await owned.WaitAsync(operation.WaitToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfRetired(operation);
            return result;
        }
        finally
        {
            // Retire observes late failures and waits for actual work AND its
            // cancellation callbacks. Healthy completion releases synchronously;
            // cancelled callers do not wait for a non-cooperative provider.
            _ = RetireAsync(owned, operation);
        }
    }

    private bool IsCurrent(SendOperation operation)
    {
        lock (_gate)
            return ReferenceEquals(_scope, operation.Scope) && _operations.Contains(operation) &&
                !operation.WaitToken.IsCancellationRequested;
    }

    private void ThrowIfRetired(SendOperation operation)
    {
        operation.WorkToken.ThrowIfCancellationRequested();
        if (!IsCurrent(operation)) throw new OperationCanceledException(operation.WaitToken);
    }

    private async Task RetireAsync(Task owned, SendOperation operation)
    {
        try { await owned.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            // The active caller still receives the original failure. If it has
            // already left, this records and observes the late owned failure.
            logger.LogWarning(exception, "Device handoff send failed.");
        }
        finally
        {
            try { await operation.CompleteAsync(logger).ConfigureAwait(false); }
            finally
            {
                int outstanding;
                lock (_gate)
                {
                    _operations.Remove(operation);
                    outstanding = _operations.Count;
                }
                NotifyRetired(operation.OnRetired);
                logger.LogDebug("Device handoff send retired; {Outstanding} sends remain.", outstanding);
            }
        }
    }

    private void NotifyRetired(Action? onRetired)
    {
        try { onRetired?.Invoke(); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Device handoff retirement notification failed.");
        }
    }

    private sealed class ScopedProgress(
        DeviceHandoffSendLifetime owner,
        SendOperation operation,
        IProgress<TransferProgress> target) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            // Never invoke external code under the admission lock. A consumer
            // that queues callbacks must also fence them when consuming: this
            // guard cannot retract work already posted by an arbitrary IProgress.
            if (!owner.IsCurrent(operation))
            {
                // Disable withdraws the scope before cancelling its snapshot of
                // owners. Close that short gap before the client resumes after
                // this report. Already-retired callbacks remain harmless no-ops.
                operation.RequestCancellation();
                return;
            }
            target.Report(value);
            // The consumer can arbitrate Cancel against Finalizing synchronously.
            // Its caller token may already be cancelled while its asynchronous
            // cancellation callback is still queued. Stop before the client can
            // advance from this report into a remote commit request.
            if (operation.CallerToken.IsCancellationRequested || !owner.IsCurrent(operation))
                operation.RequestCancellation();
            operation.WorkToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class SendOperation
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _workCancellation = new();
        private readonly CancellationTokenSource _waitCancellation = new();
        private readonly CancellationTokenRegistration _callerRegistration;
        private Task? _cancellation;
        private bool _completed;

        internal SendOperation(object scope, CancellationToken caller, Action? onRetired)
        {
            Scope = scope;
            OnRetired = onRetired;
            CallerToken = caller;
            WorkToken = _workCancellation.Token;
            WaitToken = _waitCancellation.Token;
            _callerRegistration = caller.UnsafeRegister(
                static state => ((SendOperation)state!).RequestCancellation(), this);
        }

        internal object Scope { get; }
        internal Action? OnRetired { get; }
        internal CancellationToken CallerToken { get; }
        internal CancellationToken WorkToken { get; }
        internal CancellationToken WaitToken { get; }

        internal void RequestCancellation()
        {
            lock (_gate)
            {
                if (_completed || _cancellation is not null) return;
                // CancelAsync marks each source immediately and runs registered
                // callbacks asynchronously; keep both tasks until disposal.
                _cancellation = Task.WhenAll(
                    _waitCancellation.CancelAsync(), _workCancellation.CancelAsync());
            }
        }

        internal async Task CompleteAsync(ILogger logger)
        {
            // A caller callback can take _gate; unregister outside it before
            // closing cancellation admission and capturing the final callback task.
            _callerRegistration.Dispose();
            Task cancellation;
            lock (_gate)
            {
                _completed = true;
                cancellation = _cancellation ?? Task.CompletedTask;
            }

            try { await cancellation.ConfigureAwait(false); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Device handoff cancellation callback failed.");
            }
            finally
            {
                _workCancellation.Dispose();
                _waitCancellation.Dispose();
            }
        }
    }
}
