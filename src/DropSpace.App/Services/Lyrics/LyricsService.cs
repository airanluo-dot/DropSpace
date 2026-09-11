using System.Net;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Lyrics;

public sealed class LyricsService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LyricsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LyricsService(ILogger<LyricsService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DropSpace/0.3");
    }

    public async Task<LyricsResult> QueryAsync(
        LyricsQuery query,
        LyricsMode mode,
        LyricsProviderKind provider,
        string? localDirectory,
        CancellationToken cancellationToken = default)
    {
        if (mode == LyricsMode.LocalLrc)
        {
            return QueryLocal(query, localDirectory);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var endpoint = provider switch
            {
                LyricsProviderKind.Lrclib => "https://lrclib.net/api/get?track_name={0}&artist_name={1}",
                LyricsProviderKind.Amll => "https://api.amll.dev/lyrics/search?title={0}&artist={1}",
                LyricsProviderKind.NetEase => "https://music.163.com/api/search/get/web?csrf_token=&s={0}&type=1&offset=0&total=true&limit=1",
                LyricsProviderKind.QqMusic => "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=1&w={0}",
                LyricsProviderKind.Kugou => "https://mobileservice.kugou.com/api/v3/search/song?pagesize=1&page=1&keyword={0}",
                _ => string.Empty,
            };
            if (string.IsNullOrWhiteSpace(endpoint)) return LyricsResult.NotFound("Provider is unavailable.");
            var uri = string.Format(endpoint, Uri.EscapeDataString(query.NormalizedTitle), Uri.EscapeDataString(query.NormalizedArtist));
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return LyricsResult.NotFound();
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var text = provider == LyricsProviderKind.Lrclib
                ? ExtractProperty(body, "syncedLyrics") ?? ExtractProperty(body, "plainLyrics")
                : ExtractLyricsString(body);
            if (string.IsNullOrWhiteSpace(text)) return LyricsResult.NotFound();
            var lines = LyricsParser.Parse(text);
            return lines.Count == 0
                ? LyricsResult.NotFound("Provider returned no supported LRC lines.")
                : new LyricsResult(true, new LyricsDocument(lines, provider.ToString(), DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(exception, "Lyrics provider {Provider} failed safely.", provider);
            return LyricsResult.NotFound($"{provider} is unavailable.");
        }
        finally { _gate.Release(); }
    }

    private static LyricsResult QueryLocal(LyricsQuery query, string? localDirectory)
    {
        if (string.IsNullOrWhiteSpace(localDirectory) || !Directory.Exists(localDirectory)) return LyricsResult.NotFound("Local LRC directory is unavailable.");
        var title = LyricsParser.NormalizeQueryPart(query.Title);
        var candidates = Directory.EnumerateFiles(localDirectory, "*.lrc", SearchOption.TopDirectoryOnly)
            .Take(2048)
            .OrderBy(path => Path.GetFileNameWithoutExtension(path).Contains(title, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        foreach (var path in candidates)
        {
            try
            {
                var lines = LyricsParser.Parse(File.ReadAllText(path));
                if (lines.Count > 0) return new LyricsResult(true, new LyricsDocument(lines, LyricsProviderKind.LocalLrc.ToString(), DateTimeOffset.UtcNow));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return LyricsResult.NotFound();
    }

    private static string? ExtractProperty(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return FindString(document.RootElement, property);
    }

    private static string? ExtractLyricsString(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var name in new[] { "syncedLyrics", "lyrics", "lyric", "lrc", "content" })
        {
            if (FindString(document.RootElement, name) is { Length: > 0 } value) return value;
        }

        return null;
    }

    private static string? FindString(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in element.EnumerateObject())
            {
                if (string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase) && item.Value.ValueKind == JsonValueKind.String) return item.Value.GetString();
                if (FindString(item.Value, property) is { } nested) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindString(item, property) is { } nested) return nested;
            }
        }

        return null;
    }
}
