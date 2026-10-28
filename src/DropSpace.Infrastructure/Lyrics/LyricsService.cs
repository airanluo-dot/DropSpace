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
    private readonly ILyricsLanguageIdentifier? _languageIdentifier;
    private readonly TimeSpan _providerTimeout = TimeSpan.FromSeconds(8);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<LyricsProviderKind, SemaphoreSlim> _providerGates = new();

    // Compatibility callers retain a bounded process-memory cache; production injects
    // the shared persistent store explicitly. No temporary directory is owned here.
    public LyricsService(LyricsProviderRegistry providers, Action<LyricsDiagnostic>? diagnostic = null,
        ILyricsLanguageIdentifier? languageIdentifier = null)
    { _providers = providers; _diagnostic = diagnostic; _languageIdentifier = languageIdentifier; }

    public LyricsService(LyricsProviderRegistry providers, LyricsCache cache, Action<LyricsDiagnostic>? diagnostic = null,
        ILyricsLanguageIdentifier? languageIdentifier = null)
    {
        _providers = providers;
        _cache = cache;
        _diagnostic = diagnostic;
        _languageIdentifier = languageIdentifier;
    }
    internal LyricsService(LyricsProviderRegistry providers, TimeSpan providerTimeout)
    {
        _providers = providers;
        if (providerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(providerTimeout));
        _providerTimeout = providerTimeout;
    }
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken) =>
        (await QueryDetailedAsync(query, settings, cancellationToken).ConfigureAwait(false)).Document;

    public async Task<LyricsQueryResult> QueryDetailedAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken, bool refresh = false,
        Action<LyricsDocument>? reportOriginal = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = query with { CollectSelectionCandidates = settings.SelectionMode != LyricsSelectionMode.Rules };
        if (refresh) query = query with { BypassProviderResponseCache = true };
        if (!settings.Enabled || string.IsNullOrWhiteSpace(query.Title)) return new(LyricsDocument.Empty, LyricsQueryStatus.Disabled);
        var kind = settings.Mode == LyricsMode.LocalLrc ? LyricsProviderKind.LocalLrc : settings.Provider;
        // Keep sub-second duration differences in the cache key. A same-metadata Apple Music
        // replacement (for example an edit/live file with the same displayed title) must not
        // inherit a document fetched for a nearby duration merely because both durations were
        // truncated to the same whole second.
        var backup = OnlineBackup(settings, kind);
        if (refresh && kind != LyricsProviderKind.LocalLrc)
        {
            _providers.RetryTransientFailures(kind);
            if (backup is { } backupKind) _providers.RetryTransientFailures(backupKind);
            if (settings.SearchRemainingProviders)
                foreach (var provider in OnlineProviders) _providers.RetryTransientFailures(provider);
        }
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);
        // Non-translation callers retain the existing source cache/provenance
        // contract. Target-aware lookups use a separate identity so an old
        // original-only cache cannot short-circuit the translated-source search.
        string TargetSourceKey(string version) => JsonSerializer.Serialize(new
        {
            version, matcher = LyricsMatcher.Version, primary = kind, backup, settings.SearchRemainingProviders, target,
            query.TrackIdentity, query.Title, query.Artist, query.AlbumArtist, query.Album,
            durationTicks = query.Duration.Ticks,
        });
        string OriginalSourceKey(string version) => JsonSerializer.Serialize(new
        {
            version, matcher = LyricsMatcher.Version, primary = kind, backup, settings.SearchRemainingProviders,
            query.TrackIdentity, query.Title, query.Artist, query.AlbumArtist, query.Album,
            durationTicks = query.Duration.Ticks,
        });
        var key = target.Length == 0 ? OriginalSourceKey("source-v5-quality2") : TargetSourceKey("source-v10-quality2");
        LyricsRequestTrace.Record("cache-lookup", new { key = LyricsRequestTrace.Key(key), version = target.Length == 0 ? "source-v5-quality2" : "source-v10-quality2", target, refresh });
        var generation = _cache?.Generation ?? _memory.Generation;
        LyricsDocument? cachedPreview = null;
        if (kind != LyricsProviderKind.LocalLrc && !refresh)
        {
            var cached = _cache is null ? _memory.Read(key) : await _cache.ReadDocumentAsync(key, cancellationToken).ConfigureAwait(false);
            // The old winner may be a provider's first recording, including on the
            // preferred source. Re-evaluate once under bounded candidate ranking;
            // retain old files for ordinary quota eviction rather than clearing the store.
            if (cached is not null && !HasUsableEvidence(LyricsBodyQualityPolicy.Normalize(cached)))
            { LyricsRequestTrace.Record("cache-rejected", new { reason = "placeholder-or-empty-body", qualityVersion = LyricsBodyQualityPolicy.Version }); cached = null; }
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
            // LRC-only Kugou cache entries predate native translation/word support.
            if (cached is { Provider: LyricsProviderKind.Kugou } &&
                cached.ProviderDataRevision < KugouLyricsProvider.DataRevision) cached = null;
            if (cached is { Provider: LyricsProviderKind.QqMusic } &&
                cached.ProviderDataRevision < QqMusicLyricsProvider.DataRevision) cached = null;
            if (cached is { Provider: LyricsProviderKind.Lrclib } &&
                cached.ProviderDataRevision < LrclibLyricsProvider.DataRevision) cached = null;
            if (cached is { Provider: LyricsProviderKind.Amll } &&
                cached.ProviderDataRevision < AmllLyricsProvider.DataRevision) cached = null;
            var validated = cached is null ? LyricsDocument.Empty : Validate(LyricsLanguagePolicy.IdentifyProviderTranslations(cached), query);
            if (_languageIdentifier is not null && target.Length > 0 && validated.Lines.Count > 0)
                validated = await _languageIdentifier.PrepareAsync(validated, target, cancellationToken).ConfigureAwait(false);
            LyricsRequestTrace.Record("cache-result", new { usable = HasUsableEvidence(validated) && !NeedsTranslationSearch(validated, target), document = LyricsRequestTrace.Describe(validated) });
            if (HasUsableEvidence(validated) && !NeedsTranslationSearch(validated, target))
            {
                if (!query.CollectSelectionCandidates) return new(validated, LyricsQueryStatus.Found)
                { SelectionCandidates = new([LyricsCandidateRules.Describe("c0", validated, target)], 0) };
                // A single source cache is a trusted preview, not a complete AI
                // snapshot. Continue bounded collection through the same providers.
                cachedPreview = validated;
            }
            else if (HasUsableEvidence(validated) && target.Length > 0 &&
                LyricsTranslationPolicy.HasMatchingProviderTranslation(validated, target))
            {
                // Retain an old partial native result as a preview, never as the
                // finished source decision. Current coverage drives fresh lookup.
                cachedPreview = validated;
            }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        using var candidates = new CandidateSearch(query, target, deadline.Token, kind, backup, reportOriginal,
            settings.SelectionMode != LyricsSelectionMode.Rules);
        if (cachedPreview is not null) candidates.Report(cachedPreview);
        var document = LyricsDocument.Empty;
        async Task<LyricsQueryResult> FinishAsync(LyricsDocument selected, bool sourceFailed, bool selectionComplete, bool allowCache = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selected = Validate(selected, query);
            // Stamp fresh validated reads at the shared cache boundary too.
            if (selected.Lines.Count > 0) selected = selected with { ProviderDataRevision = selected.Provider switch
            {
                LyricsProviderKind.NetEase => NetEaseLyricsProvider.DataRevision,
                LyricsProviderKind.Kugou => KugouLyricsProvider.DataRevision,
                LyricsProviderKind.QqMusic => QqMusicLyricsProvider.DataRevision,
                LyricsProviderKind.Lrclib => LrclibLyricsProvider.DataRevision,
                LyricsProviderKind.Amll => AmllLyricsProvider.DataRevision,
                _ => selected.ProviderDataRevision,
            } };
            if (_languageIdentifier is not null && target.Length > 0 && selected.Lines.Count > 0)
                selected = await _languageIdentifier.PrepareAsync(selected, target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var incomplete = NeedsTranslationSearch(selected, target) && sourceFailed;
            if (allowCache && HasUsableEvidence(selected) && kind != LyricsProviderKind.LocalLrc && !incomplete &&
                !NeedsTranslationSearch(selected, target) && (target.Length == 0 || selectionComplete))
            {
                try
                {
                    if (_cache is null) _memory.Write(key, selected, generation);
                    else await _cache.WriteDocumentAsync(key, selected, generation, cancellationToken).ConfigureAwait(false);
                    LyricsRequestTrace.Record("cache-write", new { key = LyricsRequestTrace.Key(key), document = LyricsRequestTrace.Describe(selected) });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
            }
            return CompleteQueryResult(selected, candidates.CompletedProviderCount, incomplete) with { SelectionCandidates = candidates.Snapshot };
        }
        try
        {
            var primaryTask = QueryProviderAsync(kind, query, candidates.Token, candidates.Report, candidates.Token, candidates.ReportProviderCompleted);
            // Start allowed translation sources as soon as a valid original is known.
            // A progressive primary may otherwise spend the entire shared budget on
            // its own additional recordings before any other source gets a request.
            await Task.WhenAny(primaryTask, candidates.OriginalAvailable).ConfigureAwait(false);
            var allowed = new HashSet<LyricsProviderKind>();
            if (backup is { } backupKind) allowed.Add(backupKind);
            if (kind != LyricsProviderKind.LocalLrc && settings.SearchRemainingProviders)
                allowed.UnionWith(OnlineProviders.Where(value => value != kind));
            var first = primaryTask.IsCompletedSuccessfully ? primaryTask.Result.Document : candidates.Document;
            using var supplementalStop = CancellationTokenSource.CreateLinkedTokenSource(candidates.Token);
            Task<(LyricsDocument Document, bool Failed)>? supplemental = null;
            if ((query.CollectSelectionCandidates || NeedsTranslationSearch(first, target)) && allowed.Count > 0)
            {
                LyricsRequestTrace.Record("fallback", new { providers = allowed.Select(value => value.ToString()).ToArray(), target });
                var excluded = OnlineProviders.Where(value => !allowed.Contains(value)).ToHashSet();
                supplemental = QuerySupplementalSafelyAsync(excluded, query, supplementalStop.Token, candidates.Report, backup, kind, candidates.ReportProviderCompleted);
            }
            // Own both tasks until they retire, including the cancellation path.
            if (supplemental is not null)
            {
                var all = Task.WhenAll(primaryTask, supplemental);
                if (!query.CollectSelectionCandidates && await Task.WhenAny(all, candidates.PreferredTranslationAvailable).ConfigureAwait(false) != all)
                    await supplementalStop.CancelAsync().ConfigureAwait(false);
                await all.ConfigureAwait(false);
            }
            var primary = await primaryTask.ConfigureAwait(false);
            document = SelectTranslation(Validate(primary.Document, query), candidates.Document, query, target, kind, backup);
            var fallbackFailed = false;
            if (supplemental is not null)
            {
                var fallback = await supplemental.ConfigureAwait(false);
                document = SelectTranslation(document, fallback.Document, query, target, kind, backup);
                fallbackFailed = fallback.Failed;
            }
            document = SelectTranslation(document, candidates.Document, query, target, kind, backup);
            // A lower-priority success must not permanently hide a preferred provider
            // that failed transiently. Target-aware v6 retires legacy such decisions.
            var selectionComplete = document.Provider == kind || !primary.Failed &&
                (document.Provider == backup || backup is null || !fallbackFailed);
            return await FinishAsync(document, primary.Failed || fallbackFailed, selectionComplete).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidDataException or XmlException or FormatException or RegexMatchTimeoutException)
        { document = SelectTranslation(document, candidates.Document, query, target, kind, backup); return CompleteQueryResult(document, candidates.CompletedProviderCount, document.Lines.Count > 0 && NeedsTranslationSearch(document, target)) with { SelectionCandidates = candidates.Snapshot }; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            document = SelectTranslation(document, candidates.Document, query, target, kind, backup);
            // Ending the internal selection window is a normal bounded decision.
            // Only the preferred source can be committed without its task finishing;
            // a backup must not hide an unresolved preferred request in future plays.
            if (candidates.SelectionWindowExpired)
                return await FinishAsync(document, sourceFailed: true, selectionComplete: document.Provider == kind,
                    allowCache: document.Provider == kind).ConfigureAwait(false);
            return CompleteQueryResult(document, candidates.CompletedProviderCount, document.Lines.Count > 0 && NeedsTranslationSearch(document, target))
                with { SelectionCandidates = candidates.Snapshot };
        }
    }
    // Weak-only collection has a provisional bound. The first strictly validated
    // original gets the full shared three-second source window; later candidates
    // cannot extend it. Selection starts separately after this snapshot freezes.
    private sealed class CandidateSearch : IDisposable
    {
        private readonly object _gate = new();
        private readonly LyricsQuery _query;
        private readonly string _target;
        private readonly LyricsProviderKind _primary;
        private readonly LyricsProviderKind? _backup;
        private readonly CancellationTokenSource _budget;
        private readonly CancellationToken _parentToken;
        private LyricsDocument _document = LyricsDocument.Empty;
        private bool _started, _provisionalStarted, _closed, _truncated, _originalReported;
        private readonly Action<LyricsDocument>? _reportOriginal;
        private readonly bool _captureCandidates;
        private readonly Dictionary<string, LyricsDocument> _collected = new(StringComparer.Ordinal);
        private int _collectedBytes;
        private int _prunedCandidates;
        private const int CandidatesPerSource = 3;
        private int _completedProviderCount;
        public int CompletedProviderCount => Volatile.Read(ref _completedProviderCount);
        public void ReportProviderCompleted() => Interlocked.Increment(ref _completedProviderCount);
        private readonly TaskCompletionSource _originalAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OriginalAvailable => _originalAvailable.Task;
        private readonly TaskCompletionSource _preferredTranslationAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PreferredTranslationAvailable => _preferredTranslationAvailable.Task;
        public CandidateSearch(LyricsQuery query, string target, CancellationToken token, LyricsProviderKind primary, LyricsProviderKind? backup, Action<LyricsDocument>? reportOriginal, bool captureCandidates)
        {
            _reportOriginal = reportOriginal;
            _captureCandidates = captureCandidates;
            _query = query;
            _primary = primary;
            _backup = backup;
            _target = target;
            _budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            _parentToken = token;
            Token = _budget.Token;
        }
        public CancellationToken Token { get; }
        public bool SelectionWindowExpired { get { lock (_gate) return _started && Token.IsCancellationRequested && !_parentToken.IsCancellationRequested; } }
        public LyricsDocument Document { get { lock (_gate) return _document; } }
        public LyricsCandidateSnapshot Snapshot
        {
            get
            {
                lock (_gate) return new(_collected
                    // The selector may review weaker cross-script metadata, but a
                    // confirmed empty body cannot displace strictly verified lyrics.
                    .Where(pair => pair.Value.Lines.Count > 0 || !_collected.Values.Any(document => Validate(document, _query).Lines.Count > 0))
                    .OrderBy(pair => pair.Value.Provider).ThenBy(pair => pair.Value.Match!.CandidateId, StringComparer.Ordinal)
                    .Select((pair, index) => LyricsCandidateRules.Describe("c" + index, pair.Value, _target)).ToArray(),
                    _captureCandidates && _collected.Count > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() +
                        LyricsCandidateSelectionProtocol.MaximumDecisionSeconds * System.Diagnostics.Stopwatch.Frequency : 0, _truncated)
                    { PrunedCandidates = _prunedCandidates };
            }
        }
        private static int EstimateBytes(LyricsDocument doc) => checked(
            (doc.Match is { } match ? 256 + (match.Title.Length + match.Artist.Length + match.Album.Length +
                (match.CandidateId?.Length ?? 0) + (match.ArtistAliases?.Sum(value => value.Length) ?? 0)) * 2 : 0) +
            doc.Lines.Sum(line => 192 + line.Text.Length * 2 + (line.Secondary?.Length ?? 0) * 2 +
                line.Words.Sum(word => 64 + word.Text.Length * 2)));
        public void Report(LyricsDocument candidate)
        {
            candidate = LyricsBodyQualityPolicy.Normalize(candidate);
            var valid = Validate(candidate, _query);
            var eligible = _query.CollectSelectionCandidates && candidate.Lines.Count > 0 && candidate.Match is { } match &&
                !string.IsNullOrWhiteSpace(match.CandidateId) &&
                (string.IsNullOrEmpty(match.TrackIdentity) || match.TrackIdentity == _query.TrackIdentity) &&
                LyricsMatcher.CandidateScore(_query, string.IsNullOrWhiteSpace(match.CanonicalTitle) ? match.Title : match.CanonicalTitle,
                    match.Artist, match.Album, match.DurationSeconds, match.ArtistAliases) >= 4
                ? candidate : valid;
            if (!HasUsableEvidence(eligible)) return;
            var releaseSearch = false;
            lock (_gate)
            {
                if (_closed || Token.IsCancellationRequested)
                {
                    // A transport may still report while retiring. It cannot reopen the
                    // bounded source stage, but a native veto for the frozen recording
                    // must retire AI already running for that playback. Validate above
                    // has already checked this query's recording evidence.
                    if (valid.TranslationAdmission?.Decision == LyricsWholeTrackDecision.ProviderTarget &&
                        LyricsLanguagePolicy.IsAdmissionCurrent(valid, _target) &&
                        valid.Match is { } lateMatch && lateMatch.TrackIdentity == _query.TrackIdentity)
                    {
                        try { _reportOriginal?.Invoke(valid); }
                        catch (Exception error) when (error is not OutOfMemoryException) { }
                    }
                    return;
                }
                if (!_started && HasUsableEvidence(valid))
                {
                    _started = true;
                    _budget.CancelAfter(TimeSpan.FromSeconds(3));
                }
                else if (!_started && !_provisionalStarted && _query.CollectSelectionCandidates)
                {
                    _provisionalStarted = true;
                    _budget.CancelAfter(TimeSpan.FromSeconds(3));
                }
                if (_captureCandidates)
                    CaptureCandidate(eligible);
                if (!_originalReported) { _originalReported = true; releaseSearch = true; }
                var previous = _document;
                _document = SelectTranslation(_document, valid, _query, _target, _primary, _backup);
                // A validated translation must not wait behind another provider's timeout.
                // Publish only improvements under the same ranking used for the final result.
                // Serialize callbacks so a racing older original cannot overwrite the upgrade.
                if (!ReferenceEquals(previous, _document))
                {
                    try { _reportOriginal?.Invoke(_document); }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                }
                if (!_query.CollectSelectionCandidates && valid.Provider == _primary && HasCompleteTargetCoverage(valid, _target))
                    _preferredTranslationAvailable.TrySetResult();
            }
            if (releaseSearch) _originalAvailable.TrySetResult();
        }

        private int CompareForSnapshot(LyricsDocument left, LyricsDocument right)
        {
            var identity = HasUsableEvidence(Validate(right, _query)).CompareTo(HasUsableEvidence(Validate(left, _query)));
            return identity != 0 ? identity : LyricsCandidateRules.CompareCandidates(
                LyricsCandidateRules.Describe("left", left, _target), LyricsCandidateRules.Describe("right", right, _target),
                _query, _primary, _backup);
        }

        // Providers may evaluate six recordings, but the bounded metadata prompt
        // keeps the best three per source. Normal shortlisting is not corruption.
        private void CaptureCandidate(LyricsDocument eligible)
        {
            var key = eligible.Provider + ":" + eligible.Match!.CandidateId;
            var replaceKey = key;
            _collected.TryGetValue(key, out var old);
            if (old is not null && CompareForSnapshot(eligible, old) >= 0) return;
            if (old is null)
            {
                var sameSource = _collected.Where(pair => pair.Value.Provider == eligible.Provider)
                    .OrderBy(pair => pair.Value, Comparer<LyricsDocument>.Create(CompareForSnapshot)).ToArray();
                if (sameSource.Length >= CandidatesPerSource)
                {
                    _prunedCandidates++;
                    var worst = sameSource[^1];
                    if (CompareForSnapshot(eligible, worst.Value) >= 0) return;
                    replaceKey = worst.Key;
                    old = worst.Value;
                }
            }
            var bytes = EstimateBytes(eligible);
            var oldBytes = old is null ? 0 : EstimateBytes(old);
            if ((old is not null || _collected.Count < LyricsCandidateSelectionProtocol.MaximumCandidates) &&
                bytes <= 16 * 1024 * 1024 && _collectedBytes - oldBytes + bytes <= 16 * 1024 * 1024)
            {
                if (replaceKey != key) _collected.Remove(replaceKey);
                _collected[key] = eligible;
                _collectedBytes += bytes - oldBytes;
            }
            else _truncated = true;
        }
        public void Dispose()
        {
            lock (_gate) { _closed = true; _budget.Dispose(); }
        }
    }

    private static bool HasCompleteTargetCoverage(LyricsDocument document, string target) =>
        target.Length > 0 && LyricsTranslationPolicy.HasCompleteTargetCoverage(document, target);

    private static bool NeedsTranslationSearch(LyricsDocument document, string target) =>
        !IsConfirmedInstrumental(document) && (document.Lines.Count == 0 ||
        LyricsTranslationPolicy.NeedsProviderTranslationLookup(document, target));

    private static bool IsConfirmedInstrumental(LyricsDocument document) =>
        document.Lines.Count == 0 && document.BodyQuality == LyricsBodyQuality.ConfirmedInstrumental && document.Match is not null;

    private static bool HasUsableEvidence(LyricsDocument document) => document.Lines.Count > 0 || IsConfirmedInstrumental(document);

    private static LyricsQueryResult CompleteQueryResult(LyricsDocument document, int completedProviderCount, bool translationIncomplete)
    {
        // A completed no-match search remains useful even when another provider
        // fails. Failure flags still control translation completeness and caching.
        var status = HasUsableEvidence(document) ? LyricsQueryStatus.Found :
            completedProviderCount > 0 ? LyricsQueryStatus.NotFound : LyricsQueryStatus.Failed;
        if (status == LyricsQueryStatus.Failed)
            document = document with { BodyQuality = LyricsBodyQuality.RequestFailed };
        else if (status == LyricsQueryStatus.NotFound && document.BodyQuality == LyricsBodyQuality.RequestFailed)
            document = document with { BodyQuality = LyricsBodyQuality.NoLyrics };
        LyricsRequestTrace.Record("source-search-result", new { completedProviders = completedProviderCount,
            status = status.ToString(), translationIncomplete });
        return new(document, status, translationIncomplete);
    }

    private static LyricsDocument SelectTranslation(LyricsDocument current, LyricsDocument candidate, LyricsQuery query, string target,
        LyricsProviderKind primary, LyricsProviderKind? backup)
    {
        if (!HasUsableEvidence(candidate)) return current;
        if (!HasUsableEvidence(current)) return candidate;
        if ((candidate.Lines.Count > 0) != (current.Lines.Count > 0)) return candidate.Lines.Count > 0 ? candidate : current;
        var left = LyricsCandidateRules.Describe("candidate", candidate, target);
        var right = LyricsCandidateRules.Describe("current", current, target);
        return LyricsCandidateRules.CompareCandidates(left, right, query, primary, backup) < 0 ? candidate : current;
    }

    private sealed record ProviderResult(LyricsDocument Document, bool Failed);
    private async Task<ProviderResult> QueryProviderAsync(LyricsProviderKind kind, LyricsQuery query, CancellationToken token, Action<LyricsDocument> reportCandidate, CancellationToken presentationDeadline, Action reportProviderCompleted)
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
            // Progressive/late callbacks are not completed searches. Count only the
            // nonfailed result that survived both presentation and source deadlines.
            if (!result.Failed) reportProviderCompleted();
            return result;
        }
        catch (OperationCanceledException)
        {
            // Admission failure means no provider/HTTP invocation started. Keep it
            // distinguishable from a transport timeout or upstream rejection.
            LyricsDiagnostics.Report(_diagnostic, new(kind,
                invocation is null ? LyricsDiagnosticStage.Admission : LyricsDiagnosticStage.Query,
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
            return new(LyricsDocument.Empty with { BodyQuality = LyricsBodyQuality.RequestFailed }, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(LyricsDocument.Empty with { BodyQuality = LyricsBodyQuality.RequestFailed }, true); }
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
            if (_languageIdentifier is not null && !string.IsNullOrWhiteSpace(query.PreferredTranslationLanguage) && document.Lines.Count > 0)
                document = await _languageIdentifier.PrepareAsync(document, query.PreferredTranslationLanguage, token).ConfigureAwait(false);
            reportCandidate(document);
            LyricsRequestTrace.Record("provider-body", LyricsRequestTrace.Describe(LyricsBodyQualityPolicy.Normalize(document)));
            var validated = Validate(document, query);
            LyricsDiagnostics.Report(_diagnostic, new(kind, LyricsDiagnosticStage.Query,
                validated.BodyQuality == LyricsBodyQuality.RequestFailed ? LyricsDiagnosticOutcome.TransportFailure :
                    HasUsableEvidence(validated) ? LyricsDiagnosticOutcome.Found : LyricsDiagnosticOutcome.NoMatch,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                validated.Lines.Count, validated.Lines.Count(line => !string.IsNullOrWhiteSpace(line.Secondary))));
            return new(validated, validated.BodyQuality == LyricsBodyQuality.RequestFailed);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (error is not OperationCanceledException)
                LyricsDiagnostics.Report(_diagnostic, new(kind, LyricsDiagnosticStage.Query,
                LyricsDiagnostics.Classify(error, token.IsCancellationRequested),
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ApiCode: (error as LyricsProviderRejectedException)?.ApiCode,
                HttpStatus: error is HttpRequestException httpError ? (int?)httpError.StatusCode : null));
            LyricsRequestTrace.Record("provider-body", new { provider = kind.ToString(), bodyQuality = nameof(LyricsBodyQuality.RequestFailed) });
            return new(LyricsDocument.Empty with { BodyQuality = LyricsBodyQuality.RequestFailed }, true);
        }
        finally { await cancellation.CompleteAsync().ConfigureAwait(false); gate.Release(); }
    }

    private async Task<(LyricsDocument Document, bool Failed)> QuerySupplementalSafelyAsync(
        IReadOnlySet<LyricsProviderKind> excluded, LyricsQuery query, CancellationToken token,
        Action<LyricsDocument> reportCandidate, LyricsProviderKind? backup, LyricsProviderKind primary, Action reportProviderCompleted)
    {
        try { return await QuerySupplementalAsync(excluded, query, token, reportCandidate, backup, primary, reportProviderCompleted).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return (LyricsDocument.Empty, true); }
    }

    private async Task<(LyricsDocument Document, bool Failed)> QuerySupplementalAsync(
        IReadOnlySet<LyricsProviderKind> excluded, LyricsQuery query, CancellationToken token,
        Action<LyricsDocument> reportCandidate, LyricsProviderKind? backup, LyricsProviderKind primary, Action reportProviderCompleted)
    {
        var original = LyricsDocument.Empty;
        var failed = false;
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);
        if (query.CollectSelectionCandidates)
            return await QueryFallbacksAsync(excluded, query, token, reportCandidate, backup, reportProviderCompleted).ConfigureAwait(false);
        if (backup is { } backupKind)
        {
            // Give the backup first admission, never the whole translation budget.
            // A progressive original, completion or 150 ms hedge admits remaining
            // sources while the backup continues, under the same three-second budget.
            var available = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var backupTask = QueryProviderAsync(backupKind, query, token, document =>
            {
                reportCandidate(document);
                var valid = Validate(document, query);
                if (HasUsableEvidence(valid)) available.TrySetResult(valid);
            }, token, reportProviderCompleted);
            var admissionWindow = Task.Delay(TimeSpan.FromMilliseconds(150), token);
            await Task.WhenAny(backupTask, available.Task, admissionWindow).ConfigureAwait(false);
            var remainingExcluded = excluded.Append(backupKind).ToHashSet();
            using var remainingStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<(LyricsDocument Document, bool Failed)>? remaining = null;
            var first = backupTask.IsCompletedSuccessfully ? backupTask.Result.Document :
                available.Task.IsCompletedSuccessfully ? available.Task.Result : LyricsDocument.Empty;
            if (NeedsTranslationSearch(first, target))
                remaining = QueryFallbacksAsync(remainingExcluded, query, remainingStop.Token, reportCandidate, null, reportProviderCompleted);
            try
            {
                var result = await backupTask.ConfigureAwait(false);
                original = result.Document;
                failed = result.Failed;
                if (HasCompleteTargetCoverage(original, target))
                    await remainingStop.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                // Retain every supplemental presentation task even when the parent or
                // preferred winner cancels while the backup is still retiring.
                if (remaining is not null)
                {
                    try
                    {
                        var rest = await remaining.ConfigureAwait(false);
                        original = SelectTranslation(original, rest.Document, query, target, primary, backup);
                        failed |= rest.Failed;
                    }
                    catch (OperationCanceledException) when (remainingStop.IsCancellationRequested)
                    { if (!HasCompleteTargetCoverage(original, target)) failed = true; }
                }
            }
            return (original, failed);
        }
        return await QueryFallbacksAsync(excluded, query, token, reportCandidate, null, reportProviderCompleted).ConfigureAwait(false);
    }

    private async Task<(LyricsDocument Document, bool Failed)> QueryFallbacksAsync(IReadOnlySet<LyricsProviderKind> excluded, LyricsQuery query, CancellationToken token, Action<LyricsDocument> reportCandidate, LyricsProviderKind? backup, Action reportProviderCompleted)
    {
        token.ThrowIfCancellationRequested();
        var stage = CancellationTokenSource.CreateLinkedTokenSource(token);
        // At most four provider requests are active. Allow a short quality window
        // after the first usable result so a slower, stronger verified match can
        // win, then cancel and drain the rest.
        var providerOrder = OnlineProviders.Where(kind => !excluded.Contains(kind)).ToArray();
        var pending = providerOrder.Select(kind => QueryProviderAsync(kind, query, stage.Token, reportCandidate, token, reportProviderCompleted)).ToList();
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
                if (!HasUsableEvidence(document)) continue;
                candidates.Add((document, index));
                var bestReadyRank = candidates.Where(candidate => candidate.Document.Lines.Count > 0 &&
                    (target.Length == 0 || HasCompleteTargetCoverage(candidate.Document, target)))
                    .Select(candidate => LyricsCandidateRules.SourceRank(candidate.Document.Provider, LyricsProviderKind.LocalLrc, backup))
                    .DefaultIfEmpty(int.MaxValue).Min();
                // A fast lower source cannot cut off a higher-priority request
                // still inside the shared source budget. The short drain window
                // is useful only once higher-priority work has already settled.
                if (!query.CollectSelectionCandidates && bestReadyRank < int.MaxValue &&
                    !pending.Any(task => LyricsCandidateRules.SourceRank(providerOrder[Array.IndexOf(all, task)],
                        LyricsProviderKind.LocalLrc, backup) < bestReadyRank))
                    qualityWindow ??= Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            return (LyricsCandidateRules.Best(candidates.Select(candidate =>
                LyricsCandidateRules.Describe("c" + candidate.Index, candidate.Document, target)), query,
                LyricsProviderKind.LocalLrc, backup), failed);
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
        document = LyricsBodyQualityPolicy.Normalize(document);
        if (document.Match is null) return document.BodyQuality is LyricsBodyQuality.PlaceholderOnly or LyricsBodyQuality.RequestFailed
            ? document : LyricsDocument.Empty;
        var match = document.Match;
        if (!string.IsNullOrEmpty(query.TrackIdentity) && !string.IsNullOrEmpty(match.TrackIdentity) && match.TrackIdentity != query.TrackIdentity)
            return LyricsDocument.Empty;
        if (string.IsNullOrWhiteSpace(match.CandidateId)) return LyricsDocument.Empty;
        var canonical = string.IsNullOrWhiteSpace(match.CanonicalTitle) ? match.Title : match.CanonicalTitle;
        var score = LyricsMatcher.Score(query, canonical, match.Artist, match.Album, match.DurationSeconds, match.ArtistAliases);
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
