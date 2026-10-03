using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ExpandedMusicLyricsTests
{
    [TestMethod]
    public void AiLabelToggleNotifiesAllViewsWithoutChangingDocumentOrOrigin()
    {
        var view = CreateView();
        var source = view.LyricsLines[0] with { TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "en-US" };
        var document = new LyricsDocument([source], LyricsProviderKind.NetEase);
        view.SetLyricsDocument(document);
        var notifications = 0;
        view.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MediaViewModel.SecondaryLyricText)) notifications++; };
        Assert.AreEqual("AI · Translation", view.SecondaryLyricText);
        view.Settings = view.Settings with { Lyrics = view.Settings.Lyrics with { ShowAiLyricsLabel = false } };
        Assert.AreEqual("Translation", view.SecondaryLyricText);
        Assert.AreEqual(1, notifications);
        Assert.AreSame(document.Lines, view.LyricsLines);
        Assert.AreEqual(LyricsTranslationOrigin.LocalAi, view.LyricPresentation.Line!.TranslationOrigin);
        view.Settings = view.Settings with { Lyrics = view.Settings.Lyrics with { ShowAiLyricsLabel = true } };
        Assert.AreEqual("AI · Translation", view.SecondaryLyricText);
        Assert.AreEqual(2, notifications);
    }

    [TestMethod]
    public void SwitchingTrackImmediatelyHidesOldOriginalTranslationAndOrigin()
    {
        var view = CreateView();
        view.Lyrics = new(view.LyricsLines[0], -1, 0, 1);
        Assert.AreEqual("First", view.CurrentLyricText);
        view.Session = view.Session with { TrackTitle = "Next track" };
        Assert.AreEqual("Next track", view.CurrentLyricText);
        Assert.IsNull(view.SecondaryLyricText);
        Assert.IsNull(view.LyricPresentation.Line);
        Assert.HasCount(0, view.LyricsLines);
    }

    [TestMethod]
    public void ProgressiveTranslationUsesCurrentDocumentBeforeNextHighlightFrame()
    {
        var view = CreateView();
        view.Lyrics = new(view.LyricsLines[0], -1, 0, 1);
        var translated = view.LyricsLines[0] with { Secondary = "Updated translation", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "en-US" };
        view.SetLyricsDocument(new([translated], LyricsProviderKind.NetEase));
        Assert.AreEqual("AI · Updated translation", view.SecondaryLyricText);
        Assert.AreSame(translated, view.LyricPresentation.Line);
        Assert.AreEqual(0, view.CurrentLyricIndex);
    }

    [TestMethod]
    public void RepublishingTheSameCachedDocumentRebindsItToTheCurrentTrack()
    {
        var view = CreateView();
        var document = new LyricsDocument(view.LyricsLines, LyricsProviderKind.LocalLrc);
        view.SetLyricsDocument(document);
        view.Session = view.Session with { TrackTitle = "Same lyrics on a different recording" };
        Assert.IsNull(view.SecondaryLyricText);
        view.SetLyricsDocument(document);
        Assert.AreEqual("First", view.CurrentLyricText);
        Assert.AreEqual("Translation", view.SecondaryLyricText);
    }

    [TestMethod]
    public void PreviewUsesTrackRelativeTimeAndClearsImmediatelyOnTrackSwitch()
    {
        var view = CreateView();
        view.Position = TimeSpan.FromSeconds(13);
        Assert.AreEqual("Second", view.NextLyricText);
        view.Session = view.Session with { TrackTitle = "Another song" };
        Assert.IsNull(view.NextLyricText);
        view.SetLyricsDocument(LyricsDocument.Empty);
        Assert.IsNull(view.NextLyricText);
    }

    [TestMethod]
    public void PreviewDoesNotDependOnTranslationVisibilityAndHonorsLyricsToggle()
    {
        var view = CreateView();
        Assert.AreEqual("Second", view.NextLyricText);
        view.Settings = view.Settings with { Lyrics = view.Settings.Lyrics with { SecondaryLyrics = false } };
        Assert.IsNull(view.SecondaryLyricText);
        Assert.AreEqual("Second", view.NextLyricText);
        view.Settings = view.Settings with { Lyrics = view.Settings.Lyrics with { Enabled = false } };
        Assert.IsNull(view.NextLyricText);
    }

    [TestMethod]
    public void PreviewBindingIsNotifiedForClockOffsetFrameDocumentAndSessionChanges()
    {
        var view = CreateView();
        var count = 0;
        view.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MediaViewModel.NextLyricText)) count++; };
        view.Position = TimeSpan.FromSeconds(13);
        view.Settings = view.Settings with { Lyrics = view.Settings.Lyrics with { DelayMilliseconds = 500 } };
        view.Lyrics = new(view.LyricsLines[0], 0, .5, 1);
        view.SetLyricsDocument(LyricsDocument.Empty);
        view.Session = view.Session with { TrackTitle = "Another song" };
        Assert.AreEqual(5, count);
        Assert.IsNull(view.NextLyricText);
    }

    private static MediaViewModel CreateView()
    {
        var view = new MediaViewModel(new NoopMediaService(), new EnglishStrings(), NullLogger<MediaViewModel>.Instance);
        view.Settings = view.Settings with { Lyrics = view.Settings.Lyrics with { Enabled = true, SecondaryLyrics = true } };
        view.Session = MediaSessionSnapshot.Empty with
        {
            TrackTitle = "Song",
            Timeline = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40), 1, DateTimeOffset.UtcNow),
        };
        view.Position = TimeSpan.FromSeconds(10);
        view.SetLyricsDocument(new([
            new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "First", "Translation", []),
            new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12), "Second", null, []),
        ], LyricsProviderKind.LocalLrc));
        return view;
    }

    private sealed class NoopMediaService : IMediaSessionService
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

    private sealed class EnglishStrings : IAppStringLocalizer
    {
        public System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo("en-US");
        public string Get(string key) => key;
        public bool TryGet(string key, out string value) { value = key; return true; }
        public string Format(string key, params object?[] arguments) => string.Format(Culture, key, arguments);
    }
}
