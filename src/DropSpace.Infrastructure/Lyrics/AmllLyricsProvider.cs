using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class AmllLyricsProvider(LyricsHttpClient http) : ILyricsProvider
{
    public LyricsProviderKind Kind => LyricsProviderKind.Amll;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        using var search = await http.GetAsync($"https://api.amll.dev/v1/lyrics/search?musicName={Escape(query.Title)}&pageSize=50", cancellationToken);
        var best = Array(search.RootElement, "data", "items").Select(item => new
        {
            Item = item,
            Score = Array(item, "musicNames").Where(name => name.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(name => LyricsMatcher.Score(query, name.GetString() ?? string.Empty, string.Join(' ', Array(item, "artistNames").Where(name => name.ValueKind == System.Text.Json.JsonValueKind.String).Select(name => name.GetString())), string.Empty, 0)).DefaultIfEmpty(0).Max(),
        }).OrderByDescending(candidate => candidate.Score).FirstOrDefault();
        if (best is null || best.Score < 4) return LyricsDocument.Empty;
        using var lyric = await http.GetAsync($"https://api.amll.dev/v1/lyrics/get?id={Escape(Text(best.Item, "id"))}", cancellationToken);
        return LyricsParser.Parse(NestedText(lyric.RootElement, "data", "lyrics"), Kind);
    }
}
