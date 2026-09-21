using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaPlaybackModeTests
{
    [TestMethod]
    public async Task KnownStatesWithoutCapabilitiesRemainReadOnly()
    {
        await using var media = new RecordingMediaService();
        var view = CreateView(media, MediaSessionSnapshot.Empty with { ShuffleActive = true, RepeatMode = MediaRepeatMode.List });
        Assert.IsFalse(view.ToggleShuffleCommand.CanExecute(null));
        Assert.IsFalse(view.CycleRepeatCommand.CanExecute(null));
        Assert.AreEqual("MediaShuffleOn", view.ShuffleLabel);
        Assert.AreEqual("MediaRepeatAll", view.RepeatLabel);
        Assert.AreEqual("MediaShuffleUnavailable", view.ShuffleHelpText);
        Assert.AreEqual("MediaRepeatUnavailable", view.RepeatHelpText);
        await view.ToggleShuffleCommand.ExecuteAsync(null);
        await view.CycleRepeatCommand.ExecuteAsync(null);
        Assert.IsNull(media.RequestedShuffle);
        Assert.IsNull(media.RequestedRepeat);
    }

    [TestMethod]
    public async Task MissingStateIsUnknownAndNeverGuessedAsOff()
    {
        await using var media = new RecordingMediaService();
        var view = CreateView(media, MediaSessionSnapshot.Empty with { CanChangeShuffle = true, CanChangeRepeat = true });
        Assert.IsFalse(view.ToggleShuffleCommand.CanExecute(null));
        Assert.IsFalse(view.CycleRepeatCommand.CanExecute(null));
        Assert.AreEqual("MediaShuffleUnknown", view.ShuffleLabel);
        Assert.AreEqual("MediaRepeatUnknown", view.RepeatLabel);
        Assert.AreEqual("MediaShuffleUnknownHelp", view.ShuffleHelpText);
        Assert.AreEqual("MediaRepeatUnknownHelp", view.RepeatHelpText);
        await view.ToggleShuffleCommand.ExecuteAsync(null);
        await view.CycleRepeatCommand.ExecuteAsync(null);
        Assert.IsNull(media.RequestedShuffle);
        Assert.IsNull(media.RequestedRepeat);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ShuffleRequestsOppositeStateWithoutOptimisticPublication(bool initial)
    {
        await using var media = new RecordingMediaService();
        var view = CreateView(media, MediaSessionSnapshot.Empty with { CanChangeShuffle = true, ShuffleActive = initial });
        Assert.IsTrue(view.ToggleShuffleCommand.CanExecute(null));
        await view.ToggleShuffleCommand.ExecuteAsync(null);
        Assert.AreEqual(!initial, media.RequestedShuffle);
        Assert.AreEqual(initial, view.Session.ShuffleActive, "The player must report the new state before the UI displays it.");
        view.Session = view.Session with { ShuffleActive = !initial };
        Assert.AreEqual(!initial ? "MediaShuffleOn" : "MediaShuffleOff", view.ShuffleLabel);
    }

    [TestMethod]
    [DataRow(MediaRepeatMode.None, MediaRepeatMode.List)]
    [DataRow(MediaRepeatMode.List, MediaRepeatMode.Track)]
    [DataRow(MediaRepeatMode.Track, MediaRepeatMode.None)]
    public async Task RepeatCyclesOffAllOneWithoutChangingReportedState(MediaRepeatMode initial, MediaRepeatMode requested)
    {
        await using var media = new RecordingMediaService();
        var view = CreateView(media, MediaSessionSnapshot.Empty with { CanChangeRepeat = true, RepeatMode = initial });
        Assert.IsTrue(view.CycleRepeatCommand.CanExecute(null));
        await view.CycleRepeatCommand.ExecuteAsync(null);
        Assert.AreEqual(requested, media.RequestedRepeat);
        Assert.AreEqual(initial, view.Session.RepeatMode);
    }

    [TestMethod]
    public async Task RejectedPlayerRequestKeepsReportedStateAndShowsExistingControlError()
    {
        await using var media = new RecordingMediaService { Reject = true };
        var view = CreateView(media, MediaSessionSnapshot.Empty with { CanChangeRepeat = true, RepeatMode = MediaRepeatMode.Track });
        await view.CycleRepeatCommand.ExecuteAsync(null);
        Assert.AreEqual(MediaRepeatMode.Track, view.Session.RepeatMode);
        Assert.AreEqual("MediaControlFailed", view.ControlError);
    }

    [TestMethod]
    public async Task SourceChangeInvalidatesModeCommandsAndLabels()
    {
        await using var media = new RecordingMediaService();
        var view = CreateView(media, MediaSessionSnapshot.Empty with
        {
            SourceAppUserModelId = "player-a", CanChangeShuffle = true, CanChangeRepeat = true,
            ShuffleActive = true, RepeatMode = MediaRepeatMode.Track,
        });
        var notifications = new HashSet<string?>();
        view.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var shuffleChanged = 0;
        var repeatChanged = 0;
        view.ToggleShuffleCommand.CanExecuteChanged += (_, _) => shuffleChanged++;
        view.CycleRepeatCommand.CanExecuteChanged += (_, _) => repeatChanged++;
        view.Session = MediaSessionSnapshot.Empty with { SourceAppUserModelId = "player-b" };
        Assert.IsFalse(view.ToggleShuffleCommand.CanExecute(null));
        Assert.IsFalse(view.CycleRepeatCommand.CanExecute(null));
        Assert.AreEqual("MediaShuffleUnknown", view.ShuffleLabel);
        Assert.AreEqual("MediaRepeatUnknown", view.RepeatLabel);
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.ShuffleStateText)));
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.RepeatStateText)));
        Assert.IsTrue(shuffleChanged > 0 && repeatChanged > 0);
    }

    [TestMethod]
    public async Task InvalidRepeatValueCannotReachTheWindowsAdapter()
    {
        await using var media = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => media.SetRepeatModeAsync((MediaRepeatMode)999));
    }

    private static MediaViewModel CreateView(IMediaSessionService media, MediaSessionSnapshot snapshot) =>
        new(media, IdentityAppStringLocalizer.Instance, NullLogger<MediaViewModel>.Instance) { Session = snapshot };

    private sealed class RecordingMediaService : IMediaSessionService
    {
        public event EventHandler<MediaSessionSnapshot>? Changed { add { } remove { } }
        public MediaSessionSnapshot Current => MediaSessionSnapshot.Empty;
        public bool IsAvailable => true;
        public string AvailabilityReason => "Available";
        public bool? RequestedShuffle { get; private set; }
        public MediaRepeatMode? RequestedRepeat { get; private set; }
        public bool Reject { get; init; }
        public Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedShuffle = enabled;
            return Reject ? Task.FromException(new InvalidOperationException("Rejected by player")) : Task.CompletedTask;
        }
        public Task SetRepeatModeAsync(MediaRepeatMode mode, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedRepeat = mode;
            return Reject ? Task.FromException(new InvalidOperationException("Rejected by player")) : Task.CompletedTask;
        }
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipNextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipPreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
