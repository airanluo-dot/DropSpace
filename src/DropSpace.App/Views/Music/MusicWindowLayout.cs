using Windows.Foundation;

namespace DropSpace.App.Views.Music;

/// <summary>Presentation geometry only. No media, timers, sessions or settings ownership.</summary>
internal sealed record MusicWindowFrame(Rect Cover, Rect Metadata, Rect Controls, Rect Lyrics,
    Rect Translation, Rect Footer, double LyricsOpacity, double ControlsOpacity,
    double TitleSize, double ArtistSize, double CoverRadius, double PlaybackMix, double CoverScale)
{
    internal static MusicWindowFrame Interpolate(MusicWindowFrame from, MusicWindowFrame to, double amount)
    {
        static double Mix(double a, double b, double t) => a + (b - a) * t;
        static Rect RectMix(Rect a, Rect b, double t) => new(Mix(a.X, b.X, t), Mix(a.Y, b.Y, t),
            Mix(a.Width, b.Width, t), Mix(a.Height, b.Height, t));
        return new(RectMix(from.Cover, to.Cover, amount), RectMix(from.Metadata, to.Metadata, amount),
            RectMix(from.Controls, to.Controls, amount), RectMix(from.Lyrics, to.Lyrics, amount),
            RectMix(from.Translation, to.Translation, amount), RectMix(from.Footer, to.Footer, amount),
            Mix(from.LyricsOpacity, to.LyricsOpacity, amount), Mix(from.ControlsOpacity, to.ControlsOpacity, amount),
            Mix(from.TitleSize, to.TitleSize, amount), Mix(from.ArtistSize, to.ArtistSize, amount),
            Mix(from.CoverRadius, to.CoverRadius, amount), Mix(from.PlaybackMix, to.PlaybackMix, amount),
            Mix(from.CoverScale, to.CoverScale, amount));
    }
}

internal static class MusicWindowLayout
{
    internal const double ControlsHeight = 256;
    internal const double ActionsTop = 176;
    internal static bool IsPortrait(double width, double height) => width < 600 || width / Math.Max(1, height) < 1;

    internal static MusicWindowFrame Create(double width, double height, bool portrait,
        bool lyrics, bool controls, bool playing)
    {
        width = Math.Max(280, width); height = Math.Max(400, height);
        var margin = portrait ? Math.Clamp(width * .065, 22, 42) : Math.Clamp(width * .045, 28, 52);
        var inner = width - margin * 2;
        const double metadataHeight = 70, bottomMargin = 20;
        Rect cover, metadata, transport, lyricBounds;
        double titleSize = 25, artistSize = 18;
        if (portrait && lyrics)
        {
            var small = Math.Clamp(inner * .17, 56, 78);
            cover = new(margin, 20, small, small);
            metadata = new(margin + small + 16, 20 + (small - 58) / 2, inner - small - 16, 58);
            titleSize = 21; artistSize = 16;
            var controlY = height - bottomMargin - ControlsHeight;
            transport = new(margin, controls ? controlY : height + 16, inner, ControlsHeight);
            lyricBounds = new(margin, 116, inner, Math.Max(40, (controls ? controlY - 16 : height - 16) - 116));
        }
        else
        {
            var splitWidth = Math.Clamp((inner - 60) / 2.08, 220, 500);
            var leftWidth = portrait ? Math.Min(inner, 520) : lyrics ? splitWidth : Math.Min(500, inner);
            leftWidth = Math.Max(220, leftWidth);
            var leftX = !portrait && lyrics ? margin : (width - leftWidth) / 2;
            var artSide = Math.Clamp(Math.Min(leftWidth, height - bottomMargin - metadataHeight - ControlsHeight - 58), 120, 500);
            var groupHeight = artSide + 22 + metadataHeight + 8 + ControlsHeight;
            var top = Math.Max(12, (height - bottomMargin - groupHeight) * .45);
            cover = new(leftX + (leftWidth - artSide) / 2, top, artSide, artSide);
            metadata = new(leftX, top + artSide + 22, leftWidth, metadataHeight);
            transport = new(leftX, metadata.Bottom + 8, leftWidth, ControlsHeight);
            if (portrait)
            {
                lyricBounds = new(margin, 140, inner, Math.Max(120, height - 450));
            }
            else
            {
                var rightX = margin + splitWidth + 60;
                lyricBounds = new(rightX + (lyrics ? 0 : 24), 20, Math.Max(220, width - margin - rightX), Math.Max(200, height - 40));
            }
        }
        // One action row follows the volume in every mode. Both buttons move and
        // fade with these controls, while retaining their original element identity.
        var footer = new Rect(transport.X + transport.Width * .25 - 22, transport.Y + ActionsTop, 44, 44);
        var translation = new Rect(transport.X + transport.Width * .75 - 22, transport.Y + ActionsTop, 44, 44);
        return new(cover, metadata, transport, lyricBounds, translation, footer,
            lyrics ? 1 : 0, controls ? 1 : 0, titleSize, artistSize,
            portrait && lyrics ? 9 : 12, playing ? 1 : 0, portrait && lyrics ? 1 : playing ? 1 : .76);
    }
}
