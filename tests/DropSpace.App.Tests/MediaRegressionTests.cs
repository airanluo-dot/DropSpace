using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaRegressionTests
{
    [TestMethod]
    public async Task MatchedLyricsGapNeverFallsBackToSongTitle()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var view = new MediaViewModel(service, IdentityAppStringLocalizer.Instance, NullLogger<MediaViewModel>.Instance);
        view.Session = MediaSessionSnapshot.Empty with { TrackTitle = "Song title" };
        var line = new DropSpace.Core.Lyrics.LyricsLine(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Lyric text", null, []);
        view.SetLyricsDocument(new([line], DropSpace.Core.Models.LyricsProviderKind.LocalLrc));
        view.Position = TimeSpan.FromSeconds(2.5);
        Assert.AreEqual("Lyric text", view.CurrentLyricText);
        Assert.IsFalse(view.LyricPresentation.IsInterlude);
        view.Position = TimeSpan.FromSeconds(7);
        Assert.IsTrue(view.LyricPresentation.IsInterlude);
        Assert.AreEqual("Lyric text", view.CurrentLyricText);
        view.SetLyricsDocument(DropSpace.Core.Lyrics.LyricsDocument.Empty);
        Assert.AreEqual("Song title", view.CurrentLyricText);
    }

    [TestMethod]
    public async Task FrequentTimelineUpdatesDoNotStarveNewMetadata()
    {
        long metadataRevision = 1;
        var timelineUpdates = 0;
        var reads = 0;
        var (value, revision) = await WindowsMediaSessionService.ReadStableMetadataAsync(async _ =>
        {
            reads++;
            for (var i = 0; i < 20; i++) { timelineUpdates++; await Task.Yield(); }
            return "New track";
        }, () => metadataRevision, CancellationToken.None);
        Assert.AreEqual("New track", value);
        Assert.AreEqual(1L, revision);
        Assert.AreEqual(1, reads);
        Assert.AreEqual(20, timelineUpdates);
    }

    [TestMethod]
    public async Task TrackChangeDuringReadRetriesAndPublishesNewMetadata()
    {
        long revision = 0;
        var reads = 0;
        var result = await WindowsMediaSessionService.ReadStableMetadataAsync(_ =>
        {
            reads++;
            if (reads == 1) { revision++; return Task.FromResult("Old track"); }
            return Task.FromResult("New track");
        }, () => revision, CancellationToken.None);
        Assert.AreEqual("New track", result.Value);
        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    public async Task RepeatedTrackChangesRemainBoundedAndDoNotPublishStaleMetadata()
    {
        long revision = 0;
        var reads = 0;
        var result = await WindowsMediaSessionService.ReadStableMetadataAsync(_ =>
        {
            reads++;
            revision++;
            return Task.FromResult("Stale track");
        }, () => revision, CancellationToken.None);
        Assert.IsNull(result.Value);
        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    public async Task OptionalArtworkFailureDoesNotDiscardReadableMetadata()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var artwork = await service.ReadOptionalArtworkAsync(_ => Task.FromException<byte[]?>(new IOException("Unavailable thumbnail")), CancellationToken.None);
        Assert.IsNull(artwork);
    }

    [TestMethod]
    public async Task OptionalArtworkHasAnIndependentDeadline()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var artwork = await service.ReadOptionalArtworkAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(artwork);
    }

    [TestMethod]
    public async Task ArtworkCancellationDoesNotBecomeSuccessfulMetadata()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.ReadOptionalArtworkAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<byte[]?>(null);
            }, stop.Token));
    }

    [TestMethod]
    public async Task PlayPauseRequiresTheCapabilityForTheCurrentPlaybackState()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var view = new MediaViewModel(service, IdentityAppStringLocalizer.Instance, NullLogger<MediaViewModel>.Instance);
        view.Session = MediaSessionSnapshot.Empty with { PlaybackState = MediaPlaybackState.Playing, CanPlay = true, CanPause = false };
        Assert.IsFalse(view.PlayPauseCommand.CanExecute(null));
        view.Session = view.Session with { CanPause = true, CanPlay = false };
        Assert.IsTrue(view.PlayPauseCommand.CanExecute(null));
        view.Session = view.Session with { PlaybackState = MediaPlaybackState.Paused };
        Assert.IsFalse(view.PlayPauseCommand.CanExecute(null));
        view.Session = view.Session with { CanPlay = true };
        Assert.IsTrue(view.PlayPauseCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ChangedTimelineBoundsNotifyAllRelativeTimeBindings()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var view = new MediaViewModel(service, IdentityAppStringLocalizer.Instance, NullLogger<MediaViewModel>.Instance);
        view.Position = TimeSpan.FromSeconds(15);
        var notifications = new HashSet<string?>();
        view.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        view.Session = view.Session with { Timeline = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), 1, DateTimeOffset.UtcNow) };
        Assert.AreEqual(5, view.PositionSeconds);
        Assert.AreEqual("0:05", view.ElapsedText);
        Assert.AreEqual("-1:25", view.RemainingText);
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.PositionSeconds)));
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.ElapsedText)));
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.RemainingText)));
    }

    [TestMethod]
    public void LyricsRetryWhenSameTrackGainsDurationEvidence()
    {
        var timeline = new MediaTimelineSnapshot(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.UtcNow);
        var previous = MediaSessionSnapshot.Empty with
        {
            SessionId = "session",
            SourceAppUserModelId = "player",
            TrackTitle = "Song",
            Artist = "Artist",
            Timeline = timeline,
        };
        var current = previous with { Timeline = timeline with { End = TimeSpan.FromSeconds(180) } };

        Assert.IsTrue(MediaExperienceService.ShouldRetryLyricsWithImprovedEvidence(previous, current, false));
        Assert.IsFalse(MediaExperienceService.ShouldRetryLyricsWithImprovedEvidence(previous, current, true));
        Assert.IsFalse(MediaExperienceService.ShouldRetryLyricsWithImprovedEvidence(current, current, false));
    }
}
