namespace DropSpace.Core.Lyrics;

public enum LyricsGlowMode
{
    Off,
    AiLyrics,
    Music,
}

public enum LyricsTranslationOrigin
{
    None,
    Provider,
    LocalAi,
}

/// <summary>Eligibility only; the renderer must animate to this target without resetting its phase.</summary>
public static class LyricsGlowPolicy
{
    public static bool IsEligible(LyricsGlowMode mode, bool isPlaying, bool isIslandVisible,
        bool isTranslationActuallyVisible, LyricsTranslationOrigin origin, string? translation)
    {
        if (!isPlaying || !isIslandVisible) return false;
        return mode switch
        {
            LyricsGlowMode.Music => true,
            LyricsGlowMode.AiLyrics => isTranslationActuallyVisible &&
                origin == LyricsTranslationOrigin.LocalAi && !string.IsNullOrWhiteSpace(translation),
            _ => false,
        };
    }

    public static LyricsGlowMode OnAiEnabledChanged(bool enabled) =>
        enabled ? LyricsGlowMode.AiLyrics : LyricsGlowMode.Off;
}
