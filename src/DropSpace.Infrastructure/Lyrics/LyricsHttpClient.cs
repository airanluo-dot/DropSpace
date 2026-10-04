using System.Text;
using System.Text.Json;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LyricsHttpClient(HttpClient client, Action<LyricsDiagnostic>? diagnostic = null)
{
    private const int MaximumResponseBytes = 3 * 1024 * 1024;
    internal void ReportNetEaseReuse() => LyricsDiagnostics.Report(diagnostic,
        new(DropSpace.Core.Models.LyricsProviderKind.NetEase, LyricsDiagnosticStage.Reuse, LyricsDiagnosticOutcome.Found, 0));
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    { "music.163.com", "c.y.qq.com", "lyrics.kugou.com", "songsearch.kugou.com", "lrclib.net", "api.amll.dev" };

    public async Task<JsonDocument> GetAsync(string url, CancellationToken token, string? referer = null)
    {
        var uri = new Uri(url);
        var provider = uri.Host.ToLowerInvariant() switch
        {
            "music.163.com" => DropSpace.Core.Models.LyricsProviderKind.NetEase,
            "c.y.qq.com" => DropSpace.Core.Models.LyricsProviderKind.QqMusic,
            "lyrics.kugou.com" or "songsearch.kugou.com" => DropSpace.Core.Models.LyricsProviderKind.Kugou,
            "lrclib.net" => DropSpace.Core.Models.LyricsProviderKind.Lrclib,
            _ => DropSpace.Core.Models.LyricsProviderKind.Amll,
        };
        var stage = uri.AbsolutePath.Contains("search", StringComparison.OrdinalIgnoreCase)
            ? LyricsDiagnosticStage.Search : LyricsDiagnosticStage.Lyric;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var (document, status) = await GetCoreAsync(url, token, referer).ConfigureAwait(false);
            var code = Number(document.RootElement, "code");
            var rejected = provider == DropSpace.Core.Models.LyricsProviderKind.NetEase && code > 0 && code != 200;
            LyricsDiagnostics.Report(diagnostic, new(provider, stage,
                code == 405 && rejected ? LyricsDiagnosticOutcome.RateLimited :
                rejected ? LyricsDiagnosticOutcome.Rejected : LyricsDiagnosticOutcome.Found,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                HttpStatus: status, ApiCode: document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("code", out _) && code >= int.MinValue && code <= int.MaxValue ? (int)code : null));
            return document;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LyricsDiagnostics.Report(diagnostic, new(provider, stage,
                LyricsDiagnostics.Classify(error, token.IsCancellationRequested),
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                HttpStatus: error is HttpRequestException httpError ? (int?)httpError.StatusCode : null));
            throw;
        }
    }

    private async Task<(JsonDocument Document, int Status)> GetCoreAsync(string url, CancellationToken token, string? referer)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !Hosts.Contains(uri.Host) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("Untrusted lyrics host.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await SendAsync(uri, referer, timeout.Token).ConfigureAwait(false);
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
        return (JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }), (int)response.StatusCode);
    }

    // The production transport disables automatic redirects. Follow only bounded,
    // same-origin HTTPS redirects so metadata cannot be forwarded to another service.
    private async Task<HttpResponseMessage> SendAsync(Uri uri, string? referer, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DropSpace/0.3 (+https://github.com/airanluo-dot/DropSpace)");
            if (referer is not null) request.Headers.Referrer = new(referer);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
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
