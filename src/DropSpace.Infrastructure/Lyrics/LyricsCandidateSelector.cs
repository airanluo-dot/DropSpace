using System.Diagnostics;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

// Only successful ID decisions are cached. A cold/busy/late/invalid/abstaining
// response never turns a rules fallback into a reusable AI selection.
public sealed class LyricsCandidateSelector(ILyricsSelectionRuntime runtime)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private long _generation, _order;
    private sealed record Entry(string Id, long Expires, long Order);
    public void Clear() { lock (_gate) { _cache.Clear(); _generation++; } }

    public async Task<LyricsSelectionResult> SelectAsync(LyricsQuery query, LyricsSettings settings, string target,
        string modelHash, LyricsCandidateSnapshot snapshot, LyricsDocument rules, CancellationToken token)
    {
        LyricsSelectionResult Fallback(LyricsSelectionOutcome outcome) => new(rules, outcome);
        bool AllowedPriority(LyricsSelectionCandidate candidate) => rules.Lines.Count == 0 ||
            LyricsCandidateRules.ComparePriority(candidate, LyricsCandidateRules.Describe("rules", rules, target),
                settings.Provider, settings.BackupProvider) <= 0;
        token.ThrowIfCancellationRequested();
        if (!settings.Enabled || settings.Mode == LyricsMode.LocalLrc || settings.SelectionMode == LyricsSelectionMode.Rules)
            return Fallback(LyricsSelectionOutcome.Rules);
        if (settings.SelectionMode == LyricsSelectionMode.AiAssisted && !LyricsCandidateRules.RequiresIdentityDecision(query, snapshot))
            return Fallback(LyricsSelectionOutcome.Unambiguous);
        if (!LyricsCandidateSelectionProtocol.TryBuild(query, snapshot, target, settings, out var prompt))
            return Fallback(LyricsSelectionOutcome.Invalid);
        var key = LyricsCandidateSelectionProtocol.DecisionKey(query, settings, target, modelHash, snapshot, prompt);
        long generation;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            generation = _generation;
            if (_cache.TryGetValue(key, out var entry) && entry.Expires > Stopwatch.GetTimestamp() &&
                snapshot.Candidates.FirstOrDefault(candidate => candidate.Id == entry.Id) is { } reused)
            {
                if (AllowedPriority(reused)) return new(reused.Document, LyricsSelectionOutcome.Reused);
                _cache.Remove(key);
                return Fallback(LyricsSelectionOutcome.Invalid);
            }
        }
        var remaining = snapshot.Remaining;
        if (remaining <= TimeSpan.Zero) return Fallback(LyricsSelectionOutcome.NoBudget);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500));
        Task<string?>? inference = null;
        try
        {
            inference = runtime.TryRunSelectionAsync(modelHash, prompt, deadline.Token);
            // Cleanup keeps owning the worker/gate; presentation need not await it.
            var output = await inference.WaitAsync(deadline.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (output is null) return Fallback(LyricsSelectionOutcome.Unavailable);
            if (!LyricsCandidateSelectionProtocol.TryParse(output, snapshot.Candidates, out var id))
                return Fallback(LyricsSelectionOutcome.Invalid);
            if (id is null) return Fallback(LyricsSelectionOutcome.Abstained);
            var chosen = snapshot.Candidates.Single(candidate => candidate.Id == id);
            var match = chosen.Document.Match!;
            if (LyricsMatcher.CandidateScore(query with { CollectSelectionCandidates = true }, match.Title,
                match.Artist, match.Album, match.DurationSeconds, match.ArtistAliases) < 4)
                return Fallback(LyricsSelectionOutcome.Invalid);
            if (!AllowedPriority(chosen))
                return Fallback(LyricsSelectionOutcome.Invalid);
            deadline.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_generation != generation || token.IsCancellationRequested) return Fallback(LyricsSelectionOutcome.Unavailable);
                if (deadline.IsCancellationRequested || Stopwatch.GetTimestamp() >= snapshot.DeadlineTimestamp)
                    return Fallback(LyricsSelectionOutcome.TimedOut);
                while (_cache.Count >= 32) _cache.Remove(_cache.MinBy(pair => pair.Value.Order).Key);
                _cache[key] = new(id, Stopwatch.GetTimestamp() + 600 * Stopwatch.Frequency, ++_order);
            }
            return new(chosen.Document, LyricsSelectionOutcome.Selected);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Fallback(LyricsSelectionOutcome.TimedOut); }
        catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
        { return Fallback(LyricsSelectionOutcome.Unavailable); }
        finally
        {
            if (inference is not null) _ = ObserveAsync(inference);
        }
    }
    private static async Task ObserveAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException) { } }
}
