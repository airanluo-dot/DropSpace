using System.Text.RegularExpressions;
using System.Xml;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LocalLrcLyricsProvider(Func<string> getDirectory) : ILyricsProvider
{
    private const int MaximumFiles = 512;
    private const int MaximumEnumeratedFiles = 8_192;
    private static readonly Regex Metadata = new(@"\[(ti|ar|al):([^\]\r\n]*)\]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    public LyricsProviderKind Kind => LyricsProviderKind.LocalLrc;

    public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var directory = getDirectory();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return LyricsDocument.Empty;
        LyricsDocument? best = null;
        double bestScore = 0;
        var ambiguous = false;
        var paths = CandidatePaths(directory, query).ToArray();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous);
                if (stream.Length > LyricsParser.MaximumCharacters) continue;
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var builder = new System.Text.StringBuilder();
                var characters = new char[16 * 1024];
                int count;
                while ((count = await reader.ReadAsync(characters.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    builder.Append(characters, 0, count);
                    if (builder.Length > LyricsParser.MaximumCharacters) { builder.Clear(); break; }
                }
                if (builder.Length == 0) continue;
                var text = builder.ToString();
                var fileNameTitle = Path.GetFileNameWithoutExtension(path);
                var title = string.Empty;
                var artist = string.Empty;
                var album = string.Empty;
                foreach (Match match in Metadata.Matches(text))
                {
                    switch (match.Groups[1].Value.ToLowerInvariant())
                    {
                        case "ti": title = match.Groups[2].Value; break;
                        case "ar": artist = match.Groups[2].Value; break;
                        case "al": album = match.Groups[2].Value; break;
                    }
                }
                if (TrySplitFileName(fileNameTitle, out var fileArtist, out var fileTitle))
                {
                    if (string.IsNullOrWhiteSpace(artist)) artist = fileArtist;
                    if (string.IsNullOrWhiteSpace(title)) title = fileTitle;
                }
                if (string.IsNullOrWhiteSpace(title)) title = fileNameTitle;
                var score = LyricsMatcher.Score(query, title, artist, album, 0);
                if (score < bestScore || score < 4) continue;
                try
                {
                    var parsed = LyricsParser.Parse(text, Kind);
                    if (parsed.Lines.Count == 0 || LyricsMatcher.OriginalLanguageConflicts(query, parsed)) continue;
                    var document = parsed.Bind(query, title, artist, album, 0, score, path);
                    if (score == bestScore && best?.Match is { } leader)
                    {
                        if (LyricsMatcher.ConflictingCandidates(query, leader, document.Match!)) ambiguous = true;
                        continue;
                    }
                    ambiguous = false;
                    bestScore = score;
                    best = document;
                }
                catch (Exception exception) when (exception is InvalidDataException or XmlException or FormatException) { }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return ambiguous ? new LyricsDocument([], Kind) { BodyQuality = LyricsBodyQuality.Ambiguous } : best ?? LyricsDocument.Empty;
    }, cancellationToken);

    private static IEnumerable<string> CandidatePaths(string directory, LyricsQuery query)
    {
        var exactTitle = LyricsMatcher.Normalize(query.Title);
        var exact = SafeEnumerate(directory).Where(path => LyricsMatcher.Normalize(Path.GetFileNameWithoutExtension(path)) == exactTitle)
            .Take(MaximumFiles).ToArray();
        var seen = exact.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return exact.Concat(SafeEnumerate(directory).Where(path => !seen.Contains(path)).Take(Math.Max(0, MaximumFiles - exact.Length)));
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.lrc", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Take(MaximumEnumeratedFiles).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return []; }
    }

    private static bool TrySplitFileName(string value, out string artist, out string title)
    {
        var separator = value.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0 || separator >= value.Length - 3) { artist = title = string.Empty; return false; }
        artist = value[..separator].Trim(); title = value[(separator + 3)..].Trim();
        return artist.Length > 0 && title.Length > 0;
    }
}
