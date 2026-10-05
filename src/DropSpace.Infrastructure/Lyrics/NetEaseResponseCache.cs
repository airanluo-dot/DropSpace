using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

public interface ILyricsResponseCache
{
    void ClearResponseCache();
}

// Bounded ten-minute reuse covers originals already satisfying the target and
// matching translations. Originals still needing translation live for one second
// to coalesce metadata handoffs without hiding recovery. Refresh
// and clear always fence old owners. No failures are retained for later callers.
// Reuse public responses, never matched documents: every caller still validates
// its exact title/credits/album/duration and binds its own track identity.
internal sealed class NetEaseResponseCache(LyricsHttpClient http, TimeProvider? timeProvider = null)
{
    private const int MaximumEntries = 128;
    private const int MaximumBytes = 16 * 1024 * 1024;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _bytes;
    private long _order;

    private sealed class Entry
    {
        public TaskCompletionSource<ResponseResult>? Completion;
        public int Users, Bytes;
        public long Order, Timestamp;
        public TimeSpan Lifetime;
        public string TranslationTarget = string.Empty;
        public string? Payload;
        public bool Pending;
        public CancellationToken OwnerToken;
    }

    private sealed record ResponseResult(string? Payload, ExceptionDispatchInfo? Error);

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); _bytes = 0; }
    }

    public async Task<JsonDocument> GetAsync(string url, CancellationToken token, bool refresh = false, string? translationTarget = null)
    {
        var target = LyricsTranslationPolicy.NormalizeLanguage(translationTarget);
        var isSearch = new Uri(url).AbsolutePath.Contains("search", StringComparison.Ordinal);
        if (refresh)
            lock (_gate)
                if (_entries.Remove(url, out var old)) _bytes -= old.Bytes;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var entry = Acquire(url);
            var ownsRequest = false;
            ResponseResult result = new(null, null);
            try
            {
                CancellationToken owner = default;
                Task<ResponseResult>? pending = null;
                string? saved;
                lock (_gate)
                {
                    var lifetime = !isSearch && target != entry.TranslationTarget
                        ? TimeSpan.FromSeconds(1) : entry.Lifetime;
                    saved = entry.Payload is not null &&
                        _clock.GetElapsedTime(entry.Timestamp) < lifetime ? entry.Payload : null;
                    if (saved is null)
                    {
                        if (entry.Pending) { owner = entry.OwnerToken; pending = entry.Completion!.Task; }
                        else
                        {
                            entry.Pending = true; entry.OwnerToken = token;
                            entry.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            ownsRequest = true;
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                if (saved is not null)
                {
                    LyricsRequestTrace.Record("response-cache", new { key = LyricsRequestTrace.Key(url), isSearch, target });
                    http.ReportNetEaseReuse();
                    return JsonDocument.Parse(saved);
                }
                if (pending is not null)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, owner);
                    try
                    {
                        var shared = await pending.WaitAsync(wait.Token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        shared.Error?.Throw();
                        if (shared.Payload is not null) return JsonDocument.Parse(shared.Payload);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && owner.IsCancellationRequested) { }
                    // Atomically captured ownership prevents a waiter from missing
                    // cancellation while another request takes over the entry.
                    continue;
                }
                var response = await http.GetAsync(url, token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var payload = response.RootElement.GetRawText();
                    result = new(payload, null);
                    if (Reusable(response.RootElement, url)) Store(url, entry, payload, ReuseLifetime(response.RootElement, url, target), target);
                    return response;
                }
                catch { response.Dispose(); throw; }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                result = new(null, ExceptionDispatchInfo.Capture(error));
                throw;
            }
            finally
            {
                TaskCompletionSource<ResponseResult>? completed = null;
                lock (_gate)
                {
                    if (ownsRequest)
                    {
                        completed = entry.Completion;
                        entry.Completion = null;
                        entry.Pending = false; entry.OwnerToken = default;
                    }
                    entry.Users--;
                }
                completed?.TrySetResult(result);
            }
        }
    }

    private Entry Acquire(string url)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(url, out var entry) && !(entry.Pending && entry.OwnerToken.IsCancellationRequested))
            { entry.Users++; entry.Order = ++_order; return entry; }
            if (entry is not null) { _entries.Remove(url); _bytes -= entry.Bytes; }
            while (_entries.Count >= MaximumEntries)
            {
                var oldest = _entries.Where(pair => pair.Value.Users == 0).MinBy(pair => pair.Value.Order);
                if (oldest.Value is null) return new() { Users = 1 };
                _entries.Remove(oldest.Key); _bytes -= oldest.Value.Bytes;
            }
            entry = new() { Users = 1, Order = ++_order };
            _entries.Add(url, entry);
            return entry;
        }
    }

    private void Store(string url, Entry entry, string payload, TimeSpan lifetime, string target)
    {
        var bytes = Encoding.UTF8.GetByteCount(payload);
        lock (_gate)
        {
            if (!_entries.TryGetValue(url, out var current) || !ReferenceEquals(current, entry)) return;
            while (_bytes - entry.Bytes + bytes > MaximumBytes)
            {
                var oldest = _entries.Where(pair => pair.Value.Users == 0 && pair.Value.Bytes > 0).MinBy(pair => pair.Value.Order);
                if (oldest.Value is null) return;
                _entries.Remove(oldest.Key); _bytes -= oldest.Value.Bytes;
            }
            _bytes += bytes - entry.Bytes;
            entry.Bytes = bytes; entry.Payload = payload; entry.Timestamp = _clock.GetTimestamp(); entry.Lifetime = lifetime; entry.TranslationTarget = target;
        }
    }

    private static TimeSpan ReuseLifetime(JsonElement root, string url, string? translationTarget)
    {
        if (new Uri(url).AbsolutePath.Contains("search", StringComparison.Ordinal))
            return LyricsHttpClient.Array(root, "result", "songs").Any()
                ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(1);
        // Use the exact provider pairing/admission policy, not just the presence
        // of tlyric: credit-only, wrong-language and unaligned text are not a win.
        var document = LyricsLanguagePolicy.IdentifyProviderTranslations(NetEaseLyricsProvider.ParseLyrics(root));
        var target = LyricsTranslationPolicy.NormalizeLanguage(translationTarget);
        var complete = document.Lines.Count > 0 && (target.Length == 0 ||
            !LyricsTranslationPolicy.NeedsProviderTranslation(document, target) ||
            LyricsTranslationPolicy.HasMatchingProviderTranslation(document, target));
        return complete ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(1);
    }

    private static bool Reusable(JsonElement root, string url)
    {
        if (root.ValueKind != JsonValueKind.Object || LyricsHttpClient.Number(root, "code") != 200) return false;
        if (new Uri(url).AbsolutePath.Contains("search", StringComparison.Ordinal))
            return root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array &&
                songs.EnumerateArray().All(song => song.ValueKind == JsonValueKind.Object &&
                    song.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String &&
                    song.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String);
        return (HasLyric(root, "lrc") || HasLyric(root, "yrc")) &&
            new[] { "lrc", "yrc", "tlyric", "ytlrc" }.All(name => OptionalLyric(root, name));
    }

    // Null/empty optional fields mean no data, not a corrupt successful response.
    // Strings/arrays or non-string lyric properties remain non-reusable.
    private static bool OptionalLyric(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.Object &&
        (!value.TryGetProperty("lyric", out var lyric) || lyric.ValueKind is JsonValueKind.String or JsonValueKind.Null);

    private static bool HasLyric(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("lyric", out var lyric) && lyric.ValueKind == JsonValueKind.String;
}
