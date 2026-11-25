using System.Text.Json;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class MusicWindowCheck
{
    private static async Task VerifyInterludeAsync(MediaViewModel media, MusicWindowService music,
        Action<bool, string, string, object?> check)
    {
        static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
        var document = new LyricsDocument([
            new(At(0), At(2), "First", null, []),
            new(At(2.8), At(4), "Second", null, []),
            new(At(10), At(13), "Last", "最后", [new("Last", At(10), At(13))]) { TranslationLanguage = "zh-CN" }
        ], LyricsProviderKind.LocalLrc);
        media.Settings = media.Settings with { Lyrics = media.Settings.Lyrics with { DelayMilliseconds = 500, ShowMusicWindowNextLyric = true } };
        media.SetLyricsDocument(document);
        media.LyricsStatus = LyricsQueryStatus.Found;
        var timeline = new LyricsTimelineEngine();
        void Position(double seconds)
        {
            media.Position = media.Session.Timeline.Start + At(seconds);
            media.Lyrics = timeline.GetFrame(document, At(seconds), media.Settings.Lyrics.DelayMilliseconds);
        }

        var compact = new MediaCompactView { ViewModel = media };
        var expanded = new ExpandedIslandMusicView { ViewModel = media };
        var stack = new StackPanel();
        stack.Children.Add(compact);
        stack.Children.Add(expanded);
        var islandHost = new Window { Content = stack };
        try
        {
            Position(6.6); // 7.1s after delay, already inside the interlude when opened.
            islandHost.Activate();
            music.Open();
            var window = music.CurrentWindow!;
            var compactDots = (FrameworkElement)compact.FindName("InterludeDots");
            var expandedDots = (FrameworkElement)expanded.FindName("InterludeDots");
            async Task Observe(string transition, bool dots, int anchor, string? next)
            {
                await Task.Delay(100);
                var layout = JsonSerializer.SerializeToElement(window.LayoutSnapshot);
                var preview = next is null ? string.Empty : $"{media.NextLyricLabel} · {next}";
                var matched = compactDots.IsLoaded && expandedDots.IsLoaded &&
                    (compactDots.Visibility == Visibility.Visible) == dots &&
                    (expandedDots.Visibility == Visibility.Visible) == dots &&
                    layout.GetProperty("InterludeDisplayed").GetBoolean() == dots &&
                    layout.GetProperty("LyricAnchor").GetInt32() == anchor &&
                    layout.GetProperty("NextLyric").GetString() == preview;
                check(matched, "interlude", transition, new { dots, anchor, next, compact = compactDots.Visibility.ToString(),
                    expanded = expandedDots.Visibility.ToString(), window = layout.GetProperty("InterludeDisplayed").GetBoolean(),
                    windowAnchor = layout.GetProperty("LyricAnchor").GetInt32(), preview = layout.GetProperty("NextLyric").GetString() });
            }
            await UntilAsync(() => compactDots.IsLoaded && JsonSerializer.SerializeToElement(window.LayoutSnapshot).GetProperty("LyricRows").GetInt32() == 3);
            await Observe("Open mid-interlude", true, 1, "Last");

            // Prepare the incoming Island surface the same way the overlay does.
            // Its normalized geometry must exactly match the outgoing mid-spring frame.
            media.Spectrum = new(AudioCaptureMode.ProcessLoopback, [0, .2, .4, .6, .8, 1], 1);
            compact.RefreshForPresentation();
            compact.SetActive(false);
            expanded.RefreshForPresentation();
            var outgoing = (CapsuleSpectrumView)compact.FindName("SpectrumBars");
            var incoming = (CapsuleSpectrumView)expanded.FindName("Spectrum");
            static double[] Heights(CapsuleSpectrumView meter)
            {
                var minimum = meter.Bars[0].Height; // first target is silent
                return meter.Bars.Select(bar => (bar.Height - minimum) / (.9 * meter.Height - minimum)).ToArray();
            }
            var from = Heights(outgoing);
            var to = Heights(incoming);
            check(from.Length == 6 && from[^1] is > 0 and < 1 && from.Zip(to).All(pair => Math.Abs(pair.First - pair.Second) < 1e-8),
                "spectrum", "Compact and expanded capsules present the same in-flight spring positions", new { from, to });
            compact.SetActive(true);

            media.Session = media.Session with { PlaybackState = MediaPlaybackState.Paused };
            await Observe("Pause in interlude", true, 1, "Last");
            Position(6.49);
            await Observe("Seek back across the threshold while paused", false, 1, "Last");
            Position(6.5);
            await Observe("Reach the exact interlude threshold with lyric delay", true, 1, "Last");
            media.Session = media.Session with { PlaybackState = MediaPlaybackState.Playing };
            await Observe("Resume inside interlude", true, 1, "Last");
            Position(9.5);
            await Observe("Enter next line", false, 2, null);
            Position(10.5);
            await Observe("Advance inside a timed word", false, 2, null);
            check(window.GetState().HighlightedCharacters > 0, "interlude", "Word fill resumes after the interlude", null);
            await music.SetTranslationEnabledAsync(false);
            await Observe("Disable translation in the music window", false, 2, null);
            var translated = (TextBlock)expanded.FindName("TranslatedLyric");
            check(translated.Visibility == Visibility.Collapsed &&
                !JsonSerializer.SerializeToElement(window.LayoutSnapshot).GetProperty("ActiveTranslationDisplayed").GetBoolean(),
                "lyrics", "Both surfaces hide the current translation", null);
            await music.SetTranslationEnabledAsync(true);
            await Observe("Restore translation", false, 2, null);
            check(translated.Text == "最后" && translated.Visibility == Visibility.Visible &&
                JsonSerializer.SerializeToElement(window.LayoutSnapshot).GetProperty("ActiveTranslationDisplayed").GetBoolean(),
                "lyrics", "Both surfaces restore the current provider translation", null);
            Position(1.9);
            await Observe("Seek into a short line change", false, 0, "Second");
            media.Session = media.Session with { TrackTitle = "Different track" };
            media.LyricsStatus = LyricsQueryStatus.Loading;
            await Observe("Switch tracks before the new lyrics arrive", false, -1, null);
            media.LyricsStatus = LyricsQueryStatus.NotFound;
            await Observe("Finish track lookup without lyrics", false, -1, null);
            check(media.LyricsStatusText.Length > 0 &&
                JsonSerializer.SerializeToElement(window.LayoutSnapshot).GetProperty("EmptyLyricStatus").GetString() == media.LyricsStatusText,
                "lyrics", "The empty window presents the shared lookup status", null);
        }
        finally { islandHost.Close(); }
    }
}
