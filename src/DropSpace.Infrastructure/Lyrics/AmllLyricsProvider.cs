using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class AmllLyricsProvider(LyricsHttpClient http) : ILyricsProvider
{
    public LyricsProviderKind Kind => LyricsProviderKind.Amll;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        // Player and catalogue credits are not guaranteed to use the same artist/album
        // semantics. Search broadly by normalized title, then apply the shared multi-signal
        // identity matcher to the bounded result set.
        var searchUrl = $"https://api.amll.dev/v1/lyrics/search?musicName={Escape(LyricsMatcher.SearchTitle(query.Title))}&pageSize=100";
        using var search = await http.GetAsync(searchUrl, cancellationToken);
        var best = Array(search.RootElement, "data", "items")
            .Select(item => new
            {
                Item = item,
                Titles = Array(item, "musicNames")
                    .Where(name => name.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(name => name.GetString() ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToArray(),
                Artist = string.Join("; ", Array(item, "artistNames")
                    .Where(name => name.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(name => name.GetString() ?? string.Empty)),
                Album = string.Join("; ", Array(item, "albumNames")
                    .Where(name => name.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(name => name.GetString() ?? string.Empty)),
                Duration = Number(item, "duration"),
                Id = Text(item, "id"),
            })
            .Select(candidate =>
            {
                var title = candidate.Titles
                    .Select(value => (Title: value, Score: LyricsMatcher.Score(
                        query, value, candidate.Artist, candidate.Album, candidate.Duration)))
                    .OrderByDescending(value => value.Score)
                    .FirstOrDefault();
                return new
                {
                    candidate.Item,
                    Title = title.Title,
                    candidate.Artist,
                    candidate.Album,
                    candidate.Duration,
                    candidate.Id,
                    Score = title.Score,
                };
            })
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        if (best is null || best.Score < 4 || string.IsNullOrWhiteSpace(best.Id) || string.IsNullOrWhiteSpace(best.Title))
        {
            return LyricsDocument.Empty;
        }

        using var lyric = await http.GetAsync(
            $"https://api.amll.dev/v1/lyrics/get?id={Escape(best.Id)}",
            cancellationToken);
        return LyricsParser.Parse(NestedText(lyric.RootElement, "data", "lyrics"), Kind)
            .Bind(query, best.Title, best.Artist, best.Album, best.Duration, best.Score, best.Id);
    }
}
