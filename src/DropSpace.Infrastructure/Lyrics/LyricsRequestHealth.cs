using System.Net;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

// Process-local and bounded by five providers × two endpoint stages. A search
// outage must not suppress lyrics whose catalogue identity is already cached.
internal sealed class LyricsRequestHealth(TimeProvider clock)
{
    private sealed class State
    {
        public int Failures;
        public long TransientUntil, RateLimitUntil, Revision;
    }
    private readonly object _gate = new();
    private readonly Dictionary<(LyricsProviderKind, LyricsDiagnosticStage), State> _states = [];

    public long ThrowIfUnavailable(LyricsProviderKind provider, LyricsDiagnosticStage stage)
    {
        lock (_gate)
        {
            var state = Get(provider, stage);
            var now = clock.GetTimestamp();
            if (now < state.RateLimitUntil) throw new LyricsRequestBackoffException(true);
            if (now < state.TransientUntil) throw new LyricsRequestBackoffException(false);
            return state.Revision;
        }
    }

    public void Succeeded(LyricsProviderKind provider, LyricsDiagnosticStage stage, long admittedRevision)
    {
        lock (_gate)
            if (_states.TryGetValue((provider, stage), out var state) && state.Revision == admittedRevision)
            { state.Failures = 0; state.TransientUntil = 0; }
    }

    public void Failed(LyricsProviderKind provider, LyricsDiagnosticStage stage, TimeSpan? retryAfter = null)
    {
        lock (_gate)
        {
            var state = Get(provider, stage);
            state.Revision++;
            state.Failures = Math.Min(state.Failures + 1, 6);
            // One damaged catalogue candidate can still be followed by another.
            if (state.Failures < 2 && retryAfter is null) return;
            var seconds = retryAfter?.TotalSeconds ?? Math.Min(30, 2 * Math.Pow(2, state.Failures - 2));
            state.TransientUntil = Math.Max(state.TransientUntil, Until(seconds, 120));
        }
    }

    public void RateLimited(LyricsProviderKind provider, LyricsDiagnosticStage stage, TimeSpan? retryAfter = null)
    {
        lock (_gate)
        {
            var state = Get(provider, stage);
            state.Revision++;
            // Honor long server requests separately from short outage backoff.
            // Cap pathological values at one day; ordinary 429 defaults to 30 seconds.
            state.RateLimitUntil = Math.Max(state.RateLimitUntil, Until(retryAfter?.TotalSeconds ?? 30, 86_400));
        }
    }

    public void DeferTransient(LyricsProviderKind provider, LyricsDiagnosticStage stage, TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var state = Get(provider, stage);
            state.Revision++;
            state.TransientUntil = Math.Max(state.TransientUntil, Until(retryAfter.TotalSeconds, 120));
        }
    }

    // An explicit source refresh can probe recovery, but cannot defeat a server
    // rate-limit interval. Existing in-flight calls keep their original owner.
    public void RetryTransientFailures(LyricsProviderKind provider)
    {
        lock (_gate)
            foreach (var (key, state) in _states)
                if (key.Item1 == provider) { state.Revision++; state.Failures = 0; state.TransientUntil = 0; }
    }

    private State Get(LyricsProviderKind provider, LyricsDiagnosticStage stage)
    {
        if (!_states.TryGetValue((provider, stage), out var state)) _states[(provider, stage)] = state = new();
        return state;
    }

    private long Until(double seconds, double ceiling) => clock.GetTimestamp() +
        (long)(Math.Clamp(double.IsFinite(seconds) ? seconds : 30, 1, ceiling) * clock.TimestampFrequency);
}

internal sealed class LyricsRequestBackoffException(bool rateLimited) : HttpRequestException(
    rateLimited ? "Lyrics endpoint rate-limit cooldown is active." : "Lyrics endpoint recovery cooldown is active.",
    null, rateLimited ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable);
