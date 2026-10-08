using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.ViewModels;

public sealed class MediaViewModel : ObservableObject
{
    private MediaSessionSnapshot _session = MediaSessionSnapshot.Empty;
    private LyricsHighlightFrame _lyrics = LyricsHighlightFrame.Empty;
    private LyricsDocument _lyricsDocument = LyricsDocument.Empty;
    private string _lyricsDocumentTrackIdentity = string.Empty;
    private int _currentLyricIndex = -1;
    private LyricsQueryStatus _lyricsStatus = LyricsQueryStatus.Disabled;
    private SpectrumFrame _spectrum = SpectrumFrame.Empty;
    private string? _spectrumPresentationTrackIdentity;
    private ImageSource? _artwork;
    private TimeSpan _position;
    private AppSettings _settings = new();
    private AppUiMessage _controlError = AppUiMessage.Empty;
    private bool _isReducedMotion;
    private bool _positionEstimated = true;
    private string _notifiedCurrentLyricText = string.Empty;
    private string? _notifiedSecondaryLyricText;
    private string? _notifiedNextLyricText;
    private readonly IAppStringLocalizer _strings;
    private readonly HashSet<object> _visibleOwners = [];
    private bool _presentationVisible;
    // Only an actual island may request glow audio. Main-window Music presentation
    // visibility must not accidentally light an off-screen island or start its capture.
    private readonly HashSet<object> _islandGlowOwners = [];
    private bool _islandGlowActive;
    public bool IsIslandGlowActive => Volatile.Read(ref _islandGlowActive);
    public void SetIslandGlowActive(object owner, bool active)
    {
        var wasActive = IsIslandGlowActive;
        if (active) _islandGlowOwners.Add(owner); else _islandGlowOwners.Remove(owner);
        Volatile.Write(ref _islandGlowActive, _islandGlowOwners.Count > 0);
        if (wasActive != IsIslandGlowActive) OnPropertyChanged(nameof(IsIslandGlowActive));
    }
    public bool IsPresentationVisible => Volatile.Read(ref _presentationVisible);
    public void SetPresentationVisible(object owner, bool visible)
    {
        var wasVisible = IsPresentationVisible;
        if (visible) _visibleOwners.Add(owner); else _visibleOwners.Remove(owner);
        Volatile.Write(ref _presentationVisible, _visibleOwners.Count > 0);
        if (wasVisible != IsPresentationVisible) OnPropertyChanged(nameof(IsPresentationVisible));
    }
    public MediaViewModel(IMediaSessionService media, IAppStringLocalizer strings, ILogger<MediaViewModel> logger)
    {
        _strings = strings;
        PlayPauseCommand = new AsyncRelayCommand(token => Execute(() => media.PlayPauseAsync(token)), () => IsPlaying ? Session.CanPause : Session.CanPlay);
        PreviousCommand = new AsyncRelayCommand(token => Execute(() => media.SkipPreviousAsync(token)), () => Session.CanSkipPrevious);
        NextCommand = new AsyncRelayCommand(token => Execute(() => media.SkipNextAsync(token)), () => Session.CanSkipNext);
        SeekCommand = new AsyncRelayCommand<double?>(value => Execute(async () => { if (value is { } seconds && double.IsFinite(seconds)) await media.SeekAsync(Session.Timeline.Start + TimeSpan.FromSeconds(seconds)); }), _ => Session.CanSeek && !PositionEstimated);
        async Task Execute(Func<Task> action)
        {
            try { ControlErrorMessage = AppUiMessage.Empty; await action(); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                logger.LogDebug("Media control failed ({Category}).", exception.GetType().Name);
                ControlErrorMessage = AppUiMessage.Resource("MediaControlFailed");
            }
        }
    }
    public string ControlError => _controlError.Render(_strings);
    private AppUiMessage ControlErrorMessage { set => SetProperty(ref _controlError, value, nameof(ControlError)); }
    public bool IsReducedMotion { get => _isReducedMotion; internal set => SetProperty(ref _isReducedMotion, value); }
    public bool PositionEstimated { get => _positionEstimated; internal set { if (SetProperty(ref _positionEstimated, value)) { OnPropertyChanged(nameof(TimelineStatus)); SeekCommand.NotifyCanExecuteChanged(); } } }
    public MediaSessionSnapshot Session
    {
        get => _session;
        internal set
        {
            var previous = _session;
            var previousTrack = previous.TrackIdentity;
            var currentTrack = value.TrackIdentity;
            var trackChanged = !string.Equals(previousTrack, currentTrack, StringComparison.Ordinal);
            var hadSpectrumPresentation = HasSpectrumPresentationFor(previousTrack);
            if (!SetProperty(ref _session, value)) return;
            if (trackChanged)
            {
                _spectrumPresentationTrackIdentity = null;
                SetProperty(ref _currentLyricIndex, -1, nameof(CurrentLyricIndex));
                OnPropertyChanged(nameof(LyricsLines));
            }
            var titleChanged = previous.TrackTitle != value.TrackTitle;
            var artistChanged = previous.Artist != value.Artist;
            var playbackChanged = (previous.PlaybackState == MediaPlaybackState.Playing) != IsPlaying;
            var startChanged = previous.Timeline.Start != value.Timeline.Start;
            if (titleChanged) OnPropertyChanged(nameof(Title));
            if (artistChanged) OnPropertyChanged(nameof(Artist));
            // Position and observation timestamps belong to the clock. They cannot
            // invalidate the same track's title, lyric text or control capabilities.
            if (trackChanged || startChanged) NotifyLyricTextChanges();
            if (previous.SourceDisplayName != value.SourceDisplayName) OnPropertyChanged(nameof(SourceDisplayName));
            if (playbackChanged)
            {
                OnPropertyChanged(nameof(IsPlaying)); OnPropertyChanged(nameof(PlaybackGlyph)); OnPropertyChanged(nameof(PlayPauseLabel));
            }
            if (previous.Timeline.Duration != value.Timeline.Duration) OnPropertyChanged(nameof(DurationSeconds));
            if (hadSpectrumPresentation != HasSpectrumPresentationFor(currentTrack)) OnPropertyChanged(nameof(HasSpectrumPresentation));
            if (startChanged)
            {
                OnPropertyChanged(nameof(PositionSeconds)); OnPropertyChanged(nameof(ElapsedText));
            }
            if (previous.Timeline.End != value.Timeline.End ||
                (previous.Timeline.Duration > TimeSpan.Zero) != (value.Timeline.Duration > TimeSpan.Zero))
                OnPropertyChanged(nameof(RemainingText));
            if (artistChanged || previous.AlbumTitle != value.AlbumTitle) OnPropertyChanged(nameof(ArtistAlbum));
            if (titleChanged)
            {
                OnPropertyChanged(nameof(TimelineStatus)); OnPropertyChanged(nameof(LyricsStatusText));
            }
            if (playbackChanged || previous.CanPlay != value.CanPlay || previous.CanPause != value.CanPause)
                PlayPauseCommand.NotifyCanExecuteChanged();
            if (previous.CanSkipPrevious != value.CanSkipPrevious) PreviousCommand.NotifyCanExecuteChanged();
            if (previous.CanSkipNext != value.CanSkipNext) NextCommand.NotifyCanExecuteChanged();
            if (previous.CanSeek != value.CanSeek) SeekCommand.NotifyCanExecuteChanged();
        }
    }
    public LyricsHighlightFrame Lyrics
    {
        get => _lyrics;
        internal set
        {
            if (!SetProperty(ref _lyrics, value)) return;
            var index = FindLyricIndex(value.Line);
            SetProperty(ref _currentLyricIndex, index, nameof(CurrentLyricIndex));
            NotifyLyricTextChanges();
        }
    }

