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

    public Task<LyricsQueryResult> QueryDetailedAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken,
        bool refresh = false, Action<LyricsDocument>? reportOriginal = null) =>
        QueryDetailedAsync(query, settings, cancellationToken, refresh, reportOriginal, TimeSpan.Zero);

    public async Task<LyricsQueryResult> QueryDetailedAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken,
        bool refresh, Action<LyricsDocument>? reportOriginal, TimeSpan providerAdmissionDelay)
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
        // Keep the target-specific source preference and the previous on-disk key shape
        // so Beta 7 documents can be re-admitted using the current recording rules.
        string TargetSourceKey(string version, string matcher, LyricsQuery identity) => JsonSerializer.Serialize(new
        {
            version, matcher, primary = kind, backup, settings.SearchRemainingProviders, target,
            identity.TrackIdentity, identity.Title, identity.Artist, identity.AlbumArtist, identity.Album,
            durationTicks = identity.Duration.Ticks,
        });
        string OriginalSourceKey(string version, string matcher, LyricsQuery identity) => JsonSerializer.Serialize(new
        {
            version, matcher, primary = kind, backup, settings.SearchRemainingProviders,
            identity.TrackIdentity, identity.Title, identity.Artist, identity.AlbumArtist, identity.Album,
            durationTicks = identity.Duration.Ticks,
        });
        const string version = "source-v11-recording-timing";
        var key = target.Length == 0 ? OriginalSourceKey(version, LyricsMatcher.Version, query) : TargetSourceKey(version, LyricsMatcher.Version, query);
        LyricsRequestTrace.Record("cache-lookup", new { key = LyricsRequestTrace.Key(key), version, target, refresh });
        var generation = _cache?.Generation ?? _memory.Generation;
        var cachedFallback = LyricsDocument.Empty;
        if (kind != LyricsProviderKind.LocalLrc)
        {
            var cached = _cache is null ? _memory.Read(key) : await _cache.ReadDocumentAsync(key, cancellationToken).ConfigureAwait(false);
            var legacy = false;
            if (cached is null && _cache is not null && query.CombinedAppleCredit is { } combined)
            {
                // A winner saved before the album was known could belong to a different release.
                // Keep an offline fallback only when the source independently returned this album.
                var raw = query with { Artist = combined, AlbumArtist = combined, Album = string.Empty };
                const string beta8Matcher = "recording-identity-v7-corroborated-catalogue";
                var oldKey = target.Length == 0 ? OriginalSourceKey(version, beta8Matcher, raw) : TargetSourceKey(version, beta8Matcher, raw);
                var old = await _cache.ReadDocumentAsync(oldKey, cancellationToken).ConfigureAwait(false);
                if (old is not null && LyricsCandidateRules.HasAllRecordingEvidence(query, old.Match))
                {
                    cached = old;
                    legacy = true;
                }
            }
            if (cached is null && _cache is not null)
            {
                const string previousMatcher = "recording-identity-v6-bounded-name-aliases";
                var previous = target.Length == 0 ? OriginalSourceKey("source-v5-quality2", previousMatcher, query) :
                    TargetSourceKey("source-v10-quality2", previousMatcher, query);
                cached = await _cache.ReadDocumentAsync(previous, cancellationToken).ConfigureAwait(false);
                legacy = cached is not null;
            }
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
            var legacyPlain = legacy && cached is { Provider: LyricsProviderKind.Lrclib, HasSynchronizedTiming: false } &&
                LyricsCandidateRules.HasAllRecordingEvidence(query, cached.Match);
            if (cached is { Provider: LyricsProviderKind.Lrclib } &&
                cached.ProviderDataRevision < LrclibLyricsProvider.DataRevision && !legacyPlain) cached = null;
            if (cached is { Provider: LyricsProviderKind.Amll } &&
                cached.ProviderDataRevision < AmllLyricsProvider.DataRevision) cached = null;
            var validated = cached is null ? LyricsDocument.Empty : Validate(LyricsLanguagePolicy.IdentifyProviderTranslations(cached), query);
            // An old single winner cannot establish that another artist, recording
            // length or spelling was absent from its original candidate set.
            if (legacy && validated.Match is { } previousMatch && LyricsMatcher.NeedsRecordingComparison(query, previousMatch))
                validated = LyricsDocument.Empty;
            var hit = HasUsableEvidence(validated);
            LyricsRequestTrace.Record("cache-result", new { usable = hit, document = LyricsRequestTrace.Describe(validated) });
            if (cached is not null && !hit)
                LyricsRequestTrace.Record("cache-rejected", new { reason = "unusable-or-mismatched-source", qualityVersion = LyricsBodyQualityPolicy.Version });
            if (hit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_languageIdentifier is not null && validated.Lines.Count > 0 &&
                    (target.Length > 0 || LyricsMatcher.HasDeclaredOriginalLanguage(query.Title)))
                    validated = await _languageIdentifier.PrepareAsync(validated, target, cancellationToken).ConfigureAwait(false);
                if (!LyricsMatcher.OriginalLanguageConflicts(query, validated))
                {
                    cachedFallback = validated;
                    // An old LRCLIB document predates the alternate-original check.
                    // Its verified plain text is an offline fallback; wait for the
                    // current catalogue before showing it on a connected lookup.
                    if (!refresh && !legacyPlain && (backup is null && !settings.SearchRemainingProviders ||
                        LyricsCandidateRules.HasAllRecordingEvidence(query, validated.Match)))
                        TryReportOriginal(reportOriginal, validated);
                    if (!refresh && !legacy && !validated.SourceSearchIncomplete && LyricsParser.HasCurrentTiming(validated) &&
                        (validated.HasSynchronizedTiming || IsConfirmedInstrumental(validated) || backup is null && !settings.SearchRemainingProviders))
                        return new(validated, LyricsQueryStatus.Found)
                        { SelectionCandidates = new([LyricsCandidateRules.Describe("c0", validated, target)], 0) };
                }
            }
            // Let provisional media metadata settle only before a network search.
            if (!refresh && providerAdmissionDelay > TimeSpan.Zero)
                await Task.Delay(providerAdmissionDelay, cancellationToken).ConfigureAwait(false);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        using var candidates = new CandidateSearch(query, target, deadline.Token, kind, backup, reportOriginal,
            settings.SelectionMode != LyricsSelectionMode.Rules, backup is not null || kind != LyricsProviderKind.LocalLrc && settings.SearchRemainingProviders);
        var document = LyricsDocument.Empty;
        async Task<LyricsQueryResult> FinishAsync(LyricsDocument selected, bool sourceFailed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ambiguousSources = candidates.AmbiguitySources;
            if (ambiguousSources.Length > 0)
            {
                candidates.Freeze(LyricsDocument.Empty);
                LyricsRequestTrace.Record("ambiguous-recording", new { sources = ambiguousSources.Select(source => source.ToString()).ToArray() });
                return CompleteQueryResult(new LyricsDocument([], kind) { BodyQuality = LyricsBodyQuality.Ambiguous },
                    candidates.CompletedProviderCount, sourceFailed, false) with
                    { AmbiguitySources = ambiguousSources, SelectionCandidates = candidates.Snapshot };
            }
            var usingFallback = (sourceFailed || candidates.CompletedProviderCount == 0) && HasUsableEvidence(cachedFallback) &&
                ReferenceEquals(SelectTranslation(selected, cachedFallback, query, target, kind, backup), cachedFallback);
            if (usingFallback)
                selected = cachedFallback;
            selected = Validate(selected, query);
            // Stamp fresh validated reads at the shared cache boundary too.
            if (!usingFallback && HasUsableEvidence(selected)) selected = selected with { ProviderDataRevision = selected.Provider switch
            {
                LyricsProviderKind.NetEase => NetEaseLyricsProvider.DataRevision,
                LyricsProviderKind.Kugou => KugouLyricsProvider.DataRevision,
                LyricsProviderKind.QqMusic => QqMusicLyricsProvider.DataRevision,
                LyricsProviderKind.Lrclib => LrclibLyricsProvider.DataRevision,
                LyricsProviderKind.Amll => AmllLyricsProvider.DataRevision,
                _ => selected.ProviderDataRevision,
            } };
            if (_languageIdentifier is not null && selected.Lines.Count > 0 &&
                (target.Length > 0 || LyricsMatcher.HasDeclaredOriginalLanguage(query.Title)))
                selected = await _languageIdentifier.PrepareAsync(selected, target, cancellationToken).ConfigureAwait(false);
            if (LyricsMatcher.OriginalLanguageConflicts(query, selected)) selected = LyricsDocument.Empty;
            cancellationToken.ThrowIfCancellationRequested();
            var incomplete = NeedsTranslationSearch(selected, target) && sourceFailed;
            selected = selected with { SourceSearchIncomplete = sourceFailed && NeedsMoreSources(query, selected, target) &&
                (!selected.HasSynchronizedTiming || selected.Match is { } match &&
                    (LyricsMatcher.NeedsRecordingComparison(query, match) ||
                     query.Duration > TimeSpan.Zero && LyricsMatcher.DurationError(query, match.DurationSeconds) > 2)) };
            candidates.Freeze(selected);
            // Cache the verified winner actually delivered to the player. Its original
            // lyrics are reusable even if another source failed or offered no translation.
            if (HasUsableEvidence(selected) && kind != LyricsProviderKind.LocalLrc && !usingFallback)
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
            return CompleteQueryResult(selected, candidates.CompletedProviderCount, sourceFailed, incomplete) with { SelectionCandidates = candidates.Snapshot };
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
            if ((query.CollectSelectionCandidates || NeedsMoreSources(query, first, target)) && allowed.Count > 0)
            {
                LyricsRequestTrace.Record("fallback", new { providers = allowed.Select(value => value.ToString()).ToArray(), target });
                var excluded = OnlineProviders.Where(value => !allowed.Contains(value)).ToHashSet();
                supplemental = QuerySupplementalSafelyAsync(excluded, query, supplementalStop.Token, candidates.Report, backup, kind, candidates.ReportProviderCompleted);
            }
            // Own both tasks until they retire, including the cancellation path.
            if (supplemental is not null)
            {
                var all = Task.WhenAll(primaryTask, supplemental);
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
            return await FinishAsync(document, primary.Failed || fallbackFailed).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidDataException or XmlException or FormatException or RegexMatchTimeoutException)
        { return await FinishAsync(candidates.Document, sourceFailed: true).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FinishAsync(candidates.Document, sourceFailed: true).ConfigureAwait(false);
        }
    }
    private static void TryReportOriginal(Action<LyricsDocument>? report, LyricsDocument document)
    {
        try { report?.Invoke(document); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    // Compatibility model snapshots have a short collection window. The production
    // rules search uses individual provider deadlines and the shared request deadline.
    private sealed class CandidateSearch : IDisposable
    {
        private readonly object _gate = new();
        private readonly LyricsQuery _query;
        private readonly string _target;
        private readonly LyricsProviderKind _primary;
        private readonly LyricsProviderKind? _backup;
        private readonly CancellationTokenSource _budget;
        private LyricsDocument _document = LyricsDocument.Empty;
        private bool _started, _provisionalStarted, _closed, _truncated, _originalReported;
        private readonly Action<LyricsDocument>? _reportOriginal;
        private readonly bool _captureCandidates, _hasOtherSources;
        private readonly Dictionary<string, LyricsDocument> _collected = new(StringComparer.Ordinal);
        private int _collectedBytes;
        private int _prunedCandidates;
        private const int CandidatesPerSource = 3;
        private int _completedProviderCount;
        public int CompletedProviderCount => Volatile.Read(ref _completedProviderCount);
        public void ReportProviderCompleted() => Interlocked.Increment(ref _completedProviderCount);
        private readonly TaskCompletionSource _originalAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OriginalAvailable => _originalAvailable.Task;
        private readonly List<(LyricsProviderKind Provider, LyricsMatchInfo Match)> _competing = [];
        private readonly HashSet<LyricsProviderKind> _ambiguousProviders = [];
        public LyricsProviderKind[] AmbiguitySources
        {
            get
            {
                lock (_gate)
                {
                    if (_document.Match is not { } best) return _ambiguousProviders.ToArray();
                    var sources = _competing.Where(other =>
                        LyricsCandidateRules.RecordingEvidence(_query, other.Match) == LyricsCandidateRules.RecordingEvidence(_query, best) &&
                        LyricsMatcher.ConflictingCandidates(_query, best, other.Match))
                        .Select(other => other.Provider).ToHashSet();
                    // A different source may resolve a catalogue's internal conflict
                    // with a fully corroborated recording.
                    if (_ambiguousProviders.Contains(_document.Provider) || !LyricsCandidateRules.HasAllRecordingEvidence(_query, best))
                        sources.UnionWith(_ambiguousProviders);
                    if (sources.Count > 0) sources.Add(_document.Provider);
                    return sources.ToArray();
                }
            }
        }
        public CandidateSearch(LyricsQuery query, string target, CancellationToken token, LyricsProviderKind primary, LyricsProviderKind? backup, Action<LyricsDocument>? reportOriginal, bool captureCandidates, bool hasOtherSources)
        {
            _reportOriginal = reportOriginal;
            _captureCandidates = captureCandidates;
            _hasOtherSources = hasOtherSources;
            _query = query;
            _primary = primary;
            _backup = backup;
            _target = target;
            _budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = _budget.Token;
        }
        public CancellationToken Token { get; }
        public LyricsDocument Document { get { lock (_gate) return _document; } }
        public void Freeze(LyricsDocument selected)
        {
            lock (_gate) { _document = selected; _closed = true; }
        }
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
            if (candidate.BodyQuality == LyricsBodyQuality.Ambiguous)
            {
                lock (_gate)
                {
                    if (!_closed && !Token.IsCancellationRequested) _ambiguousProviders.Add(candidate.Provider);
                }
                return;
            }
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
                    if (_closed && valid.TranslationAdmission?.Decision == LyricsWholeTrackDecision.ProviderTarget &&
                        LyricsLanguagePolicy.IsAdmissionCurrent(valid, _target) &&
                        valid.Match is { } lateMatch && lateMatch.TrackIdentity == _query.TrackIdentity &&
                        LyricsCandidateRules.CanUpgradeFrozenRecording(_document, valid) &&
                        ReferenceEquals(SelectTranslation(_document, valid, _query, _target, _primary, _backup), valid))
                    {
                        _document = valid;
                        TryReportOriginal(_reportOriginal, valid);
                    }
                    return;
                }
                if (!_started && HasUsableEvidence(valid))
                {
                    _started = true;
                    // Production rules let an admitted backup finish within its own timeout.
                    // The bounded model snapshot alone needs the shorter collection window.
                    if (_captureCandidates) _budget.CancelAfter(TimeSpan.FromSeconds(3));
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
                var needsComparison = valid.Match is { } identity && LyricsMatcher.NeedsRecordingComparison(_query, identity);
                if (needsComparison && !_competing.Contains((valid.Provider, valid.Match!))) _competing.Add((valid.Provider, valid.Match!));
                // A validated translation must not wait behind another provider's timeout.
                // Publish only improvements under the same ranking used for the final result.
                // Serialize callbacks so a racing older original cannot overwrite the upgrade.
                if (!ReferenceEquals(previous, _document) && !needsComparison &&
                    (!_hasOtherSources || LyricsCandidateRules.HasAllRecordingEvidence(_query, valid.Match)))
                {
                    TryReportOriginal(_reportOriginal, _document);
                }
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

    private static bool NeedsTranslationSearch(LyricsDocument document, string target) =>
        !IsConfirmedInstrumental(document) && (document.Lines.Count == 0 ||
        LyricsTranslationPolicy.NeedsProviderTranslationLookup(document, target));

    private static bool NeedsMoreSources(LyricsQuery query, LyricsDocument document, string target) =>
        NeedsTranslationSearch(document, target) || !LyricsCandidateRules.HasAllRecordingEvidence(query, document.Match) ||
        document.Match is { } match && LyricsMatcher.NeedsRecordingComparison(query, match) ||
        !IsConfirmedInstrumental(document) && !document.HasSynchronizedTiming;

    private static bool IsConfirmedInstrumental(LyricsDocument document) =>
        document.Lines.Count == 0 && document.BodyQuality == LyricsBodyQuality.ConfirmedInstrumental && document.Match is not null;

    private static bool HasUsableEvidence(LyricsDocument document) => document.Lines.Count > 0 || IsConfirmedInstrumental(document);

    private static LyricsQueryResult CompleteQueryResult(LyricsDocument document, int completedProviderCount, bool sourceFailed, bool translationIncomplete)
    {
        var status = HasUsableEvidence(document) ? LyricsQueryStatus.Found :
            document.BodyQuality == LyricsBodyQuality.Ambiguous ? LyricsQueryStatus.Ambiguous :
            !sourceFailed && completedProviderCount > 0 ? LyricsQueryStatus.NotFound : LyricsQueryStatus.Failed;
        if (status == LyricsQueryStatus.Failed)
            document = document with { BodyQuality = LyricsBodyQuality.RequestFailed };
        else if (status == LyricsQueryStatus.NotFound && document.BodyQuality == LyricsBodyQuality.RequestFailed)
            document = document with { BodyQuality = LyricsBodyQuality.NoLyrics };
        LyricsRequestTrace.Record("source-search-result", new { completedProviders = completedProviderCount, sourceFailed,
            status = status.ToString(), translationIncomplete });
        return new(document, status, translationIncomplete);
    }

    private static LyricsDocument SelectTranslation(LyricsDocument current, LyricsDocument candidate, LyricsQuery query, string target,
        LyricsProviderKind primary, LyricsProviderKind? backup)
    {
        if (!HasUsableEvidence(candidate)) return current;
        if (!HasUsableEvidence(current)) return candidate;
        var left = LyricsCandidateRules.Describe("candidate", candidate, target);
        var right = LyricsCandidateRules.Describe("current", current, target);
        return LyricsCandidateRules.CompareCandidates(left, right, query, primary, backup) < 0 ? candidate : current;
    }

    private sealed record ProviderResult(LyricsDocument Document, bool Failed, bool AccessUnavailable = false);
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
            if (!result.Failed && !result.AccessUnavailable) reportProviderCompleted();
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
            var checkLanguage = _languageIdentifier is not null && LyricsMatcher.HasDeclaredOriginalLanguage(query.Title);
            var pendingOriginals = checkLanguage ? new Dictionary<string, LyricsDocument>(StringComparer.Ordinal) : null;
            void Preview(LyricsDocument reported)
            {
                if (pendingOriginals is not null && reported.Lines.Count > 0 && reported.TranslationAdmission is null &&
                    reported.Match?.CandidateId is { } id)
                {
                    lock (pendingOriginals) pendingOriginals[id] = reported;
                    return;
                }
                reportCandidate(reported);
            }
            var document = provider is IProgressiveLyricsProvider progressive
                ? await progressive.QueryAsync(query, token, Preview).ConfigureAwait(false)
                : await provider.QueryAsync(query, token).ConfigureAwait(false);
            // Cancellation wins even when a transport returns a stale success instead of throwing.
            token.ThrowIfCancellationRequested();
            var target = query.PreferredTranslationLanguage ?? "";
            if (_languageIdentifier is not null && (target.Length > 0 || checkLanguage) && document.Lines.Count > 0)
                document = await _languageIdentifier.PrepareAsync(document, target, token).ConfigureAwait(false);
            if (pendingOriginals is not null)
            {
                var returnedId = document.Match?.CandidateId;
                document = Validate(document, query);
                LyricsDocument[] others;
                lock (pendingOriginals) others = pendingOriginals.Values.Where(other => other.Match?.CandidateId != returnedId).ToArray();
                foreach (var other in others)
                {
                    var prepared = await _languageIdentifier!.PrepareAsync(other, target, token).ConfigureAwait(false);
                    var approved = Validate(prepared, query);
                    if (!HasUsableEvidence(approved)) continue;
                    reportCandidate(approved);
                    document = SelectTranslation(document, approved, query, target, kind, null);
                }
            }
            reportCandidate(document);
            LyricsRequestTrace.Record("provider-body", LyricsRequestTrace.Describe(LyricsBodyQualityPolicy.Normalize(document)));
            var validated = Validate(document, query);
            LyricsDiagnostics.Report(_diagnostic, new(kind, LyricsDiagnosticStage.Query,
                validated.BodyQuality == LyricsBodyQuality.RequestFailed ? LyricsDiagnosticOutcome.TransportFailure :
                    validated.BodyQuality == LyricsBodyQuality.Ambiguous ? LyricsDiagnosticOutcome.Ambiguous :
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
            // The anonymous QQ catalogue may refuse access even for a well-formed request.
            // Preserve its actual diagnostic, but it cannot invalidate an exhaustive clean
            // search of the other accessible catalogues. QQ alone still has zero completions.
            if (kind == LyricsProviderKind.QqMusic && _providers.QqMusicSignedOut &&
                (error is LyricsProviderRejectedException { ApiCode: -2 or 1000 or 2001 or 101010 or 401 or 403 } ||
                 error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }))
                return new(LyricsDocument.Empty, false, AccessUnavailable: true);
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
            // sources while the backup continues within its provider deadline.
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
            if (NeedsMoreSources(query, first, target))
                remaining = QueryFallbacksAsync(remainingExcluded, query, remainingStop.Token, reportCandidate, null, reportProviderCompleted);
            try
            {
                var result = await backupTask.ConfigureAwait(false);
                original = result.Document;
                failed = result.Failed;
                if (!NeedsMoreSources(query, original, target))
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
                    { if (NeedsMoreSources(query, original, target)) failed = true; }
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
                var bestReadyRank = candidates.Where(candidate => candidate.Document.Lines.Count > 0 && !NeedsMoreSources(query, candidate.Document, target))
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
        if (document.Match is null) return document.BodyQuality is LyricsBodyQuality.PlaceholderOnly or LyricsBodyQuality.RequestFailed or LyricsBodyQuality.Ambiguous
            ? document : LyricsDocument.Empty;
        var match = document.Match;
        if (!string.IsNullOrEmpty(query.TrackIdentity) && !string.IsNullOrEmpty(match.TrackIdentity) && match.TrackIdentity != query.TrackIdentity)
            return LyricsDocument.Empty;
        if (string.IsNullOrWhiteSpace(match.CandidateId)) return LyricsDocument.Empty;
        var score = LyricsMatcher.Score(query, match);
        return score < 4 || LyricsMatcher.OriginalLanguageConflicts(query, document) ? LyricsDocument.Empty :
            document with { Match = match with { Score = score, TrackIdentity = query.TrackIdentity } };
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
