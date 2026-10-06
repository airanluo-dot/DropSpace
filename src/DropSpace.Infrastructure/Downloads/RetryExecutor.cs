// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
using DropSpace.Core.Downloads;
using System.Diagnostics.CodeAnalysis;

namespace DropSpace.Infrastructure.Downloads;

public sealed class RetryExecutor
{
    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var after = response.Headers.RetryAfter;
        throw new HttpRetryException(response.StatusCode,
            after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : null));
    }
    private sealed class HttpRetryException(System.Net.HttpStatusCode status, TimeSpan? retryAfter)
        : HttpRequestException($"Download HTTP status {(int)status}.", null, status)
    { public TimeSpan? RetryAfter { get; } = retryAfter; }
    [SuppressMessage("Performance", "CA1822", Justification = "The executor is intentionally kept as an injectable service for testability and future policy state.")]
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryPolicy policy,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken = default, Action<int, Exception, TimeSpan>? retrying = null)
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
                var delay = AddJitter(policy.GetDelay(attempt));
                if (exception is HttpRetryException { RetryAfter: { } after } && after > delay)
                {
                    // A longer server cooldown is a terminal retry deferral, never an
                    // excuse to request earlier than Retry-After or wait without a bound.
                    if (after > TimeSpan.FromMinutes(2)) throw;
                    delay = after;
                }
                retrying?.Invoke(attempt, exception, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
