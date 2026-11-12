using System.ComponentModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DropSpace.App.Views.Island;

public sealed partial class MediaExpandedView : UserControl
{
    internal bool IsTranslationActuallyVisible => _presentationActive && IsLoaded && Visibility == Visibility.Visible &&
        LyricsArea.Visibility == Visibility.Visible && TranslatedLyric.Visibility == Visibility.Visible &&
        TranslatedLyric.Opacity > 0.01 && TranslatedLyric.ActualWidth > 0 && TranslatedLyric.ActualHeight > 0 &&
        !string.IsNullOrWhiteSpace(TranslatedLyric.Text) &&
        string.Equals(TranslatedLyric.Text, _view?.SecondaryLyricText, StringComparison.Ordinal);

    private MediaViewModel? _view;
    private readonly MediaRenderQueue _renderQueue = new();
    private readonly MediaSeekInteraction _seekInteraction = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _seekCommitTimer;
    private uint? _activePointerId;
    private double? _queuedSeekSeconds;
    private string _trackIdentity = string.Empty;
    private string _lyricViewportText = string.Empty;
    private bool _updating;
    private bool _presentationActive = true;
    public MediaExpandedView()
    {
        InitializeComponent();
        _seekCommitTimer = DispatcherQueue.CreateTimer();
        _seekCommitTimer.Interval = TimeSpan.FromMilliseconds(120);
        _seekCommitTimer.IsRepeating = false;
        _seekCommitTimer.Tick += OnSeekCommitTimer;
        // Observe the whole slider, including track presses, even when its template
        // handles pointer events. Never depend on finding a Thumb before layout.
        Progress.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekPointerPressed), true);
        Progress.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekPointerReleased), true);
        Progress.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnSeekPointerCanceled), true);
        Progress.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekPointerCaptureLost), true);
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
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
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MediaViewModel.CurrentLyricIndex) && CanRender)
        {
            CancelRender();
            Render();
        }
        else RequestRender();
    }
    internal void SetActive(bool active)
    {
        _presentationActive = active;
        if (CanRender) RequestRender();
        else
        {
            CancelRender();
            CancelSeekInteraction();
        }
    }
    private bool CanRender => _presentationActive && IsLoaded && Visibility == Visibility.Visible;
    private void RequestRender()
    {
        if (_renderQueue.Request(CanRender)) CompositionTarget.Rendering += OnRendering;
    }
    private void OnRendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnRendering;
        if (_renderQueue.BeginRender(CanRender)) Render();
    }
    private void CancelRender()
    {
        CompositionTarget.Rendering -= OnRendering;
        _renderQueue.Cancel();
    }
    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (CanRender) RequestRender();
        else
        {
            CancelRender();
            CancelSeekInteraction();
        }
    }
    private void Render()
    {
        if (_view is null) return;
        OriginalLyric.FontSize = _view.Settings.Lyrics.OriginalFontSize;
        TranslatedLyric.FontSize = _view.Settings.Lyrics.TranslationFontSize;
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
            var playbackSeconds = _view.ProgressSliderSeconds;
            if (_seekInteraction.ShouldApplyPlayback(Progress.IsEnabled))
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
            CurrentLyricsViewport.Visibility = LyricsArea.Visibility;
            TranslatedLyric.Visibility = string.IsNullOrWhiteSpace(_view.SecondaryLyricText) ? Visibility.Collapsed : Visibility.Visible;
            if (!string.Equals(_lyricViewportText, _view.CurrentLyricText, StringComparison.Ordinal))
            {
                _lyricViewportText = _view.CurrentLyricText;
                CurrentLyricsViewport.ChangeView(null, 0, null, disableAnimation: true);
            }
            AutomationProperties.SetName(PlayPause, _view.PlayPauseLabel);
            Spectrum.Visibility = _view.Settings.IslandActivity.ShowSpectrum && _view.HasSpectrumPresentation ? Visibility.Visible : Visibility.Collapsed;
            for (var index = 0; index < Spectrum.Children.Count; index++)
                ((Rectangle)Spectrum.Children[index]).Height = 2 + 22 * Math.Clamp(_view.Spectrum.Bands.ElementAtOrDefault(index), 0, 1);
        }
        finally { _updating = false; }
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
        var target = _seekInteraction.Complete(canceled || !CanRender);
        if (target is { } seconds) ExecuteSeek(seconds);
        else RequestRender();
    }

    private void OnSeekCommitTimer(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!CanRender || _queuedSeekSeconds is not { } seconds || _seekInteraction.IsDragging) return;
        _queuedSeekSeconds = null;
        ExecuteSeek(_seekInteraction.Commit(seconds));
    }

    private async void ExecuteSeek(double seconds)
    {
        var revision = _seekInteraction.Revision;
        if (CanRender && _view?.SeekCommand.CanExecute(seconds) == true)
            await _view.SeekCommand.ExecuteAsync(seconds);
        _seekInteraction.FinishRequest(revision);
        RequestRender();
    }

    private void CancelSeekInteraction()
    {
        _seekCommitTimer.Stop();
        _queuedSeekSeconds = null;
        _activePointerId = null;
        _seekInteraction.Reset();
    }


}

internal sealed class MediaSeekInteraction
{
    private double? _pendingSeconds;
    internal long Revision { get; private set; }
    public bool IsDragging { get; private set; }
    public bool IsPreviewing { get; private set; }
    public double PreviewSeconds { get; private set; }
    public double? HeldSeconds => IsDragging || IsPreviewing ? PreviewSeconds : _pendingSeconds;

    public void Begin(double seconds)
    {
        IsDragging = true;
        Preview(seconds);
    }

    public void Preview(double seconds)
    {
        Revision++;
        PreviewSeconds = Normalize(seconds);
        IsPreviewing = true;
        _pendingSeconds = null;
    }

    public double? Complete(bool canceled)
    {
        if (!IsDragging) return null;
        if (!canceled) return Commit(PreviewSeconds);
        Reset();
        return null;
    }

    public double Commit(double seconds)
    {
        Revision++;
        IsDragging = IsPreviewing = false;
        PreviewSeconds = Normalize(seconds);
        _pendingSeconds = PreviewSeconds;
        return PreviewSeconds;
    }

    public bool ShouldApplyPlayback(bool canSeek)
    {
        if (!canSeek && HeldSeconds is not null) Reset();
        return HeldSeconds is null;
    }

    public bool IsPendingTarget(double seconds) => _pendingSeconds is { } pending && Math.Abs(Normalize(seconds) - pending) <= 0.001;

    public void FinishRequest(long revision)
    {
        if (revision == Revision) _pendingSeconds = null;
    }

    public void Reset()
    {
        Revision++;
        _pendingSeconds = null;
        IsDragging = IsPreviewing = false;
        PreviewSeconds = 0;
    }

    private static double Normalize(double seconds) => double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
}
