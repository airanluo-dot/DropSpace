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
        // Let the artwork clear the reading area before revealing the lyrics;
        // fade departing lyrics before the cover crosses that area again.
        var lyricAmount = from.LyricsOpacity < to.LyricsOpacity
            ? Math.Clamp((amount - .55) / .45, 0, 1)
            : Math.Clamp(amount / .6, 0, 1);
        lyricAmount = lyricAmount * lyricAmount * (3 - 2 * lyricAmount);
        return new(RectMix(from.Cover, to.Cover, amount), RectMix(from.Metadata, to.Metadata, amount),
            RectMix(from.Controls, to.Controls, amount), RectMix(from.Lyrics, to.Lyrics, amount),
            RectMix(from.Translation, to.Translation, amount), RectMix(from.Footer, to.Footer, amount),
            Mix(from.LyricsOpacity, to.LyricsOpacity, lyricAmount), Mix(from.ControlsOpacity, to.ControlsOpacity, amount),
            Mix(from.TitleSize, to.TitleSize, amount), Mix(from.ArtistSize, to.ArtistSize, amount),
            Mix(from.CoverRadius, to.CoverRadius, amount), Mix(from.PlaybackMix, to.PlaybackMix, amount),
            Mix(from.CoverScale, to.CoverScale, amount));
    }
}

internal static class MusicWindowLayout
{
    internal const double ActionSize = 44;
    internal const double StatusHeight = 20;
    internal const double SeekHeight = 48;
    internal const double PlaybackHeight = 58;
    internal const double VolumeHeight = 40;
    internal const double MinimumBodyWidth = 320;
    internal const double MinimumBodyHeight = 360;

    internal static bool IsPortrait(double width, double height) => width < 660 || width < height * 1.22;

    internal static MusicWindowFrame Create(double width, double height, bool portrait,
        bool lyrics, bool controls, bool playing, bool hasStatus)
    {
        // On ultrawide displays keep the player and the lyrics within reading distance.
        var spread = portrait ? width : Math.Min(width, height * 2.5);
        var inset = Math.Clamp(Math.Min(spread, height) * .048, 16, 64);
        var left = (width - spread) / 2 + inset;
        var inner = spread - 2 * inset;
        var gap = Math.Clamp(height * .022, 8, 24);
        var titleSize = Math.Clamp(height * .036, 20, 28);
        var artistSize = Math.Clamp(height * .025, 15, 19);
        var metadataHeight = Math.Ceiling((titleSize + artistSize) * 1.4 + 3);

        // The three spacer rows share this space 1:1:2. The four control rows keep
        // their native hit targets even when the user flattens the window.
        var spacerHeight = Math.Clamp((height - 400) * .08, 0, 32);
        var actionsTop = SeekHeight + PlaybackHeight + VolumeHeight + spacerHeight;
        var actionBottom = actionsTop + ActionSize;
        var controlsHeight = actionBottom + StatusHeight;
        var visibleHeight = hasStatus ? controlsHeight : actionBottom;
        var bottomY = height - inset - visibleHeight;

        Rect cover, metadata, transport, lyricBounds;
        if (portrait && lyrics)
        {
            var side = Math.Clamp(inner * .16, 48, 76);
            var textGap = Math.Clamp(inset * .6, 12, 22);
            titleSize = 21;
            artistSize = 16;
            const double headerTextHeight = 55;
            var headerHeight = Math.Max(side, headerTextHeight);
            cover = new(left, inset + (headerHeight - side) / 2, side, side);
            metadata = new(cover.Right + textGap, inset + (headerHeight - headerTextHeight) / 2,
                inner - side - textGap, headerTextHeight);
            var lyricsY = inset + headerHeight + gap;
            var lyricsBottom = controls ? bottomY - gap : height - inset;
            lyricBounds = new(left, lyricsY, inner, lyricsBottom - lyricsY);
            transport = new(left, controls ? bottomY : height + gap, inner, controlsHeight);
        }
        else
        {
            var split = !portrait && lyrics;
            var columnWidth = split ? spread * .40 : Math.Min(inner, height * .78);
            var columnX = split ? left : (width - columnWidth) / 2;
            var artworkRoom = bottomY - metadataHeight - 2 * gap - inset;
            var side = Math.Min(columnWidth, artworkRoom);
            // In split view the controls stay anchored while the artwork can grow
            // vertically. Without lyrics the complete player is centered as one unit.
            var extra = artworkRoom - side;
            var coverY = inset + extra / 2;
            var metadataY = split || portrait ? inset + artworkRoom + gap : coverY + side + gap;
            cover = new(columnX + (columnWidth - side) / 2, coverY, side, side);
            metadata = new(columnX, metadataY, columnWidth, metadataHeight);
            transport = new(columnX, metadata.Bottom + gap, columnWidth, controlsHeight);

            var lyricsX = portrait ? left : left + spread * .40 + Math.Clamp(spread * .035, 24, 72);
            lyricBounds = new(lyricsX, inset, left + inner - lyricsX, height - 2 * inset);
        }

        var footer = new Rect(transport.X + transport.Width * .25 - ActionSize / 2, transport.Y + actionsTop, ActionSize, ActionSize);
        var translation = new Rect(transport.X + transport.Width * .75 - ActionSize / 2, transport.Y + actionsTop, ActionSize, ActionSize);
        return new(cover, metadata, transport, lyricBounds, translation, footer,
            lyrics ? 1 : 0, controls ? 1 : 0, titleSize, artistSize,
            portrait && lyrics ? 9 : 12, playing ? 1 : 0, portrait && lyrics || playing ? 1 : .74);
    }
}
