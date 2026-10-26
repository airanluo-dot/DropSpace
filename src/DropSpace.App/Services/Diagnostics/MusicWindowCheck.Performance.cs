using System.Diagnostics;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class MusicWindowCheck
{
    // Explicit, bounded fixture only. It observes the real UI dispatcher while
    // exercising admitted-looking synthetic translations, not model inference.
    private static async Task<PortraitResult> VerifyBeta2PerformanceAsync(string output,
        MusicWindowService music, MediaViewModel media, LyricsLine[] lines,
        IslandExperienceCoordinator experience, OverlayWindowService overlay)
    {
        if (Directory.Exists(output)) throw new IOException("Performance checks require a fresh output directory.");
        Directory.CreateDirectory(output);
        music.CurrentWindow?.SetMinimized(true);
        var settings = media.Settings;
        media.Settings = settings with { Lyrics = settings.Lyrics with { GlowMode = LyricsGlowMode.AiLyrics } };
        var translated = lines.Select(line => line with { TranslationOrigin = LyricsTranslationOrigin.LocalAi }).ToArray();
        var timeline = new LyricsTimelineEngine();
        void Update(int index)
        {
            // Progressive translation emits new records; the shared projection
            // must retain its UI/resource owners across these updates.
            var document = new LyricsDocument(translated.Select(line => line with { }).ToArray(), LyricsProviderKind.LocalLrc);
            media.SetLyricsDocument(document);
            media.Position = TimeSpan.FromSeconds(40 + index % 19);
            media.Lyrics = timeline.GetFrame(document, media.Position, 0);
        }
        Update(0);
        await Task.Delay(1_000);
        var checks = new List<CheckResult>();
        var samples = new List<object>();
        using var process = Process.GetCurrentProcess();
        object Resources()
        {
            process.Refresh();
            return new { handles = process.HandleCount, privateBytes = process.PrivateMemorySize64,
                managedBytes = GC.GetTotalMemory(false), allocatedBytes = GC.GetTotalAllocatedBytes(),
                gen2Collections = GC.CollectionCount(2), cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds };
        }
        async Task Sample(string name, bool animate)
        {
            var intervals = new List<double>(2_000);
            var last = Stopwatch.GetTimestamp();
            void Frame(object? sender, object args)
            {
                var now = Stopwatch.GetTimestamp();
                if (intervals.Count < 2_000) intervals.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                last = now;
            }
            var before = Resources();
            var watch = Stopwatch.StartNew();
            CompositionTarget.Rendering += Frame;
            try
            {
                for (var i = 0; i < 32; i++)
                {
                    if (animate && i % 4 == 0)
                    {
                        if (i % 8 == 0) experience.Collapse(); else experience.Open(IslandPage.Music);
                    }
                    Update(i);
                    await Task.Delay(125);
                }
            }
            finally { CompositionTarget.Rendering -= Frame; }
            var sorted = intervals.Order().ToArray();
            samples.Add(new { name, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, frameCallbacks = sorted.Length,
                medianMilliseconds = sorted.Length > 0 ? sorted[sorted.Length / 2] : 0,
                p95Milliseconds = sorted.Length > 0 ? sorted[(int)((sorted.Length - 1) * .95)] : 0,
                maximumMilliseconds = sorted.LastOrDefault(), over100Milliseconds = sorted.Count(value => value > 100),
                before, after = Resources(), glowEligible = media.IsIslandGlowActive,
                island = overlay.DiagnosticActiveWindow.ObserveModuleOverlay() });
        }
        try
        {
            checks.Add(new(media.IsIslandGlowActive ? "passed" : "failed", "beta2-performance", "AI translation actually activates the existing native glow", null));
            await Sample("early-transitions", true);
            experience.Collapse();
            await Task.Delay(700);
            await Sample("compact-progressive-lyrics", false);
            await Sample("late-transitions", true);
            experience.Open(IslandPage.Music);
            await Task.Delay(900);
            var state = overlay.DiagnosticActiveWindow.ObserveModuleOverlay();
            checks.Add(new(media.IsIslandGlowActive && state.MusicActive && Math.Abs(state.Width - 560) < 1 && Math.Abs(state.Height - 340) < 1 ? "passed" : "failed",
                "beta2-performance", "Repeated progressive updates and transitions retain glow, music page and the shared 560 x 340 geometry", state));
        }
        finally { media.Settings = settings; }
        var failed = checks.Count(check => check.Status == "failed");
        var path = Path.Combine(output, "receipt.json");
        await WriteAsync(path, new { status = failed == 0 ? "passed" : "failed", syntheticMedia = true,
            modelInference = false, checks, samples,
            limits = "Bounded native UI callback intervals and process counters, not GPU-present FPS or proof of hours-long stability/native-crash resolution. No production monitor is installed." });
        return new(failed == 0 ? "passed" : "failed", path, checks.Count - failed, failed, null);
    }
}
