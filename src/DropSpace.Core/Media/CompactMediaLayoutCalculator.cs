namespace DropSpace.Core.Media;

public readonly record struct IslandRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

public sealed record CompactMediaLayoutInput
{
    public double Scale { get; init; } = 1;
    public double BaseWidth { get; init; } = 340;
    public double BaseHeight { get; init; } = 64;
    public double MinimumWidth { get; init; } = 260;
    public double MaximumWidth { get; init; } = 560;
    public bool ShowArtwork { get; init; } = true;
    public bool ShowLyrics { get; init; } = true;
    public bool ShowSecondaryLyrics { get; init; }
    public bool ShowSpectrum { get; init; } = true;
    public bool DynamicWidth { get; init; } = true;
    public double MeasuredPrimaryTextWidth { get; init; }
    public double MeasuredSecondaryTextWidth { get; init; }
}

public sealed record CompactMediaLayout(
    double Width,
    double Height,
    IslandRect Artwork,
    IslandRect Lyrics,
    IslandRect Spectrum,
    bool ArtworkVisible,
    bool LyricsVisible,
    bool SpectrumVisible);

/// <summary>
/// Pure compact-media geometry. WinUI measures text and feeds the measurements back here;
/// no HWND or visual state is needed to calculate a safe target frame.
/// </summary>
public static class CompactMediaLayoutCalculator
{
    private const double ArtworkSize = 34;
    private const double LeftInset = 10;
    private const double TextGap = 12;
    private const double SpectrumWidth = 30;
    private const double SpectrumGap = 12;
    private const double RightInset = 17;
    private const double MinimumTextWidth = 72;

    public static CompactMediaLayout Calculate(CompactMediaLayoutInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var scale = NormalizeScale(input.Scale);
        var height = Math.Max(48, input.BaseHeight * scale);
        var minimumWidth = Math.Max(160, input.MinimumWidth * scale);
        var maximumWidth = Math.Max(minimumWidth, input.MaximumWidth * scale);
        var baseWidth = Math.Clamp(input.BaseWidth * scale, minimumWidth, maximumWidth);

        var artworkVisible = input.ShowArtwork;
        var lyricsVisible = input.ShowLyrics;
        var spectrumVisible = input.ShowSpectrum;
        var artworkWidth = artworkVisible ? ArtworkSize * scale : 0;
        var textWidth = Math.Max(
            MinimumTextWidth * scale,
            Math.Max(
                NormalizeMeasuredWidth(input.MeasuredPrimaryTextWidth, scale),
                input.ShowSecondaryLyrics
                    ? NormalizeMeasuredWidth(input.MeasuredSecondaryTextWidth, scale)
                    : 0));
        var textSectionWidth = lyricsVisible ? textWidth : 0;
        var chrome = LeftInset * scale + artworkWidth + (artworkVisible ? TextGap * scale : 0);
        if (lyricsVisible && artworkVisible)
        {
            chrome += 0;
        }

        if (spectrumVisible)
        {
            chrome += SpectrumWidth * scale + SpectrumGap * scale + RightInset * scale;
        }
        else
        {
            chrome += RightInset * scale;
        }

        var naturalWidth = chrome + textSectionWidth;
        var width = input.DynamicWidth
            ? Math.Clamp(Math.Max(baseWidth, naturalWidth), minimumWidth, maximumWidth)
            : baseWidth;

        var artwork = artworkVisible
            ? new IslandRect(LeftInset * scale, (height - ArtworkSize * scale) / 2, artworkWidth, artworkWidth)
            : new IslandRect(0, 0, 0, 0);
        var lyricX = artworkVisible ? artwork.Right + TextGap * scale : LeftInset * scale;
        var lyricRight = spectrumVisible
            ? width - (SpectrumWidth + SpectrumGap + RightInset) * scale
            : width - RightInset * scale;
        var lyrics = lyricsVisible
            ? new IslandRect(
                lyricX,
                0,
                Math.Max(0, lyricRight - lyricX),
                height)
            : new IslandRect(0, 0, 0, 0);
        var spectrum = spectrumVisible
            ? new IslandRect(
                width - (SpectrumWidth + RightInset) * scale,
                (height - 28 * scale) / 2,
                SpectrumWidth * scale,
                28 * scale)
            : new IslandRect(0, 0, 0, 0);

        return new CompactMediaLayout(
            width,
            height,
            artwork,
            lyrics,
            spectrum,
            artworkVisible,
            lyricsVisible,
            spectrumVisible);
    }

    private static double NormalizeMeasuredWidth(double width, double scale) =>
        double.IsFinite(width) && width > 0 ? width + 4 * scale : 0;

    private static double NormalizeScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? Math.Clamp(scale, 0.5, 2) : 1;
}
