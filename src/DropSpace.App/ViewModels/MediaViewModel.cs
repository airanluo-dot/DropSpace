using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.ViewModels;

public sealed class MediaViewModel : ObservableObject
{
    private MediaSessionSnapshot _session = MediaSessionSnapshot.Empty;
    private LyricsHighlightFrame _lyrics = LyricsHighlightFrame.Empty;
    private SpectrumFrame _spectrum = SpectrumFrame.Empty;
    private ImageSource? _artwork;
    private TimeSpan _position;
    private AppSettings _settings = new();
    private string _controlError = string.Empty;
    private bool _isReducedMotion;
    private bool _positionEstimated = true;
    private readonly IAppStringLocalizer _strings;
    private readonly HashSet<object> _visibleOwners = [];
    private bool _presentationVisible;
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
        PlayPauseCommand = new AsyncRelayCommand(token => Execute(() => media.PlayPauseAsync(token)), () => Session.CanPlay || Session.CanPause);
        PreviousCommand = new AsyncRelayCommand(token => Execute(() => media.SkipPreviousAsync(token)), () => Session.CanSkipPrevious);
        NextCommand = new AsyncRelayCommand(token => Execute(() => media.SkipNextAsync(token)), () => Session.CanSkipNext);
        SeekCommand = new AsyncRelayCommand<double?>(value => Execute(async () => { if (value is { } seconds && double.IsFinite(seconds)) await media.SeekAsync(Session.Timeline.Start + TimeSpan.FromSeconds(seconds)); }), _ => Session.CanSeek && !PositionEstimated);
        async Task Execute(Func<Task> action)
        {
            try { ControlError = string.Empty; await action(); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                logger.LogDebug("Media control failed ({Category}).", exception.GetType().Name);
                ControlError = strings.Get("MediaControlFailed");
            }
        }
    }
    public string ControlError { get => _controlError; private set => SetProperty(ref _controlError, value); }
    public bool IsReducedMotion { get => _isReducedMotion; internal set => SetProperty(ref _isReducedMotion, value); }
    public bool PositionEstimated { get => _positionEstimated; internal set { if (SetProperty(ref _positionEstimated, value)) { OnPropertyChanged(nameof(TimelineStatus)); SeekCommand.NotifyCanExecuteChanged(); } } }
    public MediaSessionSnapshot Session
    {
        get => _session;
        internal set
        {
            if (!SetProperty(ref _session, value)) return;
            OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Artist)); OnPropertyChanged(nameof(CurrentLyricText));
            OnPropertyChanged(nameof(IsPlaying)); OnPropertyChanged(nameof(DurationSeconds)); OnPropertyChanged(nameof(PlaybackGlyph));
            OnPropertyChanged(nameof(ArtistAlbum)); OnPropertyChanged(nameof(PlayPauseLabel)); OnPropertyChanged(nameof(TimelineStatus));
            PlayPauseCommand.NotifyCanExecuteChanged(); PreviousCommand.NotifyCanExecuteChanged(); NextCommand.NotifyCanExecuteChanged(); SeekCommand.NotifyCanExecuteChanged();
        }
    }
    public LyricsHighlightFrame Lyrics
    {
        get => _lyrics;
        internal set { if (SetProperty(ref _lyrics, value)) OnPropertyChanged(nameof(CurrentLyricText)); }
    }
    public SpectrumFrame Spectrum { get => _spectrum; internal set => SetProperty(ref _spectrum, value); }
    public ImageSource? Artwork { get => _artwork; internal set => SetProperty(ref _artwork, value); }
    public AppSettings Settings { get => _settings; internal set => SetProperty(ref _settings, value); }
    public TimeSpan Position
    {
        get => _position;
        internal set
        {
            if (!SetProperty(ref _position, value)) return;
            OnPropertyChanged(nameof(PositionSeconds)); OnPropertyChanged(nameof(ElapsedText)); OnPropertyChanged(nameof(RemainingText));
        }
    }
    public string Title => Session.TrackTitle;
    public string Artist => Session.Artist;
    public string ArtistAlbum => string.Join(" · ", new[] { Artist, Session.AlbumTitle }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string PlayPauseLabel => _strings.Get(IsPlaying ? "MediaPauseLabel" : "MediaPlayLabel");
    public string TimelineStatus => string.IsNullOrEmpty(Title) ? string.Empty : PositionEstimated ? _strings.Get("MediaEstimatedTimeline") : string.Empty;
    public string CurrentLyricText => Lyrics.Line?.Text ?? Title;
    public string? SecondaryLyricText => LyricsDisplayPolicy.Secondary(Lyrics.Line, _strings.Culture.Name, Settings.Lyrics.Enabled && Settings.Lyrics.SecondaryLyrics);
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
    private static string FormatTime(TimeSpan time)
    {
        var seconds = Math.Max(0, (long)time.TotalSeconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
