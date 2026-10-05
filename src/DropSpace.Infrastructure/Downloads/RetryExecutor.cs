// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
using DropSpace.Core.Downloads;
using System.Diagnostics.CodeAnalysis;

namespace DropSpace.Infrastructure.Downloads;

public sealed class RetryExecutor
{
    [SuppressMessage("Performance", "CA1822", Justification = "The executor is intentionally kept as an injectable service for testability and future policy state.")]
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryPolicy policy,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(isTransient);
        Exception? lastException = null;
        var attempts = Math.Clamp(policy.MaxAttempts, 1, 100);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < attempts && isTransient(exception))
            {
                lastException = exception;
                await Task.Delay(AddJitter(policy.GetDelay(attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastException ?? new InvalidOperationException("Retry operation did not execute.");
    }

    private static TimeSpan AddJitter(TimeSpan delay)
    {
        var jitter = Random.Shared.NextDouble() * 0.2 - 0.1;
        return TimeSpan.FromMilliseconds(Math.Max(0, delay.TotalMilliseconds * (1 + jitter)));
    }
}
