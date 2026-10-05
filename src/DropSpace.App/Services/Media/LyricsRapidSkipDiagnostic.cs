#if DEBUG
using DropSpace.Core.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.App.Services.Media;

// Explicit local investigation only. Uses the same transport service as the music buttons.
internal static class LyricsRapidSkipDiagnostic
{
    public static async Task CheckProvidersAsync(WindowsMediaSessionService media, LyricsService lyrics,
        LyricsSettings settings, CancellationToken token)
    {
        if (!LyricsRequestTrace.Enabled || Environment.GetEnvironmentVariable("DROPSPACE_LYRICS_CHECK_PROVIDERS") != "1") return;
        try
        {
            while (string.IsNullOrWhiteSpace(media.Current.TrackTitle) || media.Current.Timeline.Duration <= TimeSpan.Zero)
                await Task.Delay(250, token).ConfigureAwait(false);
            await Task.Delay(2000, token).ConfigureAwait(false);
            var current = media.Current;
            var query = new LyricsQuery(current.TrackTitle, current.Artist, current.AlbumTitle, current.Timeline.Duration)
            { AlbumArtist = current.AlbumArtist, TrackIdentity = current.LyricsCacheIdentity, PreferredTranslationLanguage = "zh-Hans" };
            foreach (var provider in new[] { LyricsProviderKind.NetEase, LyricsProviderKind.QqMusic, LyricsProviderKind.Kugou,
                LyricsProviderKind.Amll, LyricsProviderKind.Lrclib })
            {
                if (!media.Current.IsSameTrack(current)) return;
                using var trace = LyricsRequestTrace.Begin(new { diagnostic = "provider-check", provider = provider.ToString(),
                    current.TrackTitle, current.Artist, current.AlbumTitle, duration = current.Timeline.Duration.TotalSeconds });
                var result = await lyrics.QueryDetailedAsync(query, settings with { Enabled = true, Mode = LyricsMode.Online,
                    Provider = provider, BackupProvider = null, SearchRemainingProviders = false, SelectionMode = LyricsSelectionMode.Rules },
                    token, refresh: true, reportOriginal: document => trace.Write("provider-preview", LyricsRequestTrace.Describe(document))).ConfigureAwait(false);
                trace.Write("provider-result", new { status = result.Status.ToString(), result.TranslationLookupIncomplete,
                    document = LyricsRequestTrace.Describe(result.Document) });
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { LyricsRequestTrace.Record("provider-check-failure", new { category = error.GetType().Name }); }
    }

    public static async Task RunAsync(WindowsMediaSessionService media, CancellationToken token)
    {
        if (!LyricsRequestTrace.Enabled || !int.TryParse(Environment.GetEnvironmentVariable("DROPSPACE_LYRICS_RAPID_SKIP"), out var count) || count is < 2 or > 20) return;
        var requestedSource = Environment.GetEnvironmentVariable("DROPSPACE_LYRICS_RAPID_SOURCE") ?? "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
        try
        {
            while (media.Current.PlaybackState != MediaPlaybackState.Playing || media.Current.SourceAppUserModelId != requestedSource)
                await Task.Delay(250, token).ConfigureAwait(false);
            var source = media.Current.SourceAppUserModelId;
            using var trace = LyricsRequestTrace.Begin(new { diagnostic = "rapid-skip", count, intervalMs = 2500, source });
            for (var index = 0; index < count; index++)
            {
                var current = media.Current;
                if (current.SourceAppUserModelId != source || current.PlaybackState != MediaPlaybackState.Playing) return;
                trace.Write("sample", new { index, current.TrackTitle, current.Artist, current.AlbumArtist, current.AlbumTitle, duration = current.Timeline.Duration.TotalSeconds });
                if (index == count - 1) break;
                await Task.Delay(2500, token).ConfigureAwait(false);
                if (media.Current.SourceAppUserModelId != source || media.Current.PlaybackState != MediaPlaybackState.Playing) return;
                await media.SkipNextAsync(token).ConfigureAwait(false);
            }
            trace.Write("rapid-complete", new { });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { LyricsRequestTrace.Record("rapid-failure", new { category = error.GetType().Name }); }
    }
}
#endif
