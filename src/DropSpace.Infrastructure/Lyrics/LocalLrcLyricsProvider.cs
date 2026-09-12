using System.Text.RegularExpressions;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LocalLrcLyricsProvider(Func<string> getDirectory) : ILyricsProvider
{
    private const int MaximumFiles = 512;
    private static readonly Regex Metadata = new(@"\[(ti|ar|al):([^\]\r\n]*)\]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    public LyricsProviderKind Kind => LyricsProviderKind.LocalLrc;

    public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var directory = getDirectory();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return LyricsDocument.Empty;
        string? bestText = null;
        double bestScore = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.lrc", new EnumerationOptions
        { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(MaximumFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous);
                if (stream.Length > LyricsParser.MaximumCharacters) continue;
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var characters = new char[checked((int)stream.Length + 1)];
                var count = await reader.ReadBlockAsync(characters.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count > LyricsParser.MaximumCharacters || count == characters.Length) continue;
                var text = new string(characters, 0, count);
                var title = Path.GetFileNameWithoutExtension(path);
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
                var score = Math.Max(LyricsMatcher.Score(query, title, artist, album, 0),
                    LyricsMatcher.Score(query, Path.GetFileNameWithoutExtension(path), query.Artist, string.Empty, 0) - 1);
                if (score > bestScore) { bestScore = score; bestText = text; }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return bestText is null || bestScore < 4 ? LyricsDocument.Empty : LyricsParser.Parse(bestText, Kind);
    }, cancellationToken);
}
