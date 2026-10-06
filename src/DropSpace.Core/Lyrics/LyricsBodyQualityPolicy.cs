namespace DropSpace.Core.Lyrics;

public enum LyricsBodyQuality { Usable, PlaceholderOnly, NoLyrics, ConfirmedInstrumental, RequestFailed }

/// <summary>Whole-line service placeholders are not proof that a recording is instrumental.</summary>
public static class LyricsBodyQualityPolicy
{
    public const string Version = "body-quality-v1";
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "纯音乐，请欣赏", "纯音乐,请欣赏", "纯音乐请欣赏", "純音樂，請欣賞", "純音樂請欣賞",
        "此歌曲为没有填词的纯音乐，请您欣赏", "此歌曲为没有填词的纯音乐,请您欣赏",
        "此歌曲为没有填词的纯音乐", "暂无歌词", "暫無歌詞", "没有歌词", "純音樂", "纯音乐",
        "instrumental", "no lyrics", "no lyrics available", "lyrics not available", "lyrics unavailable",
    };
    private static bool IsPlaceholder(string text) => Placeholders.Contains(text.Trim().Trim('[', ']', '(', ')', '【', '】', '。', '.', '!', '！').Trim());
    public static LyricsBodyQuality Classify(LyricsDocument document, bool confirmedInstrumental = false)
    {
        var body = document.Lines.SelectMany(line => line.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(text => !LyricsLanguagePolicy.IsCredit(text)).ToArray();
        if (body.Length == 0) return confirmedInstrumental ? LyricsBodyQuality.ConfirmedInstrumental : LyricsBodyQuality.NoLyrics;
        return body.All(IsPlaceholder) ? LyricsBodyQuality.PlaceholderOnly : LyricsBodyQuality.Usable;
    }
    public static LyricsDocument Normalize(LyricsDocument document)
    {
        var quality = document.Lines.Count == 0 && document.BodyQuality != LyricsBodyQuality.Usable
            ? document.BodyQuality : Classify(document);
        return document with { Lines = quality == LyricsBodyQuality.Usable ? document.Lines : [], BodyQuality = quality };
    }
}
