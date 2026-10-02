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
    private readonly TimeSpan _providerTimeout = TimeSpan.FromSeconds(8);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<LyricsProviderKind, SemaphoreSlim> _providerGates = new();

    // Compatibility callers retain a bounded process-memory cache; production injects
    // the shared persistent store explicitly. No temporary directory is owned here.
    public LyricsService(LyricsProviderRegistry providers) => _providers = providers;

    public LyricsService(LyricsProviderRegistry providers, LyricsCache cache)
    {
        _providers = providers;
        _cache = cache;
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
        if (!settings.Enabled || string.IsNullOrWhiteSpace(query.Title)) return new(LyricsDocument.Empty, LyricsQueryStatus.Disabled);
        var kind = settings.Mode == LyricsMode.LocalLrc ? LyricsProviderKind.LocalLrc : settings.Provider;
        // Keep sub-second duration differences in the cache key. A same-metadata Apple Music
        // replacement (for example an edit/live file with the same displayed title) must not
        // inherit a document fetched for a nearby duration merely because both durations were
        // truncated to the same whole second.
        var backup = OnlineBackup(settings, kind);
        var key = JsonSerializer.Serialize(new
        {
            version = "source-v2", primary = kind, backup, settings.SearchRemainingProviders,
            query.TrackIdentity, query.Title, query.Artist, query.AlbumArtist, query.Album,
            durationTicks = query.Duration.Ticks,
        });
        var generation = _cache?.Generation ?? _memory.Generation;
        if (kind != LyricsProviderKind.LocalLrc && !refresh)
        {
            var cached = _cache is null ? _memory.Read(key) : await _cache.ReadDocumentAsync(key, cancellationToken).ConfigureAwait(false);
            var validated = cached is null ? LyricsDocument.Empty : Validate(LyricsLanguagePolicy.IdentifyProviderTranslations(cached), query);
            if (validated.Lines.Count > 0)
                return new(validated, LyricsQueryStatus.Found);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        try
        {
            var primary = await QueryProviderAsync(kind, query, deadline.Token).ConfigureAwait(false);
            var document = Validate(primary.Document, query);
            var fallbackFailed = false;
            if (document.Lines.Count == 0 && backup is { } backupKind)
            {
                var backupResult = await QueryProviderAsync(backupKind, query, deadline.Token).ConfigureAwait(false);
                document = backupResult.Document;
                fallbackFailed |= backupResult.Failed;
            }
            if (document.Lines.Count == 0 && kind != LyricsProviderKind.LocalLrc && settings.SearchRemainingProviders)
            {
                var excluded = backup is { } selectedBackup ? new HashSet<LyricsProviderKind> { kind, selectedBackup } : [kind];
                var fallback = await QueryFallbacksAsync(excluded, query, deadline.Token).ConfigureAwait(false);
                document = fallback.Document;
                fallbackFailed |= fallback.Failed;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Lines.Count > 0 && kind != LyricsProviderKind.LocalLrc)
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
            return new(document, document.Lines.Count > 0 ? LyricsQueryStatus.Found : failed ? LyricsQueryStatus.Failed : LyricsQueryStatus.NotFound);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidDataException or XmlException or FormatException or RegexMatchTimeoutException)
        { return new(LyricsDocument.Empty, LyricsQueryStatus.Failed); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(LyricsDocument.Empty, LyricsQueryStatus.Failed); }
    }
    private sealed record ProviderResult(LyricsDocument Document, bool Failed);
    private async Task<ProviderResult> QueryProviderAsync(LyricsProviderKind kind, LyricsQuery query, CancellationToken token)
    {
        var cancellation = new ProviderCancellation(token, _providerTimeout);
        var requestToken = cancellation.Token;
        var invocationOwnsCancellation = false;
        Task<ProviderResult>? invocation = null;
        // Bound actual calls, including transports still retiring after the caller left.
        // A deadline releases the presentation waiter, never the provider's resources/slot.
        var gate = _providerGates.GetOrAdd(kind, _ => new SemaphoreSlim(2, 2));
        try
        {
            await gate.WaitAsync(requestToken).ConfigureAwait(false);
            invocationOwnsCancellation = true;
            invocation = InvokeProviderAsync(kind, query, requestToken, cancellation, gate);
            var result = await invocation.WaitAsync(requestToken).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        {
            // Let cooperative HTTP/stream cleanup finish before returning, but keep a
            // bounded waiter if a transport ignores cancellation. The invocation still
            // owns its gate and timeout after this small retirement grace expires.
            if (invocation is not null)
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
        CancellationToken token, ProviderCancellation cancellation, SemaphoreSlim gate)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var document = await _providers.Get(kind).QueryAsync(query, token).ConfigureAwait(false);
            // Cancellation wins even when a transport returns a stale success instead of throwing.
            token.ThrowIfCancellationRequested();
            return new(Validate(document, query), false);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(LyricsDocument.Empty, true); }
        finally { await cancellation.CompleteAsync().ConfigureAwait(false); gate.Release(); }
    }

    private async Task<(LyricsDocument Document, bool Failed)> QueryFallbacksAsync(IReadOnlySet<LyricsProviderKind> excluded, LyricsQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stage = CancellationTokenSource.CreateLinkedTokenSource(token);
        // At most four provider requests are active. Allow a short quality window
        // after the first usable result so a slower, stronger verified match can
        // win, then cancel and drain the rest.
        var pending = OnlineProviders.Where(kind => !excluded.Contains(kind)).Select(kind => QueryProviderAsync(kind, query, stage.Token)).ToList();
        var all = pending.ToArray();
        var candidates = new List<(LyricsDocument Document, int Index)>();
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
                qualityWindow ??= Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            return (candidates.OrderByDescending(candidate => candidate.Document.Match?.Score ?? 0)
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
        private readonly CancellationTokenRegistration _registration;
        private readonly Timer _deadline;
        private Task _callbacks = Task.CompletedTask;
        private bool _requested, _closed;

        public ProviderCancellation(CancellationToken parent, TimeSpan timeout)
        {
            Token = _source.Token;
            _registration = parent.UnsafeRegister(static state => ((ProviderCancellation)state!).Request(), this);
            _deadline = new Timer(static state => ((ProviderCancellation)state!).Request(), this, timeout, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token { get; }
        private void Request()
        {
            lock (_gate)
            {
                if (_closed || _requested) return;
                _requested = true;
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
            finally { _source.Dispose(); }
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
        if (_cache is null) _memory.Clear();
        else _cache.Clear();
    }

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
