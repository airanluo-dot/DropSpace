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
}
