using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LyricsService
{
    private static readonly LyricsProviderKind[] OnlineProviders = [LyricsProviderKind.NetEase, LyricsProviderKind.QqMusic, LyricsProviderKind.Kugou, LyricsProviderKind.Lrclib, LyricsProviderKind.Amll];
    private readonly LyricsProviderRegistry _providers;
    private readonly LyricsCache? _cache;
    private readonly MemoryCache _memory = new();
    private readonly Action<LyricsDiagnostic>? _diagnostic;
    private readonly TimeSpan _providerTimeout = TimeSpan.FromSeconds(8);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<LyricsProviderKind, SemaphoreSlim> _providerGates = new();

    // Compatibility callers retain a bounded process-memory cache; production injects
    // the shared persistent store explicitly. No temporary directory is owned here.
    public LyricsService(LyricsProviderRegistry providers, Action<LyricsDiagnostic>? diagnostic = null)
    { _providers = providers; _diagnostic = diagnostic; }

    public LyricsService(LyricsProviderRegistry providers, LyricsCache cache, Action<LyricsDiagnostic>? diagnostic = null)
    {
        _providers = providers;
        _cache = cache;
        _diagnostic = diagnostic;
    }
    internal LyricsService(LyricsProviderRegistry providers, TimeSpan providerTimeout)
    {
        _providers = providers;
        if (providerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(providerTimeout));
        _providerTimeout = providerTimeout;
    }
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken) =>
        (await QueryDetailedAsync(query, settings, cancellationToken).ConfigureAwait(false)).Document;

    public async Task<LyricsQueryResult> QueryDetailedAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken, bool refresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (refresh) query = query with { BypassProviderResponseCache = true };
        if (!settings.Enabled || string.IsNullOrWhiteSpace(query.Title)) return new(LyricsDocument.Empty, LyricsQueryStatus.Disabled);
        var kind = settings.Mode == LyricsMode.LocalLrc ? LyricsProviderKind.LocalLrc : settings.Provider;
        // Keep sub-second duration differences in the cache key. A same-metadata Apple Music
        // replacement (for example an edit/live file with the same displayed title) must not
        // inherit a document fetched for a nearby duration merely because both durations were
        // truncated to the same whole second.
        var backup = OnlineBackup(settings, kind);
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);
        // Non-translation callers retain the existing source cache/provenance
        // contract. Target-aware lookups use a separate identity so an old
        // original-only cache cannot short-circuit the translated-source search.
        var key = target.Length == 0 ? JsonSerializer.Serialize(new
        {
            version = "source-v2", primary = kind, backup, settings.SearchRemainingProviders,
            query.TrackIdentity, query.Title, query.Artist, query.AlbumArtist, query.Album,
            durationTicks = query.Duration.Ticks,
        }) : JsonSerializer.Serialize(new
        {
            version = "source-v3", primary = kind, backup, settings.SearchRemainingProviders, target,
            query.TrackIdentity, query.Title, query.Artist, query.AlbumArtist, query.Album,
            durationTicks = query.Duration.Ticks,
        });
        var generation = _cache?.Generation ?? _memory.Generation;
        if (kind != LyricsProviderKind.LocalLrc && !refresh)
        {
            var cached = _cache is null ? _memory.Read(key) : await _cache.ReadDocumentAsync(key, cancellationToken).ConfigureAwait(false);
            // Older source-v2 entries persisted both heuristic and explicit tags
            // without provenance. They cannot safely be distinguished. Refetch
            // those entries once; legacy untagged entries remain reusable. New
            // explicit TTML tags survive cache round trips, inferred tags are
            // reclassified below on every read after a policy change.
            if (cached?.Lines.Any(line => line.TranslationOrigin == LyricsTranslationOrigin.Provider &&
                !string.IsNullOrWhiteSpace(line.TranslationLanguage) && line.TranslationLanguageIsExplicit is null) == true)
                cached = null;
            // Old NetEase entries can contain incomplete cross-paired translations,
            // not just originals. Their surviving rows cannot establish correctness;
            // refetch each old revision once, leaving other providers unchanged.
            if (cached is { Provider: LyricsProviderKind.NetEase } &&
                cached.ProviderDataRevision < NetEaseLyricsProvider.DataRevision) cached = null;
            var validated = cached is null ? LyricsDocument.Empty : Validate(LyricsLanguagePolicy.IdentifyProviderTranslations(cached), query);
            if (validated.Lines.Count > 0)
                return new(validated, LyricsQueryStatus.Found);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        using var candidates = new CandidateSearch(query, target, deadline.Token);
        var document = LyricsDocument.Empty;
        var translationIncomplete = false;
        try
        {
            var primaryTask = QueryProviderAsync(kind, query, candidates.Token, candidates.Report, candidates.Token);
            // Start allowed translation sources as soon as a valid original is known.
            // A progressive primary may otherwise spend the entire shared budget on
            // its own additional recordings before any other source gets a request.
            await Task.WhenAny(primaryTask, candidates.OriginalAvailable).ConfigureAwait(false);
            var allowed = new HashSet<LyricsProviderKind>();
            if (backup is { } backupKind) allowed.Add(backupKind);
            if (kind != LyricsProviderKind.LocalLrc && settings.SearchRemainingProviders)
                allowed.UnionWith(OnlineProviders.Where(value => value != kind));
            var first = primaryTask.IsCompletedSuccessfully ? primaryTask.Result.Document : candidates.Document;
            Task<(LyricsDocument Document, bool Failed)>? supplemental = null;
            if (NeedsTranslationSearch(first, target) && allowed.Count > 0)
            {
                var excluded = OnlineProviders.Where(value => !allowed.Contains(value)).ToHashSet();
                supplemental = QueryFallbacksAsync(excluded, query, candidates.Token, candidates.Report);
            }
            // Own both tasks until they retire, including the cancellation path.
            if (supplemental is not null)
                await Task.WhenAll(primaryTask, supplemental).ConfigureAwait(false);
            var primary = await primaryTask.ConfigureAwait(false);
            document = PreferTranslation(Validate(primary.Document, query), candidates.Document, target);
            var fallbackFailed = false;
            if (supplemental is not null)
            {
                var fallback = await supplemental.ConfigureAwait(false);
                document = PreferTranslation(document, fallback.Document, target);
                fallbackFailed = fallback.Failed;
            }
            document = PreferTranslation(document, candidates.Document, target);
            cancellationToken.ThrowIfCancellationRequested();
            // Any successful fresh read uses this service's current provider pipeline.
            // Stamp at the cache boundary too, so injected provider implementations
            // cannot turn an original-only response into perpetual cache misses.
            if (document.Provider == LyricsProviderKind.NetEase && document.Lines.Count > 0)
                document = document with { ProviderDataRevision = NetEaseLyricsProvider.DataRevision };
            translationIncomplete = NeedsTranslationSearch(document, target) && (primary.Failed || fallbackFailed);
            if (document.Lines.Count > 0 && kind != LyricsProviderKind.LocalLrc && !translationIncomplete)
            {
                try
                {
                    if (_cache is null) _memory.Write(key, document, generation);
                    else await _cache.WriteDocumentAsync(key, document, generation, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
            }
            var failed = document.Lines.Count == 0 && (primary.Failed || fallbackFailed);
            return new(document, document.Lines.Count > 0 ? LyricsQueryStatus.Found : failed ? LyricsQueryStatus.Failed : LyricsQueryStatus.NotFound, translationIncomplete);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidDataException or XmlException or FormatException or RegexMatchTimeoutException)
        { document = PreferTranslation(document, candidates.Document, target); return new(document, document.Lines.Count > 0 ? LyricsQueryStatus.Found : LyricsQueryStatus.Failed, document.Lines.Count > 0 && NeedsTranslationSearch(document, target)); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { document = PreferTranslation(document, candidates.Document, target); return new(document, document.Lines.Count > 0 ? LyricsQueryStatus.Found : LyricsQueryStatus.Failed, document.Lines.Count > 0 && NeedsTranslationSearch(document, target)); }
    }
    // One 3-second budget starts at the first validated original, including
    // originals discovered inside a provider. Backup/candidate changes never reset it.
    private sealed class CandidateSearch : IDisposable
    {
        private readonly object _gate = new();
        private readonly LyricsQuery _query;
        private readonly string _target;
        private readonly CancellationTokenSource _budget;
        private LyricsDocument _document = LyricsDocument.Empty;
        private bool _started, _closed;
        private readonly TaskCompletionSource _originalAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OriginalAvailable => _originalAvailable.Task;
        public CandidateSearch(LyricsQuery query, string target, CancellationToken token)
        {
            _query = query;
            _target = target;
            _budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = _budget.Token;
        }
        public CancellationToken Token { get; }
        public LyricsDocument Document { get { lock (_gate) return _document; } }
        public void Report(LyricsDocument candidate)
        {
            var valid = Validate(candidate, _query);
            if (valid.Lines.Count == 0) return;
            lock (_gate)
            {
                if (_closed || Token.IsCancellationRequested) return;
                _document = PreferTranslation(_document, valid, _target);
                if (!_started && NeedsTranslationSearch(_document, _target))
                {
                    _started = true;
                    _budget.CancelAfter(TimeSpan.FromSeconds(3));
                    _originalAvailable.TrySetResult();
                }
            }
        }
        public void Dispose()
        {
            lock (_gate) { _closed = true; _budget.Dispose(); }
        }
    }

    private static bool HasTargetTranslation(LyricsDocument document, string target) =>
        target.Length > 0 && LyricsTranslationPolicy.HasMatchingProviderTranslation(
            LyricsLanguagePolicy.IdentifyProviderTranslations(document), target);

    private static bool NeedsTranslationSearch(LyricsDocument document, string target) =>
        document.Lines.Count == 0 || target.Length > 0 && !HasTargetTranslation(document, target) &&
        LyricsTranslationPolicy.NeedsProviderTranslation(document, target);

    private static LyricsDocument PreferTranslation(LyricsDocument current, LyricsDocument candidate, string target) =>
        candidate.Lines.Count > 0 && (current.Lines.Count == 0 ||
            !HasTargetTranslation(current, target) && HasTargetTranslation(candidate, target)) ? candidate : current;

    private sealed record ProviderResult(LyricsDocument Document, bool Failed);
    private async Task<ProviderResult> QueryProviderAsync(LyricsProviderKind kind, LyricsQuery query, CancellationToken token, Action<LyricsDocument> reportCandidate, CancellationToken presentationDeadline)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var cancellation = new ProviderCancellation(token, _providerTimeout);
        var requestToken = cancellation.Token;
        var presentationToken = cancellation.PresentationToken;
        var invocationOwnsCancellation = false;
        Task<ProviderResult>? invocation = null;
        // Bound actual calls, including transports still retiring after the caller left.
        // A deadline releases the presentation waiter, never the provider's resources/slot.
        var gate = _providerGates.GetOrAdd(kind, _ => new SemaphoreSlim(2, 2));
        try
        {
            await gate.WaitAsync(presentationToken).ConfigureAwait(false);
            invocationOwnsCancellation = true;
            invocation = InvokeProviderAsync(kind, query, requestToken, cancellation, gate, reportCandidate);
            var result = await invocation.WaitAsync(presentationToken).ConfigureAwait(false);
            presentationToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        {
            LyricsDiagnostics.Report(_diagnostic, new(kind, LyricsDiagnosticStage.Query,
                token.IsCancellationRequested ? LyricsDiagnosticOutcome.Cancelled : LyricsDiagnosticOutcome.Timeout,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            // Let cooperative HTTP/stream cleanup finish before returning, but keep a
            // bounded waiter if a transport ignores cancellation. The invocation still
            // owns its gate and timeout after this small retirement grace expires.
            if (invocation is not null && !presentationDeadline.IsCancellationRequested)
            {
                try { await invocation.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            token.ThrowIfCancellationRequested();
            return new(LyricsDocument.Empty, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(LyricsDocument.Empty, true); }
        finally { if (!invocationOwnsCancellation) await cancellation.CompleteAsync().ConfigureAwait(false); }
    }
    private async Task<ProviderResult> InvokeProviderAsync(LyricsProviderKind kind, LyricsQuery query,
        CancellationToken token, ProviderCancellation cancellation, SemaphoreSlim gate, Action<LyricsDocument> reportCandidate)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            token.ThrowIfCancellationRequested();
            var provider = _providers.Get(kind);
            var document = provider is IProgressiveLyricsProvider progressive
                ? await progressive.QueryAsync(query, token, reportCandidate).ConfigureAwait(false)
                : await provider.QueryAsync(query, token).ConfigureAwait(false);
            // Cancellation wins even when a transport returns a stale success instead of throwing.
            token.ThrowIfCancellationRequested();
            reportCandidate(document);
            var validated = Validate(document, query);
            LyricsDiagnostics.Report(_diagnostic, new(kind, LyricsDiagnosticStage.Query,
                validated.Lines.Count > 0 ? LyricsDiagnosticOutcome.Found : LyricsDiagnosticOutcome.NoMatch,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                validated.Lines.Count, validated.Lines.Count(line => !string.IsNullOrWhiteSpace(line.Secondary))));
            return new(validated, false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (error is not OperationCanceledException)
                LyricsDiagnostics.Report(_diagnostic, new(kind, LyricsDiagnosticStage.Query,
                LyricsDiagnostics.Classify(error, token.IsCancellationRequested),
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ApiCode: (error as LyricsProviderRejectedException)?.ApiCode,
                HttpStatus: error is HttpRequestException httpError ? (int?)httpError.StatusCode : null));
            return new(LyricsDocument.Empty, true);
        }
        finally { await cancellation.CompleteAsync().ConfigureAwait(false); gate.Release(); }
    }

    private async Task<(LyricsDocument Document, bool Failed)> QueryFallbacksAsync(IReadOnlySet<LyricsProviderKind> excluded, LyricsQuery query, CancellationToken token, Action<LyricsDocument> reportCandidate)
    {
        token.ThrowIfCancellationRequested();
        var stage = CancellationTokenSource.CreateLinkedTokenSource(token);
        // At most four provider requests are active. Allow a short quality window
        // after the first usable result so a slower, stronger verified match can
        // win, then cancel and drain the rest.
        var pending = OnlineProviders.Where(kind => !excluded.Contains(kind)).Select(kind => QueryProviderAsync(kind, query, stage.Token, reportCandidate, token)).ToList();
        var all = pending.ToArray();
        var candidates = new List<(LyricsDocument Document, int Index)>();
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);
        var failed = false;
        Task? qualityWindow = null;
        try
        {
            while (pending.Count > 0)
            {
                Task<ProviderResult> completed;
                if (qualityWindow is null)
                    completed = await Task.WhenAny(pending).ConfigureAwait(false);
                else
                {
                    var waitSet = pending.Cast<Task>().Append(qualityWindow).ToArray();
                    var finished = await Task.WhenAny(waitSet).ConfigureAwait(false);
                    if (ReferenceEquals(finished, qualityWindow)) break;
                    completed = (Task<ProviderResult>)finished;
                }
                var index = all.AsSpan().IndexOf(completed);
                pending.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                var document = result.Document;
                token.ThrowIfCancellationRequested();
                failed |= result.Failed;
                if (document.Lines.Count == 0) continue;
                candidates.Add((document, index));
                if (target.Length == 0 || HasTargetTranslation(document, target))
                    qualityWindow ??= Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            return (candidates.OrderByDescending(candidate => HasTargetTranslation(candidate.Document, target))
                .ThenByDescending(candidate => candidate.Document.Match?.Score ?? 0)
                .ThenBy(candidate => candidate.Index).Select(candidate => candidate.Document).FirstOrDefault() ?? LyricsDocument.Empty, failed);
        }
        finally
        {
            // Request cancellation without synchronously running transport callbacks on
            // the winner's continuation. The stage owns callbacks until they actually exit.
            var cancellation = stage.CancelAsync();
            _ = DisposeStageAfterCancellationAsync(stage, cancellation);
            try { await Task.WhenAll(all).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidDataException or XmlException or FormatException or RegexMatchTimeoutException or OperationCanceledException) { }
        }
    }

    private static async Task DisposeStageAfterCancellationAsync(CancellationTokenSource stage, Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally { stage.Dispose(); }
    }

    // Cancellation callbacks are part of the transport lifetime. Neither an old song
    // nor a provider deadline runs those callbacks synchronously on the new caller.
    private sealed class ProviderCancellation
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenSource _presentation = new();
        private readonly CancellationTokenRegistration _registration;
        private readonly Timer _deadline;
        private Task _callbacks = Task.CompletedTask;
        private bool _requested, _closed;

        public ProviderCancellation(CancellationToken parent, TimeSpan timeout)
        {
            Token = _source.Token;
            PresentationToken = _presentation.Token;
            _registration = parent.UnsafeRegister(static state => ((ProviderCancellation)state!).Request(), this);
            _deadline = new Timer(static state => ((ProviderCancellation)state!).Request(), this, timeout, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token { get; }
        // This token never reaches a provider or HTTP transport. Their later-registered
        // callbacks cannot delay cancellation of admission or the presentation waiter.
        public CancellationToken PresentationToken { get; }
        private void Request()
        {
            lock (_gate)
            {
                if (_closed || _requested) return;
                _requested = true;
                _presentation.Cancel();
                _callbacks = _source.CancelAsync();
            }
        }

        public async Task CompleteAsync()
        {
            Task callbacks;
            lock (_gate)
            {
                _closed = true;
                callbacks = _callbacks;
                _registration.Unregister();
                _deadline.Dispose();
            }
            try { await callbacks.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            finally { _source.Dispose(); _presentation.Dispose(); }
        }
    }

    private static LyricsProviderKind? OnlineBackup(LyricsSettings settings, LyricsProviderKind primary) =>
        primary != LyricsProviderKind.LocalLrc && settings.BackupProvider is { } backup &&
        backup != primary && backup != LyricsProviderKind.LocalLrc && OnlineProviders.Contains(backup)
            ? backup : null;

    private static LyricsDocument Validate(LyricsDocument document, LyricsQuery query)
    {
        if (document.Lines.Count == 0 || document.Match is null) return LyricsDocument.Empty;
        var match = document.Match;
        if (!string.IsNullOrEmpty(query.TrackIdentity) && !string.IsNullOrEmpty(match.TrackIdentity) && match.TrackIdentity != query.TrackIdentity)
            return LyricsDocument.Empty;
        if (string.IsNullOrWhiteSpace(match.CandidateId)) return LyricsDocument.Empty;
        var score = LyricsMatcher.Score(query, match.Title, match.Artist, match.Album, match.DurationSeconds);
        return score < 4 ? LyricsDocument.Empty : document with { Match = match with { Score = score, TrackIdentity = query.TrackIdentity } };
    }
    public void ClearCache()
    {
        ClearResponseCaches();
        if (_cache is null) _memory.Clear();
        else _cache.Clear();
    }
    public void ClearResponseCaches() => _providers.ClearResponseCaches();

    private sealed class MemoryCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (LyricsDocument Document, long Order, int Size)> _entries = new(StringComparer.Ordinal);
        private long _generation, _order;
        private int _characters;
        public long Generation { get { lock (_gate) return _generation; } }
        public LyricsDocument? Read(string key)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry)) return null;
                _entries[key] = (entry.Document, ++_order, entry.Size);
                return entry.Document;
            }
        }
        public void Write(string key, LyricsDocument document, long generation)
        {
            var size = document.Lines.Sum(line => line.Text.Length + (line.Secondary?.Length ?? 0) + line.Words.Sum(word => word.Text.Length));
            if (size > 4 * 1024 * 1024) return;
            lock (_gate)
            {
                if (generation != _generation) return;
                if (_entries.Remove(key, out var replaced)) _characters -= replaced.Size;
                while (_entries.Count >= 32 || _characters + size > 4 * 1024 * 1024)
                {
                    var oldest = _entries.MinBy(entry => entry.Value.Order);
                    _entries.Remove(oldest.Key); _characters -= oldest.Value.Size;
                }
                _entries[key] = (document, ++_order, size); _characters += size;
            }
        }
        public void Clear() { lock (_gate) { ++_generation; _entries.Clear(); _characters = 0; } }
    }
}
