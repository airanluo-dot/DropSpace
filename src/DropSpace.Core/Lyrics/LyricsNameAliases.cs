using System.Collections.Frozen;
using System.Text.Json;

namespace DropSpace.Core.Lyrics;

// A small pinned catalogue of names, not a translation service or fuzzy identity rule.
// Search expansion never mutates the original media query or provider metadata.
internal static class LyricsNameAliases
{
    private static readonly Lazy<Catalogue> Names = new(Load);
    private sealed record Song(string Title, bool CanConfirmRecording);
    private sealed record Artist(string Name, FrozenDictionary<string, Song> Songs);
    private sealed record Catalogue(FrozenDictionary<string, Artist> Artists);

    internal static string CanonicalArtist(string credit)
    {
        var key = ArtistCreditOrthography.Fold(credit);
        return Names.Value.Artists.TryGetValue(key, out var artist)
            ? ArtistCreditOrthography.Fold(artist.Name) : key;
    }

    internal static string? SearchTerm(string title, string completeArtist)
    {
        if (!Names.Value.Artists.TryGetValue(ArtistCreditOrthography.Fold(completeArtist), out var artist)) return null;
        var searchTitle = LyricsMatcher.SearchTitle(title);
        if (artist.Songs.TryGetValue(ArtistCreditOrthography.Fold(searchTitle), out var song))
            searchTitle = LyricsMatcher.SearchTitle(song.Title);
        var expanded = $"{searchTitle} {artist.Name}".Trim();
        return string.Equals(expanded, $"{LyricsMatcher.SearchTitle(title)} {completeArtist}".Trim(),
            StringComparison.OrdinalIgnoreCase) ? null : expanded;
    }

    // The caller must independently verify complete artist credits, duration,
    // and both directions of language/version compatibility before using this.
    internal static bool AreRecordingTitlesEquivalent(string left, string right, string completeArtist)
    {
        if (!Names.Value.Artists.TryGetValue(ArtistCreditOrthography.Fold(completeArtist), out var artist)) return false;
        return artist.Songs.TryGetValue(TitleKey(left), out var a) && a.CanConfirmRecording &&
            artist.Songs.TryGetValue(TitleKey(right), out var b) && b.CanConfirmRecording &&
            TitleKey(a.Title) == TitleKey(b.Title);
    }

    private static string TitleKey(string title) => ArtistCreditOrthography.Fold(LyricsMatcher.SearchTitle(title));

    private static Catalogue Load()
    {
        using var stream = typeof(LyricsNameAliases).Assembly.GetManifestResourceStream(
            "DropSpace.Core.Lyrics.Data.RecordingNameAliases.json") ??
            throw new InvalidOperationException("Missing pinned recording name aliases.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 5 });
        if (document.RootElement.GetProperty("ver").GetInt32() != 1)
            throw new InvalidDataException("Unsupported recording name aliases version.");
        var artists = new Dictionary<string, Artist>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.GetProperty("singers").EnumerateObject().Take(16))
        {
            var name = entry.Value.GetProperty("name").GetString() ?? string.Empty;
            if (entry.Name.Length is 0 or > 256 || name.Length is 0 or > 256)
                throw new InvalidDataException("Invalid recording artist alias.");
            var songs = new Dictionary<string, Song>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in entry.Value.GetProperty("songs").EnumerateObject().Take(256))
            {
                var title = item.Value.GetString() ?? string.Empty;
                if (item.Name.Length is 0 or > 256 || title.Length is 0 or > 256)
                    throw new InvalidDataException("Invalid recording title alias.");
                // A display-name map does not establish equivalence of guest names.
                // Keep these rows useful for lookup without dropping collaboration evidence.
                var song = new Song(title, !LyricsMatcher.HasFeaturedArtistCredit(item.Name) &&
                    !LyricsMatcher.HasFeaturedArtistCredit(title));
                foreach (var key in new[] { TitleKey(item.Name), TitleKey(title) })
                {
                    if (songs.TryGetValue(key, out var existing) && existing != song) ambiguous.Add(key);
                    else songs[key] = song;
                }
            }
            foreach (var key in ambiguous) songs.Remove(key);
            var artist = new Artist(name, songs.ToFrozenDictionary(StringComparer.Ordinal));
            artists.Add(ArtistCreditOrthography.Fold(entry.Name), artist);
            artists.TryAdd(ArtistCreditOrthography.Fold(name), artist);
        }
        return new(artists.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