    public IReadOnlyList<LyricsLine> LyricsLines =>
        string.Equals(_lyricsDocumentTrackIdentity, Session.TrackIdentity, StringComparison.Ordinal) ? _lyricsDocument.Lines : [];
    public int CurrentLyricIndex => _currentLyricIndex;

    internal void SetLyricsDocument(LyricsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (ReferenceEquals(_lyricsDocument, document) &&
            string.Equals(_lyricsDocumentTrackIdentity, Session.TrackIdentity, StringComparison.Ordinal)) return;
        _lyricsDocument = document;
        _lyricsDocumentTrackIdentity = Session.TrackIdentity;
        var index = FindLyricIndex(_lyrics.Line);
        SetProperty(ref _currentLyricIndex, index, nameof(CurrentLyricIndex));
        OnPropertyChanged(nameof(LyricsLines));
        NotifyLyricTextChanges();
    }
    public LyricsQueryStatus LyricsStatus
    {
        get => _lyricsStatus;
        internal set
        {
            if (!SetProperty(ref _lyricsStatus, value)) return;
            NotifyLyricTextChanges(); OnPropertyChanged(nameof(LyricsStatusText));
        }
    }
    public SpectrumFrame Spectrum
    {
        get => _spectrum;
        internal set
        {
            var trackIdentity = Session.TrackIdentity;
            var hadSpectrumPresentation = HasSpectrumPresentationFor(trackIdentity);
            if (IsPlaying && value.CaptureMode == AudioCaptureMode.ProcessLoopback)
                _spectrumPresentationTrackIdentity = trackIdentity;
            SetProperty(ref _spectrum, value);
            if (hadSpectrumPresentation != HasSpectrumPresentationFor(trackIdentity)) OnPropertyChanged(nameof(HasSpectrumPresentation));
        }
    }
    // Pausing capture may publish an empty frame. Keep the same session's
    // existing visual bars, now silent, without claiming new audio activity.
    public bool HasSpectrumPresentation => HasSpectrumPresentationFor(Session.TrackIdentity);
    private bool HasSpectrumPresentationFor(string trackIdentity) => Session.IsActive &&
        string.Equals(_spectrumPresentationTrackIdentity, trackIdentity, StringComparison.Ordinal) &&
        (Spectrum.CaptureMode == AudioCaptureMode.ProcessLoopback || Session.PlaybackState == MediaPlaybackState.Paused);
    public ImageSource? Artwork { get => _artwork; internal set => SetProperty(ref _artwork, value); }
    public AppSettings Settings
    {
        get => _settings;
        internal set
        {
            var languageChanged = _settings.Language != value.Language;
            if (!SetProperty(ref _settings, value)) return;
            NotifyLyricTextChanges(); OnPropertyChanged(nameof(LyricsStatusText)); OnPropertyChanged(nameof(TimelineStatus));
            if (languageChanged) OnPropertyChanged(string.Empty);
        }
    }
    public TimeSpan Position
    {
        get => _position;
        internal set
        {
            var oldElapsed = DisplaySeconds(Position - Session.Timeline.Start);
            var oldRemaining = DisplaySeconds(Session.Timeline.End - Position);
            if (!SetProperty(ref _position, value)) return;
            OnPropertyChanged(nameof(PositionSeconds));
            if (oldElapsed != DisplaySeconds(Position - Session.Timeline.Start)) OnPropertyChanged(nameof(ElapsedText));
            if (Session.Timeline.Duration > TimeSpan.Zero && oldRemaining != DisplaySeconds(Session.Timeline.End - Position))
                OnPropertyChanged(nameof(RemainingText));
            NotifyLyricTextChanges();
        }
    }
    public string Title => Session.TrackTitle;
    public string Artist => Session.Artist;
    public string SourceDisplayName => Session.SourceDisplayName;
    public string ArtistAlbum => string.Join(" · ", new[] { Artist, Session.AlbumTitle }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string PlayPauseLabel => _strings.Get(IsPlaying ? "MediaPauseLabel" : "MediaPlayLabel");
    public string TimelineStatus => string.IsNullOrEmpty(Title) ? string.Empty : PositionEstimated ? _strings.Get("MediaEstimatedTimeline") : string.Empty;
    public LyricsPresentation LyricPresentation
    {
        get
        {
            if (!string.Equals(_lyricsDocumentTrackIdentity, Session.TrackIdentity, StringComparison.Ordinal))
                return new(false, null, false, false);
            // Progressive translations replace a document before the next highlight frame.
            // Always render the current document's text and origin at the stable original index.
            var frame = CurrentLyricIndex >= 0 && CurrentLyricIndex < LyricsLines.Count
                ? Lyrics with { Line = LyricsLines[CurrentLyricIndex] } : LyricsHighlightFrame.Empty;
            return LyricsDisplayPolicy.Presentation(LyricsLines, frame,
                Position - Session.Timeline.Start, Settings.Lyrics.DelayMilliseconds);
        }
    }
    public string CurrentLyricText => Settings.Lyrics.Enabled && LyricPresentation.HasLyrics
        ? LyricPresentation.Line?.Text ?? string.Empty : Title;
    public string? SecondaryLyricText => LyricsDisplayPolicy.SecondaryPresentation(LyricPresentation.Line, LyricsTranslationTargetPolicy.ToLanguageTag(Settings.LyricsTranslationTarget), Settings.Lyrics.Enabled && Settings.Lyrics.SecondaryLyrics, Settings.Lyrics.ShowAiLyricsLabel);
    public string? NextLyricText => Settings.Lyrics.Enabled &&
        string.Equals(_lyricsDocumentTrackIdentity, Session.TrackIdentity, StringComparison.Ordinal)
            ? LyricsPreviewPolicy.NextLine(LyricsLines, Lyrics, Position - Session.Timeline.Start,
                Settings.Lyrics.DelayMilliseconds)?.Text : null;
    public string NextLyricLabel => _strings.Get("MediaNextLyricLabel");
    public string CurrentLyricsLabel => _strings.Get("MusicLyricsSection");
    public string LyricsStatusText => string.IsNullOrEmpty(Title) || !Settings.Lyrics.Enabled ? string.Empty : LyricsStatus switch
    {
        LyricsQueryStatus.Loading => _strings.Get("LyricsLoading"),
        LyricsQueryStatus.NotFound => _strings.Get("LyricsNotFound"),
        LyricsQueryStatus.Failed => _strings.Get("LyricsFailed"),
        _ => string.Empty,
    };
    public bool IsPlaying => Session.PlaybackState == MediaPlaybackState.Playing;
    public string PlaybackGlyph => IsPlaying ? "\uE769" : "\uE768";
    public double PositionSeconds => Math.Max(0, (Position - Session.Timeline.Start).TotalSeconds);
    public double DurationSeconds => Math.Max(1, Session.Timeline.Duration.TotalSeconds);
    public string ElapsedText => FormatTime(Position - Session.Timeline.Start);
    public string RemainingText => Session.Timeline.Duration > TimeSpan.Zero ? "-" + FormatTime(Session.Timeline.End - Position) : "—";
    public IAsyncRelayCommand PlayPauseCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand<double?> SeekCommand { get; }

    private int FindLyricIndex(LyricsLine? line)
    {
        if (line is null) return -1;
        // The active original index usually survives both clock frames and AI progress.
        if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricsDocument.Lines.Count &&
            SameOriginal(_lyricsDocument.Lines[_currentLyricIndex], line)) return _currentLyricIndex;
        for (var index = 0; index < _lyricsDocument.Lines.Count; index++)
        {
            var candidate = _lyricsDocument.Lines[index];
            if (ReferenceEquals(candidate, line) || SameOriginal(candidate, line))
            {
                return index;
            }
        }

        return -1;
    }

    private void NotifyLyricTextChanges()
    {
        SetProperty(ref _notifiedCurrentLyricText, CurrentLyricText, nameof(CurrentLyricText));
        SetProperty(ref _notifiedSecondaryLyricText, SecondaryLyricText, nameof(SecondaryLyricText));
        SetProperty(ref _notifiedNextLyricText, NextLyricText, nameof(NextLyricText));
    }

    private static bool SameOriginal(LyricsLine left, LyricsLine right) =>
        left.Start == right.Start && left.End == right.End && left.Text == right.Text;
    private static long DisplaySeconds(TimeSpan time) => Math.Max(0, (long)time.TotalSeconds);
    private static string FormatTime(TimeSpan time)
    {
        var seconds = DisplaySeconds(time);
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
