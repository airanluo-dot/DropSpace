using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaSessionNativeSmokeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task RealSmtcSubscriptionDrainsBeforeDisableReturns()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var observed = new TaskCompletionSource<MediaSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, snapshot) => observed.TrySetResult(snapshot);
        await service.SetEnabledAsync(true);
        Assert.IsTrue(service.IsAvailable, service.AvailabilityReason);
        var snapshot = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(snapshot.Artwork is null || snapshot.Artwork.Length <= 4 * 1024 * 1024);
        var filtered = new TaskCompletionSource<MediaSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, value) =>
        {
            if (value == MediaSessionSnapshot.Empty) filtered.TrySetResult(value);
        };
        service.SetAllowedSources([], restrictToList: true);
        await filtered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rejected = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.PlayPauseAsync());
        Assert.AreEqual("No media session is available.", rejected.Message);
        await service.SetEnabledAsync(false);
        Assert.IsFalse(service.IsAvailable);
        Assert.AreEqual(MediaSessionSnapshot.Empty, service.Current);
        Assert.AreEqual(0, service.AvailableSources.Count);
        await service.SetEnabledAsync(true);
        Assert.IsTrue(service.IsAvailable, service.AvailabilityReason);
        await service.SetEnabledAsync(false);
    }

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task RealAppleMusicSessionResolvesLyricsThroughGenericPipeline()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DROPSPACE_APPLE_MUSIC_NATIVE"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set DROPSPACE_APPLE_MUSIC_NATIVE=1 with Apple Music open on a lyric-bearing track.");
        }

        const string appleMusicId = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
        await using var media = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var observed = new TaskCompletionSource<MediaSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        media.Changed += (_, snapshot) =>
        {
            if (snapshot.SourceAppUserModelId.Equals(appleMusicId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(snapshot.TrackTitle))
            {
                observed.TrySetResult(snapshot);
            }
        };
        media.SetAllowedSources([appleMusicId], restrictToList: true);
        await media.SetEnabledAsync(true);
        Assert.IsTrue(media.IsAvailable, media.AvailabilityReason);

        var current = media.Current;
        var snapshot = current.SourceAppUserModelId.Equals(appleMusicId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(current.TrackTitle)
            ? current
            : await observed.Task.WaitAsync(TimeSpan.FromSeconds(15));

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var lyrics = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(client), Path.GetTempPath));
        var result = await lyrics.QueryDetailedAsync(new LyricsQuery(
            snapshot.TrackTitle,
            snapshot.Artist,
            snapshot.AlbumTitle,
            snapshot.Timeline.Duration,
            snapshot.TrackIdentity,
            snapshot.AlbumArtist), new LyricsSettings(), CancellationToken.None);

        TestContext.WriteLine(
            $"Apple Music: title={snapshot.TrackTitle}; artist={snapshot.Artist}; albumArtist={snapshot.AlbumArtist}; " +
            $"album={snapshot.AlbumTitle}; duration={snapshot.Timeline.Duration}; status={result.Status}; " +
            $"provider={result.Document.Provider}; lines={result.Document.Lines.Count}; match={result.Document.Match?.Score}");
        Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
        Assert.IsTrue(result.Document.Lines.Count > 0);
        Assert.IsNotNull(result.Document.Match);

        await media.SetEnabledAsync(false);
    }
}
