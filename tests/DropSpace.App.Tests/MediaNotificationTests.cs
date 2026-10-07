using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaNotificationTests
{
    [TestMethod]
    public void TimelineObservationsAndArtworkKeepSessionUpdatesWithoutRefreshingMetadataOrCommands()
    {
        var view = CreateView();
        var sessionNotifications = 0; var unrelatedNotifications = 0; var commands = 0;
        view.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MediaViewModel.Session)) sessionNotifications++;
            else unrelatedNotifications++;
        };
        ObserveCommands(view, () => commands++);
        var latest = view.Session;
        for (var index = 1; index <= 30; index++)
        {
            latest = latest with
            {
                LastUpdated = DateTimeOffset.UnixEpoch.AddMilliseconds(index * 33),
                Timeline = latest.Timeline with
                {
                    Position = TimeSpan.FromMilliseconds(index * 33),
                    LastUpdated = DateTimeOffset.UnixEpoch.AddMilliseconds(index * 33),
                },
            };
            view.Session = latest;
        }
        latest = latest with { Artwork = [1, 2, 3] };
        view.Session = latest;
        Assert.AreSame(latest, view.Session);
        Assert.AreEqual(31, sessionNotifications, "Clock and artwork consumers still receive each new session snapshot.");
        Assert.AreEqual(0, unrelatedNotifications);
        Assert.AreEqual(0, commands);
    }

    [TestMethod]
    public void TimelineBoundsAndPlaybackCapabilitiesStillNotifyTheirConsumers()
    {
        var view = CreateView();
        view.Position = TimeSpan.FromSeconds(15);
        var notifications = new HashSet<string?>();
        view.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var playPauseChanges = 0; var seekChanges = 0; var skipChanges = 0;
        view.PlayPauseCommand.CanExecuteChanged += (_, _) => playPauseChanges++;
        view.SeekCommand.CanExecuteChanged += (_, _) => seekChanges++;
        view.NextCommand.CanExecuteChanged += (_, _) => skipChanges++;
        view.PreviousCommand.CanExecuteChanged += (_, _) => skipChanges++;
        view.PositionEstimated = false;
        seekChanges = 0;
        view.Session = view.Session with
        {
            Timeline = view.Session.Timeline with { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(100) },
        };
        Assert.AreEqual(5, view.PositionSeconds);
        Assert.AreEqual("0:05", view.ElapsedText);
        Assert.AreEqual("-1:25", view.RemainingText);
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.PositionSeconds)));
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.ElapsedText)));
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.RemainingText)));
        Assert.AreEqual(0, playPauseChanges);
        Assert.AreEqual(0, seekChanges);
        view.Session = view.Session with { CanPause = false, CanSeek = false, CanSkipNext = false, CanSkipPrevious = false };
        Assert.IsFalse(view.PlayPauseCommand.CanExecute(null));
        Assert.IsFalse(view.SeekCommand.CanExecute(null));
        Assert.IsFalse(view.NextCommand.CanExecute(null));
        Assert.IsFalse(view.PreviousCommand.CanExecute(null));
        Assert.AreEqual(1, playPauseChanges);
        Assert.AreEqual(1, seekChanges);
        Assert.AreEqual(2, skipChanges);
        view.Session = view.Session with { PlaybackState = MediaPlaybackState.Paused };
        Assert.IsTrue(view.PlayPauseCommand.CanExecute(null));
        Assert.AreEqual(2, playPauseChanges);
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.PlayPauseLabel)));
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.PlaybackGlyph)));
        notifications.Clear();
        view.Session = view.Session with { Timeline = view.Session.Timeline with { Start = view.Session.Timeline.End } };
        Assert.AreEqual("—", view.RemainingText);
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.RemainingText)), "Losing duration must update the unknown-remaining label even when End is unchanged.");
    }

    [TestMethod]
    public void NewRecordingMetadataNotifiesTheBoundTitleCreditsAlbumAndSource()
    {
        var view = CreateView();
        var notifications = new HashSet<string?>();
        view.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        view.Session = view.Session with
        {
            TrackTitle = "Next song", Artist = "Next artist", AlbumTitle = "Next album", SourceDisplayName = "Next player",
        };
        Assert.AreEqual("Next song", view.Title);
        Assert.AreEqual("Next artist · Next album", view.ArtistAlbum);
        Assert.AreEqual("Next player", view.SourceDisplayName);
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.Title)));
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.Artist)));
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.ArtistAlbum)));
        Assert.IsTrue(notifications.Contains(nameof(MediaViewModel.SourceDisplayName)));
    }

    [TestMethod]
    public void SteadySpectrumFramesKeepBandUpdatesAndNotifyVisibilityOnlyAtActualTransitions()
    {
        var view = CreateView();
        var spectra = 0; var visibilityChanges = 0;
        view.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MediaViewModel.Spectrum)) spectra++;
            if (args.PropertyName == nameof(MediaViewModel.HasSpectrumPresentation)) visibilityChanges++;
        };
        var frame = new SpectrumFrame(AudioCaptureMode.ProcessLoopback, [.1, .2, .3], 1);
        view.Spectrum = frame;
        Assert.IsTrue(view.HasSpectrumPresentation);
        Assert.AreEqual(1, visibilityChanges);
        for (var index = 2; index <= 30; index++) view.Spectrum = frame with { Revision = index };
        Assert.AreEqual(30, spectra);
        Assert.AreEqual(1, visibilityChanges);
        view.Session = view.Session with { PlaybackState = MediaPlaybackState.Paused };
        view.Spectrum = SpectrumFrame.Empty;
        Assert.IsTrue(view.HasSpectrumPresentation, "Pausing retains the current track's silent bars.");
        Assert.AreEqual(1, visibilityChanges);
        view.Session = view.Session with { PlaybackState = MediaPlaybackState.Playing };
        Assert.IsFalse(view.HasSpectrumPresentation);
        Assert.AreEqual(2, visibilityChanges);
        view.Spectrum = frame;
        Assert.IsTrue(view.HasSpectrumPresentation);
        Assert.AreEqual(3, visibilityChanges);
        view.Session = view.Session with { SessionId = "replacement-session" };
        Assert.IsFalse(view.HasSpectrumPresentation, "The previous track cannot supply the new track's presentation owner.");
        Assert.AreEqual(4, visibilityChanges);
        var spectraBeforeReuse = spectra;
        view.Spectrum = frame;
        Assert.IsTrue(view.HasSpectrumPresentation);
        Assert.AreEqual(5, visibilityChanges, "Rebinding an equal spectrum frame must still notify the changed presentation owner.");
        Assert.AreEqual(spectraBeforeReuse, spectra);
        view.Session = view.Session with { PlaybackState = MediaPlaybackState.Stopped };
        Assert.IsFalse(view.HasSpectrumPresentation, "A stopped session cannot keep active spectrum presentation.");
        Assert.AreEqual(6, visibilityChanges);
        view.Session = view.Session with { PlaybackState = MediaPlaybackState.Playing };
        Assert.IsTrue(view.HasSpectrumPresentation);
        Assert.AreEqual(7, visibilityChanges);
        view.Session = view.Session with { TrackTitle = string.Empty };
        Assert.IsFalse(view.HasSpectrumPresentation, "Clearing the title deactivates the session and retires its old track.");
        Assert.AreEqual(8, visibilityChanges);
    }

    [TestMethod]
    public void LanguageChangeRefreshesPlaybackLabelWithoutDependingOnAnotherSessionObservation()
    {
        var strings = new MutableStrings();
        var view = CreateView(strings);
        view.Settings = view.Settings with { Language = AppLanguagePreference.English };
        var notifications = 0;
        view.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MediaViewModel.PlayPauseLabel)) notifications++; };
        var currentSession = view.Session;
        strings.LanguageTag = "zh-CN";
        view.Settings = view.Settings with { Language = AppLanguagePreference.SimplifiedChinese };
        Assert.AreEqual("zh-CN:MediaPauseLabel", view.PlayPauseLabel);
        Assert.AreEqual(1, notifications);
        Assert.AreSame(currentSession, view.Session);
    }

    private static MediaViewModel CreateView(IAppStringLocalizer? strings = null)
    {
        var view = new MediaViewModel(new NoopMedia(), strings ?? IdentityAppStringLocalizer.Instance,
            NullLogger<MediaViewModel>.Instance);
        view.Session = MediaSessionSnapshot.Empty with
        {
            SessionId = "session", SourceAppUserModelId = "player", TrackTitle = "Song", Artist = "Artist",
            SourceDisplayName = "Player", PlaybackState = MediaPlaybackState.Playing, CanPlay = true,
            CanPause = true, CanSeek = true, CanSkipNext = true, CanSkipPrevious = true,
            Timeline = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(180), 1, DateTimeOffset.UnixEpoch),
        };
        return view;
    }

    private static void ObserveCommands(MediaViewModel view, Action changed)
    {
        view.PlayPauseCommand.CanExecuteChanged += (_, _) => changed();
        view.PreviousCommand.CanExecuteChanged += (_, _) => changed();
        view.NextCommand.CanExecuteChanged += (_, _) => changed();
        view.SeekCommand.CanExecuteChanged += (_, _) => changed();
    }

    private sealed class MutableStrings : IAppStringLocalizer
    {
        public string LanguageTag { get; set; } = "en-US";
        public System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo(LanguageTag);
        public string Get(string key) => $"{LanguageTag}:{key}";
        public bool TryGet(string key, out string value) { value = Get(key); return true; }
        public string Format(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments);
    }

    private sealed class NoopMedia : IMediaSessionService
    {
        public event EventHandler<MediaSessionSnapshot>? Changed { add { } remove { } }
        public MediaSessionSnapshot Current => MediaSessionSnapshot.Empty;
        public bool IsAvailable => true;
        public string AvailabilityReason => string.Empty;
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipNextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipPreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
