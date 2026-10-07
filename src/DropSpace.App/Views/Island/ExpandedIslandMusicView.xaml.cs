using System.ComponentModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DropSpace.App.Views.Island;

public sealed partial class ExpandedIslandMusicView : UserControl
{
    internal bool IsTranslationActuallyVisible => _presentationActive && IsLoaded && Visibility == Visibility.Visible &&
        LyricsArea.Visibility == Visibility.Visible && TranslatedLyric.Visibility == Visibility.Visible &&
        TranslatedLyric.Opacity > 0.01 && TranslationIntersectsViewport() &&
        !string.IsNullOrWhiteSpace(TranslatedLyric.Text) &&
        string.Equals(TranslatedLyric.Text, _view?.SecondaryLyricText, StringComparison.Ordinal);

    private bool TranslationIntersectsViewport()
    {
        if (TranslatedLyric.ActualWidth <= 0 || TranslatedLyric.ActualHeight <= 0 ||
            CurrentLyricsViewport.ViewportWidth <= 0 || CurrentLyricsViewport.ViewportHeight <= 0) return false;
        var bounds = TranslatedLyric.TransformToVisual(CurrentLyricsViewport).TransformBounds(
            new Windows.Foundation.Rect(0, 0, TranslatedLyric.ActualWidth, TranslatedLyric.ActualHeight));
        return LyricsDisplayPolicy.IntersectsViewport(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            CurrentLyricsViewport.ViewportWidth, CurrentLyricsViewport.ViewportHeight);
    }

