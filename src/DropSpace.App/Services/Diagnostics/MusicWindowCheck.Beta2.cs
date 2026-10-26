using System.Text.Json;
using System.Text.RegularExpressions;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class MusicWindowCheck
{
    private static async Task<PortraitResult> VerifyBeta2EdgesAsync(string output, MusicWindowService music, MediaViewModel media)
    {
        if (Directory.Exists(output)) throw new IOException("Beta2 edge checks require a fresh output directory.");
        Directory.CreateDirectory(output);
        var checks = new List<CheckResult>();
        void Check(bool passed, string description, object evidence) => checks.Add(new(passed ? "passed" : "failed", "beta2-native-edge", description, evidence));
        music.Open(); music.SetLyricsVisible(true);
        var window = music.CurrentWindow!;
        ResizeMusicDips(window, 430, 800);
        var line = new LyricsLine(TimeSpan.Zero, TimeSpan.FromSeconds(10), "春风穿过安静的窗口把歌声带到很远的地方", null,
            [new(" 春风", TimeSpan.Zero, TimeSpan.FromSeconds(2)),
             new("穿过安静的窗口", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)),
             new("把歌声带到很远的地方 ", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10))]);
        var document = new LyricsDocument([line], LyricsProviderKind.LocalLrc);
        var timeline = new LyricsTimelineEngine();
        media.SetLyricsDocument(document);
        async Task<double> At(double seconds)
        {
            media.Position = TimeSpan.FromSeconds(seconds);
            media.Lyrics = timeline.GetFrame(document, media.Position, 0);
            await Task.Delay(250);
            return window.GetState().HighlightedCharacters;
        }
        await At(0); await Task.Delay(650);
        var first = await At(.4); var second = await At(1.2);
        Check(first > 0 && second > first && second < 2, "CJK word geometry tolerates provider boundary whitespace", new { first, second });
        var wrappedFirst = await At(7.1); var wrappedSecond = await At(7.8);
        Check(wrappedSecond > wrappedFirst && wrappedSecond < line.Text.Length,
            "A timed CJK segment spanning wrapped rows continues its partial fill", new { wrappedFirst, wrappedSecond });
        window.DiagnosticFocusTransport(); window.DiagnosticNotifyInteraction();
        await Task.Delay(4_500);
        Check(window.GetState().LyricsIdle && window.GetState().ControlsVisible,
            "Resting keyboard focus permits idle lyric focus while preserving accessible controls", window.LayoutSnapshot);
        var path = Path.Combine(output, "receipt.json");
        var passed = checks.Count(check => check.Status == "passed");
        var failed = checks.Count - passed;
        var status = failed == 0 ? "passed" : "failed";
        await WriteAsync(path, new { status, syntheticMedia = true, checks });
        return new(status, path, passed, failed, null);
    }

    private static LyricsLine[] TimedFixtureLyrics(LyricsLine[] lines) => lines.Select(line =>
    {
        var words = Regex.Matches(line.Text, @"\S+\s*").Select(match => match.Value).ToArray();
        var duration = (line.End - line.Start).TotalSeconds / words.Length;
        return line with { Words = words.Select((word, i) => new LyricsWord(word,
            line.Start + TimeSpan.FromSeconds(i * duration), line.Start + TimeSpan.FromSeconds((i + 1) * duration))).ToArray() };
    }).ToArray();

    private static async Task<PortraitResult> VerifyBeta2Async(string output, MusicWindowService music,
        MediaViewModel media, LyricsLine[] lines, ServiceProvider services, OverlayWindowService overlay)
    {
        if (Directory.Exists(output)) throw new IOException("Beta2 checks require a fresh output directory.");
        Directory.CreateDirectory(output);
        var checks = new List<CheckResult>();
        var samples = new List<object>();
        void Check(bool value, string description, object? evidence = null) => checks.Add(new(value ? "passed" : "failed", "beta2-native", description, evidence));
        string? failure = null;
        try
        {
            music.Open(); music.SetLyricsVisible(true);
            var window = music.CurrentWindow!;
            ResizeMusicDips(window, 1040, 760);
            var document = new LyricsDocument(lines, LyricsProviderKind.LocalLrc);
            var timeline = new LyricsTimelineEngine();
            media.SetLyricsDocument(document);
            async Task<JsonElement> At(double seconds)
            {
                media.Position = TimeSpan.FromSeconds(seconds);
                media.Lyrics = timeline.GetFrame(document, media.Position, 0);
                await Task.Delay(180);
                var layout = JsonSerializer.SerializeToElement(window.LayoutSnapshot);
                samples.Add(new { seconds, frame = media.Lyrics, layout }); return layout;
            }
            await Task.Delay(700);
            var first = await At(44.2);
            var second = await At(44.9);
            var early = first.GetProperty("ActiveHighlightedCharacters").GetDouble();
            var later = second.GetProperty("ActiveHighlightedCharacters").GetDouble();
            Check(early > 0 && later > early && later < lines[2].Text.Length,
                "Native text geometry follows two positions within the same shared timed word without highlighting the whole line", new { early, later });
            window.DiagnosticNotifyInteraction();
            await Task.Delay(4_500);
            var idle = JsonSerializer.SerializeToElement(window.LayoutSnapshot);
            var effects = idle.GetProperty("LyricEffects").EnumerateArray().ToArray();
            Check(idle.GetProperty("LyricsIdle").GetBoolean() && effects.Length >= 3 &&
                effects.Single(e => e.GetProperty("Index").GetInt32() == 2).GetProperty("BlurAmount").GetDouble() == 0 &&
                effects.Where(e => e.GetProperty("Index").GetInt32() != 2).All(e => e.GetProperty("BlurAmount").GetDouble() > 0) &&
                idle.GetProperty("LyricEffectFailures").GetArrayLength() == 0,
                "Idle creates real compositor effect graphs; active line stays sharp and surrounding rows blur", idle);
            window.DiagnosticNotifyInteraction();
            await Task.Delay(420);
            var awake = JsonSerializer.SerializeToElement(window.LayoutSnapshot);
            Check(!awake.GetProperty("LyricsIdle").GetBoolean() && awake.GetProperty("LyricEffects").EnumerateArray().All(e => e.GetProperty("BlurAmount").GetDouble() == 0),
                "Interaction smoothly restores clear lyrics", awake);
            ResizeMusicDips(window, 430, 800);
            await Task.Delay(750);
            var wrapped = await At(52.7);
            Check(wrapped.GetProperty("Portrait").GetBoolean() && wrapped.GetProperty("ActiveHighlightedCharacters").GetDouble() > later && wrapped.GetProperty("LyricEffectFailures").GetArrayLength() == 0,
                "Portrait relayout rebuilds wrapped word geometry and keeps the shared timeline", wrapped);

            music.CurrentWindow?.SetMinimized(true);
            overlay.OpenRetrieval();
            var retrieval = services.GetRequiredService<IslandRetrievalViewModel>();
            await UntilAsync(() => retrieval.Items.Count >= 2);
            Check(retrieval.Items.Count >= 2 && retrieval.SelectedItem is null, "Opening search does not silently select a result");
            var image = retrieval.Items.First(card => card.Title == "synthetic-artwork.png");
            retrieval.SelectedItem = image; retrieval.SelectedItem = null;
            retrieval.RequestRefresh(); await Task.Delay(450);
            Check(retrieval.SelectedItem is null, "A passive refresh preserves explicit deselection");
            retrieval.SelectedItem = image;
            var island = overlay.DiagnosticActiveWindow;
            var before = island.ObserveModuleOverlay();
            await island.DiagnosticRetrievalView.DiagnosticPreviewAsync(); await Task.Delay(900);
            var preview = island.ObserveModuleOverlay();
            Check(island.DiagnosticRetrievalView.IsPreviewOpen && preview.Width > before.Width * 1.2 && preview.Height > before.Height * 1.3,
                "Image preview enlarges the actual island surface", new { before, preview });
            island.DiagnosticRetrievalView.ResetPreview(); await Task.Delay(900);
            var after = island.ObserveModuleOverlay();
            Check(Math.Abs(after.Width - before.Width) < 2 && Math.Abs(after.Height - before.Height) < 2 &&
                retrieval.SelectedItem?.Id == image.Id && island.DiagnosticRetrievalView.DiagnosticBuildDragData() is not null,
                "Back restores the search size and original selection remains draggable", after);
            retrieval.Query = "不存在的检索内容-beta2"; await Task.Delay(450);
            Check(retrieval.Empty && retrieval.SelectedItem is null, "Empty Chinese query results clear stale selection and actions");
            retrieval.Query = string.Empty; await Task.Delay(450);
            services.GetRequiredService<DropSpace.Core.Island.IslandExperienceCoordinator>().DismissNow();

            for (var i = 0; i < 3; i++)
            {
                music.Open();
                await Task.Delay(550);
                Check(music.GetState().IsOpen && music.CurrentWindow!.GetState().BackgroundFault is null,
                    $"Native music window reopen {i + 1} has no background fault");
                music.Close(); await Task.Delay(200);
            }
            music.Open(); ResizeMusicDips(music.CurrentWindow!, 1040, 760);
            await At(44.9);
        }
        catch (Exception error) { failure = error.ToString(); }
        var path = Path.Combine(output, "receipt.json");
        var passed = checks.Count(check => check.Status == "passed");
        var failed = checks.Count(check => check.Status == "failed");
        var status = failure is null && failed == 0 ? "passed" : "failed";
        await WriteAsync(path, new { status, syntheticMedia = true, realAudioCapture = false, checks, samples, error = failure,
            limits = "Fixture media with real native windows/compositor and real isolated content. Does not prove rare background heap-corruption resolution or real audio response. Pixel appearance is reviewed separately through Computer Use." });
        return new(status, path, passed, failed, failure);
    }
}
