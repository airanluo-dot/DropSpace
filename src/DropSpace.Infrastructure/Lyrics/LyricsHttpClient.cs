using System.Text;
using System.Text.Json;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LyricsHttpClient(HttpClient client, Action<LyricsDiagnostic>? diagnostic = null, QqMusicSession? qqMusicSession = null,
    TimeProvider? timeProvider = null)
{
    internal QqMusicSession? QqSession => qqMusicSession;
    private const int MaximumResponseBytes = 3 * 1024 * 1024;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly LyricsRequestHealth _health = new(timeProvider ?? TimeProvider.System);
    private long _netEaseNextRequest;
    private readonly SemaphoreSlim _netEaseAdmission = new(1, 1);
    public void RetryTransientFailures(DropSpace.Core.Models.LyricsProviderKind provider) => _health.RetryTransientFailures(provider);
    internal void ReportNetEaseReuse() => ReportReuse(DropSpace.Core.Models.LyricsProviderKind.NetEase);
    internal void ReportReuse(DropSpace.Core.Models.LyricsProviderKind provider) => LyricsDiagnostics.Report(diagnostic,
        new(provider, LyricsDiagnosticStage.Reuse, LyricsDiagnosticOutcome.Found, 0));
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    { "music.163.com", "c.y.qq.com", "u.y.qq.com", "lyrics.kugou.com", "songsearch.kugou.com", "lrclib.net", "api.amll.dev" };

    public async Task<JsonDocument> GetAsync(string url, CancellationToken token, string? referer = null)
        => await RequestAsync(url, token, referer, null, null).ConfigureAwait(false);

    public async Task<JsonDocument> PostAsync(string url, string json, CancellationToken token, string? referer = null,
        LyricsDiagnosticStage stage = LyricsDiagnosticStage.Search)
        => await RequestAsync(url, token, referer, json, stage).ConfigureAwait(false);

    private async Task<JsonDocument> RequestAsync(string url, CancellationToken token, string? referer, string? json,
        LyricsDiagnosticStage? requestedStage)
    {
        var uri = new Uri(url);
        var provider = uri.Host.ToLowerInvariant() switch
        {
            "music.163.com" => DropSpace.Core.Models.LyricsProviderKind.NetEase,
            "c.y.qq.com" or "u.y.qq.com" => DropSpace.Core.Models.LyricsProviderKind.QqMusic,
            "lyrics.kugou.com" or "songsearch.kugou.com" => DropSpace.Core.Models.LyricsProviderKind.Kugou,
            "lrclib.net" => DropSpace.Core.Models.LyricsProviderKind.Lrclib,
            _ => DropSpace.Core.Models.LyricsProviderKind.Amll,
        };
        var stage = requestedStage ?? (uri.AbsolutePath.Contains("search", StringComparison.OrdinalIgnoreCase)
            ? LyricsDiagnosticStage.Search : LyricsDiagnosticStage.Lyric);
        stage = stage == LyricsDiagnosticStage.Search ? LyricsDiagnosticStage.Search : LyricsDiagnosticStage.Lyric;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        long healthRevision = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            healthRevision = _health.ThrowIfUnavailable(provider, stage);
            if (provider == DropSpace.Core.Models.LyricsProviderKind.NetEase)
            {
                // A cancelled metadata handoff never reserves a future slot. Search
                // and lyric calls share pacing, but rejection of search must not block
                // lyrics reached through an already validated cached catalogue.
                await _netEaseAdmission.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var delay = _netEaseNextRequest - Environment.TickCount64;
                    if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    healthRevision = _health.ThrowIfUnavailable(provider, stage);
                    _netEaseNextRequest = Environment.TickCount64 + 500;
                }
                finally { _netEaseAdmission.Release(); }
            }
            var (document, status) = await GetCoreAsync(url, token, referer, json, provider, stage).ConfigureAwait(false);
            var (code, rejected) = BusinessStatus(document.RootElement, provider, uri.Host == "songsearch.kugou.com");
            if (code == 429 || provider == DropSpace.Core.Models.LyricsProviderKind.NetEase && code == 405)
                _health.RateLimited(provider, stage);
            else if (rejected) _health.Failed(provider, stage);
            else _health.Succeeded(provider, stage, healthRevision);
            LyricsRequestTrace.Record("http", new { provider = provider.ToString(), endpoint = uri.GetLeftPart(UriPartial.Path), status, code, rejected });
            LyricsDiagnostics.Report(diagnostic, new(provider, stage,
                rejected && (code == 429 || provider == DropSpace.Core.Models.LyricsProviderKind.NetEase && code == 405)
                    ? LyricsDiagnosticOutcome.RateLimited :
                rejected ? LyricsDiagnosticOutcome.Rejected : LyricsDiagnosticOutcome.Found,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                HttpStatus: status, ApiCode: code));
            return document;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!token.IsCancellationRequested && error is not LyricsRequestBackoffException && IsTransientFailure(error))
                _health.Failed(provider, stage);
            else if (!token.IsCancellationRequested && error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
                _health.Succeeded(provider, stage, healthRevision);
            LyricsRequestTrace.Record("http-failure", new { provider = provider.ToString(), endpoint = uri.GetLeftPart(UriPartial.Path),
                status = error is HttpRequestException requestError ? (int?)requestError.StatusCode : null,
                category = LyricsDiagnostics.Classify(error, token.IsCancellationRequested).ToString() });
            LyricsDiagnostics.Report(diagnostic, new(provider, stage,
                LyricsDiagnostics.Classify(error, token.IsCancellationRequested),
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                HttpStatus: error is HttpRequestException httpError ? (int?)httpError.StatusCode : null));
            throw;
        }
    }

    private static bool IsTransientFailure(Exception error) => error switch
    {
        LyricsProviderRejectedException => false,
        HttpRequestException request => request.StatusCode is null or System.Net.HttpStatusCode.RequestTimeout || (int?)request.StatusCode >= 500,
        OperationCanceledException or IOException or JsonException => true,
        _ => false,
    };

    // Reporting only: each adapter remains responsible for its own response
    // contract and terminal rejection. HTTP 200 is not provider-level success.
    private static (int? Code, bool Rejected) BusinessStatus(JsonElement root,
        DropSpace.Core.Models.LyricsProviderKind provider, bool kugouCatalog)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, false);
        var fields = provider switch
        {
            DropSpace.Core.Models.LyricsProviderKind.NetEase => new[] { "code" },
            DropSpace.Core.Models.LyricsProviderKind.QqMusic => ["code", "retcode", "subcode"],
            DropSpace.Core.Models.LyricsProviderKind.Kugou => ["error_code", "errcode", "status"],
            DropSpace.Core.Models.LyricsProviderKind.Amll => ["status"],
            _ => [],
        };
        int? seen = null;
        foreach (var field in fields)
        {
            if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var code)) continue;
            seen = code;
            var rejected = provider switch
            {
                DropSpace.Core.Models.LyricsProviderKind.NetEase => code > 0 && code != 200,
                DropSpace.Core.Models.LyricsProviderKind.Amll => code != 200,
                DropSpace.Core.Models.LyricsProviderKind.Kugou => !KugouResponseStatus.IsSuccess(field, code, kugouCatalog),
                _ => code != 0,
            };
            if (rejected) return (code, true);
        }
        return (seen, false);
    }

    private async Task<(JsonDocument Document, int Status)> GetCoreAsync(string url, CancellationToken token, string? referer, string? jsonBody,
        DropSpace.Core.Models.LyricsProviderKind provider, LyricsDiagnosticStage stage)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !Hosts.Contains(uri.Host) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("Untrusted lyrics host.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await SendAsync(uri, referer, timeout.Token, jsonBody).ConfigureAwait(false);
        var retryAfter = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date ? date - _clock.GetUtcNow() : (TimeSpan?)null);
        if (!token.IsCancellationRequested && response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            _health.RateLimited(provider, stage, retryAfter);
        else if (!token.IsCancellationRequested && (int)response.StatusCode >= 500 && retryAfter is not null)
            _health.DeferTransient(provider, stage, retryAfter.Value);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != "https" || !finalUri.IsDefaultPort || !Hosts.Contains(finalUri.Host)) throw new InvalidDataException("Untrusted lyrics response.");
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes) throw new InvalidDataException("Lyrics response exceeds limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("Lyrics response exceeds limit.");
            buffer.Write(chunk, 0, count);
        }
        timeout.Token.ThrowIfCancellationRequested();
        var bytes = buffer.GetBuffer();
        var length = checked((int)buffer.Length);
        var encoding = Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { }
        }
        var json = encoding.GetString(bytes, 0, length).Trim().TrimStart('\uFEFF');
        // QQ may return a named JSONP envelope even when JSON was requested.
        if (!json.StartsWith('{') && !json.StartsWith('['))
        {
            var begin = json.IndexOf('(');
            var end = json.LastIndexOf(')');
            if (begin < 0 || end <= begin) throw new InvalidDataException("Invalid lyrics response.");
            json = json[(begin + 1)..end];
        }
        timeout.Token.ThrowIfCancellationRequested();
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (timeout.IsCancellationRequested)
        {
            document.Dispose();
            timeout.Token.ThrowIfCancellationRequested();
        }
        return (document, (int)response.StatusCode);
    }

    // The production transport disables automatic redirects. Follow only bounded,
    // same-origin HTTPS redirects so metadata cannot be forwarded to another service.
    private async Task<HttpResponseMessage> SendAsync(Uri uri, string? referer, CancellationToken token, string? jsonBody)
    {
        for (var redirects = 0; ; redirects++)
        {
            var isQq = uri.Host is "u.y.qq.com" or "c.y.qq.com";
            if (isQq) qqMusicSession?.EnsureUsable();
            using var request = new HttpRequestMessage(jsonBody is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var sessionGeneration = qqMusicSession?.Generation ?? 0;
            var cookieHeader = qqMusicSession?.CookieHeader(uri);
            if (!string.IsNullOrEmpty(cookieHeader)) request.Headers.Add("Cookie", cookieHeader);
            request.Headers.UserAgent.ParseAdd("DropSpace/0.3 (+https://github.com/airanluo-dot/DropSpace)");
            if (referer is not null) request.Headers.Referrer = new(referer);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (isQq && response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                qqMusicSession?.ReportAccess(sessionGeneration, false, 429);
            if (!string.IsNullOrEmpty(cookieHeader) && qqMusicSession is not null)
            {
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
                    qqMusicSession.ReportAccess(sessionGeneration, false, (int)response.StatusCode);
                if (response.Headers.TryGetValues("Set-Cookie", out var updates))
                {
                    try { await qqMusicSession.ApplyResponseCookiesAsync(uri, updates, sessionGeneration, token).ConfigureAwait(false); }
                    catch { response.Dispose(); throw; }
                }
            }
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (jsonBody is not null) throw new InvalidDataException("Unexpected lyrics POST redirect.");
            if (redirects >= 3 || location is null || !Uri.TryCreate(uri, location, out var next) ||
                next.Scheme != "https" || !next.IsDefaultPort || !string.Equals(uri.Host, next.Host, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(next.UserInfo))
                throw new InvalidDataException("Untrusted or excessive lyrics redirect.");
            uri = next;
        }
    }

    public static string Escape(string text) => Uri.EscapeDataString(string.Concat(text.EnumerateRunes().Take(2_048).Select(rune => rune.ToString())));
    public static string Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : string.Empty : string.Empty;
    public static double Number(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out number) && double.IsFinite(number)) return number;
        return 0;
    }
    public static IEnumerable<JsonElement> Array(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return [];
        }
        return element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Take(100).ToArray() : [];
    }
    public static string NestedText(JsonElement element, string parent, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(parent, out var value) && value.ValueKind == JsonValueKind.Object ? Text(value, property) : string.Empty;
}