    private MediaViewModel? _view;
    private readonly MediaRenderQueue _renderQueue = new();
    private EventHandler<object>? _renderHandler;
    private bool _presentationActive = true;
    internal long RenderCount { get; private set; }
    internal bool HasPendingSeekWork => _seekCommitTimer.IsRunning || _seekAcknowledgementTimer.IsRunning ||
        _queuedSeekSeconds is not null || _activePointerId is not null || _seekInteraction.HeldSeconds is not null;
    private readonly MediaSeekInteraction _seekInteraction = new(TimeSpan.FromSeconds(2));
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _seekCommitTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _seekAcknowledgementTimer;
    private uint? _activePointerId;
    private double? _queuedSeekSeconds;
    private string _trackIdentity = string.Empty;
    private string _lyricViewportText = string.Empty;
    private bool _updating;
    private bool _translationWasVisible;
    private string _displayedTranslation = string.Empty;
    private LyricsTranslationOrigin _displayedOrigin;
    public event EventHandler? TranslationVisibilityChanged;
    public ExpandedIslandMusicView()
    {
        InitializeComponent();
        _seekCommitTimer = DispatcherQueue.CreateTimer();
        _seekCommitTimer.Interval = TimeSpan.FromMilliseconds(120);
        _seekCommitTimer.IsRepeating = false;
        _seekCommitTimer.Tick += OnSeekCommitTimer;
        _seekAcknowledgementTimer = DispatcherQueue.CreateTimer();
        _seekAcknowledgementTimer.Interval = TimeSpan.FromSeconds(2);
        _seekAcknowledgementTimer.IsRepeating = false;
        _seekAcknowledgementTimer.Tick += (_, _) =>
        {
            if (!CanRender) return;
            _seekInteraction.RejectPending();
            RequestRender();
        };
        // Observe the whole slider, including track presses, even when its template
        // handles pointer events. Never depend on finding a Thumb before layout.
        Progress.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekPointerPressed), true);
        Progress.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekPointerReleased), true);
        Progress.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnSeekPointerCanceled), true);
        Progress.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekPointerCaptureLost), true);
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        CurrentLyricsViewport.ViewChanged += (_, _) => NotifyTranslationVisibility();
        LayoutUpdated += (_, _) => NotifyTranslationVisibility();
    }
    public MediaViewModel? ViewModel
    {
        get => _view;
        set
        {
            if (_view is not null) _view.PropertyChanged -= OnChanged;
            _view = value; DataContext = value;
            if (IsLoaded && _view is not null) _view.PropertyChanged += OnChanged;
            RequestRender();
        }
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_view is not null)
        {
            _view.PropertyChanged -= OnChanged;
            _view.PropertyChanged += OnChanged;
        }
        RequestRender();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_view is not null) _view.PropertyChanged -= OnChanged;
        CancelRender();
        CancelSeekInteraction();
        NotifyTranslationVisibility();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args) => RequestRender();
    internal void SetActive(bool active)
    {
        if (_presentationActive == active) return;
        _presentationActive = active;
        if (CanRender) RequestRender();
        else
        {
            CancelRender();
            CancelSeekInteraction();
        }
        NotifyTranslationVisibility();
    }
    internal void RefreshForPresentation()
    {
        CancelRender();
        if (_renderQueue.BeginImmediateRender()) Render();
    }
    private bool CanRender => _presentationActive && IsLoaded && Visibility == Visibility.Visible;
    private void RequestRender()
    {
        if (!_renderQueue.Request(CanRender, out var generation)) return;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(_renderHandler, handler)) _renderHandler = null;
            if (_renderQueue.BeginRender(CanRender, generation)) Render();
        };
        _renderHandler = handler;
        CompositionTarget.Rendering += handler;
    }
    private void CancelRender()
    {
        if (_renderHandler is not null) CompositionTarget.Rendering -= _renderHandler;
        _renderHandler = null;
        _renderQueue.Cancel();
    }
    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (CanRender) RequestRender();
        else
        {
            CancelRender();
            CancelSeekInteraction();
            NotifyTranslationVisibility();
        }
    }
    private void Render()
    {
        if (_view is null) return;
        RenderCount++;
        OriginalLyric.FontSize = _view.Settings.Lyrics.OriginalFontSize;
        TranslatedLyric.FontSize = _view.Settings.Lyrics.TranslationFontSize;
        NextLyric.FontSize = _view.Settings.Lyrics.OriginalFontSize * (13d / 16d);
        _updating = true;
        try
        {
            var trackIdentity = _view.Session.TrackIdentity;
            if (!string.Equals(_trackIdentity, trackIdentity, StringComparison.Ordinal))
            {
                _trackIdentity = trackIdentity;
                _lyricViewportText = string.Empty;
                CancelSeekInteraction();
            }
            if (!_seekInteraction.IsDragging && !_seekInteraction.IsPreviewing)
            {
                Progress.Maximum = _view.DurationSeconds;
                Progress.IsEnabled = _view.Session.CanSeek && !_view.PositionEstimated;
            }
            var playbackSeconds = _view.PositionEstimated ? 0 : Math.Clamp(_view.PositionSeconds, 0, Progress.Maximum);
            if (_seekInteraction.ShouldApplyPlayback(playbackSeconds, DateTimeOffset.UtcNow, Progress.IsEnabled))
                Progress.Value = playbackSeconds;
            UpdateTimelineLabels();
            Progress.Visibility = _view.Settings.IslandActivity.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            var empty = string.IsNullOrEmpty(_view.Title);
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            var showArtwork = !empty && _view.Settings.IslandActivity.ShowArtwork;
            ArtworkColumn.Width = new GridLength(showArtwork ? 100 : 0);
            ArtworkHost.Visibility = showArtwork ? Visibility.Visible : Visibility.Collapsed;
            TimelineRow.Visibility = ControlsRow.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            LyricsArea.Visibility = empty || !_view.Settings.Lyrics.Enabled ? Visibility.Collapsed : Visibility.Visible;
            TranslatedLyric.Visibility = string.IsNullOrWhiteSpace(_view.SecondaryLyricText) ? Visibility.Collapsed : Visibility.Visible;
            LyricsStatus.Visibility = string.IsNullOrWhiteSpace(_view.LyricsStatusText) ? Visibility.Collapsed : Visibility.Visible;
            NextLyricPreview.Visibility = string.IsNullOrWhiteSpace(_view.NextLyricText) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(NextLyric, $"{_view.NextLyricLabel}: {_view.NextLyricText}");
            if (!string.Equals(_lyricViewportText, _view.CurrentLyricText, StringComparison.Ordinal))
            {
                _lyricViewportText = _view.CurrentLyricText;
                CurrentLyricsViewport.ChangeView(null, 0, null, disableAnimation: true);
            }
            TimelineStatus.Visibility = string.IsNullOrWhiteSpace(_view.TimelineStatus) ? Visibility.Collapsed : Visibility.Visible;
            ControlError.Visibility = string.IsNullOrWhiteSpace(_view.ControlError) ? Visibility.Collapsed : Visibility.Visible;
            PlaybackStatus.Visibility = TimelineStatus.Visibility == Visibility.Visible || ControlError.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(PlayPause, _view.PlayPauseLabel);
            Spectrum.Visibility = _view.Settings.IslandActivity.ShowSpectrum && _view.HasSpectrumPresentation ? Visibility.Visible : Visibility.Collapsed;
            for (var index = 0; index < Spectrum.Children.Count; index++)
                ((Rectangle)Spectrum.Children[index]).Height = 2 + 22 * Math.Clamp(_view.Spectrum.Bands.ElementAtOrDefault(index), 0, 1);
        }
        finally { _updating = false; }
        NotifyTranslationVisibility();
    }
    private void NotifyTranslationVisibility()
    {
        var visible = IsTranslationActuallyVisible;
        var origin = _view?.LyricPresentation.Line?.TranslationOrigin ?? LyricsTranslationOrigin.None;
        if (_translationWasVisible == visible && _displayedTranslation == TranslatedLyric.Text && _displayedOrigin == origin) return;
        _translationWasVisible = visible;
        _displayedTranslation = TranslatedLyric.Text;
        _displayedOrigin = origin;
        TranslationVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }
    private void UpdateTimelineLabels()
    {
        if (_view is null) return;
        if (_seekInteraction.HeldSeconds is { } seconds)
        {
            ElapsedLabel.Text = FormatSeconds(seconds);
            RemainingLabel.Text = "-" + FormatSeconds(Math.Max(0, Progress.Maximum - seconds));
        }
        else
        {
            ElapsedLabel.Text = _view.ElapsedText;
            RemainingLabel.Text = _view.RemainingText;
        }
    }

    private static string FormatSeconds(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!CanRender || _updating || !double.IsFinite(args.NewValue)) return;
        var value = Math.Clamp(args.NewValue, Progress.Minimum, Progress.Maximum);
        if (_seekInteraction.IsPendingTarget(value)) return;
        _seekAcknowledgementTimer.Stop();
        _seekInteraction.Preview(value);
        UpdateTimelineLabels();
        if (_seekInteraction.IsDragging) return;

        // Track clicks and keyboard adjustments are committed after a short quiet period.
        // If this ValueChanged starts a pointer gesture, PointerPressed cancels the timer before it
        // can send a premature seek.
        _queuedSeekSeconds = value;
        _seekCommitTimer.Stop();
        _seekCommitTimer.Start();
    }

    private void OnSeekPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(Progress);
        if (!CanRender || !Progress.IsEnabled || (!point.IsInContact && !point.Properties.IsLeftButtonPressed)) return;
        if (_activePointerId is not null) return;
        _activePointerId = args.Pointer.PointerId;
        _seekAcknowledgementTimer.Stop();
        _seekCommitTimer.Stop();
        _queuedSeekSeconds = null;
        _seekInteraction.Begin(Progress.Value);
    }

    private void OnSeekPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_activePointerId == args.Pointer.PointerId) FinishPointerSeek(canceled: false);
    }

    private void OnSeekPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_activePointerId == args.Pointer.PointerId) FinishPointerSeek(canceled: true);
    }

    private void OnSeekPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_activePointerId != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(Progress);
        // Normal release may drop capture before the parent receives PointerReleased.
        // Complete it once; an interruption while still pressed cancels without seeking.
        FinishPointerSeek(point.IsInContact || point.Properties.IsLeftButtonPressed);
    }

    private void FinishPointerSeek(bool canceled)
    {
        _activePointerId = null;
        _seekCommitTimer.Stop();
        _queuedSeekSeconds = null;
        _seekInteraction.Preview(Progress.Value);
        var target = _seekInteraction.Complete(canceled, DateTimeOffset.UtcNow);
        if (target is { } seconds) ExecuteSeek(seconds);
        else RequestRender();
    }

    private void OnSeekCommitTimer(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!CanRender || _queuedSeekSeconds is not { } seconds || _seekInteraction.IsDragging) return;
        _queuedSeekSeconds = null;
        ExecuteSeek(_seekInteraction.Commit(seconds, DateTimeOffset.UtcNow));
    }

    private void ExecuteSeek(double seconds)
    {
        if (_view?.SeekCommand.CanExecute(seconds) == true)
        {
            _view.SeekCommand.Execute(seconds);
            // Paused players may reject a seek without producing another media event.
            _seekAcknowledgementTimer.Stop();
            _seekAcknowledgementTimer.Start();
            return;
        }

        _seekInteraction.RejectPending();
        RequestRender();
    }

    private void CancelSeekInteraction()
    {
        _seekAcknowledgementTimer.Stop();
        _seekCommitTimer.Stop();
        _queuedSeekSeconds = null;
        _activePointerId = null;
        _seekInteraction.Reset();
    }


}
