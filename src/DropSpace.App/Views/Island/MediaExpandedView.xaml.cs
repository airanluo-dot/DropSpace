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
    private MediaViewModel? _view;
    private readonly MediaSeekInteraction _seekInteraction = new(TimeSpan.FromSeconds(2));
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _seekCommitTimer;
    private uint? _activePointerId;
    private double? _queuedSeekSeconds;
    private string _trackIdentity = string.Empty;
    private bool _updating;
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
    }
    public MediaViewModel? ViewModel
    {
        get => _view;
        set
        {
            if (_view is not null) _view.PropertyChanged -= OnChanged;
            _view = value; DataContext = value;
            if (IsLoaded && _view is not null) _view.PropertyChanged += OnChanged;
            Render();
        }
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_view is not null) _view.PropertyChanged += OnChanged;
        Render();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_view is not null) _view.PropertyChanged -= OnChanged;
        CancelSeekInteraction();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args) => Render();
    private void Render()
    {
        if (_view is null) return;
        OriginalLyric.FontSize = _view.Settings.Lyrics.OriginalFontSize + 2;
        TranslatedLyric.FontSize = _view.Settings.Lyrics.TranslationFontSize;
        // Keep the established compact panel bounds; full lyrics remain available on the Music page.
        OriginalLyric.MaxLines = OriginalLyric.FontSize > 22 ? 1 : 2;
        TranslatedLyric.MaxLines = TranslatedLyric.FontSize > 18 ? 1 : 2;
        _updating = true;
        try
        {
            var trackIdentity = _view.Session.TrackIdentity;
            if (!string.Equals(_trackIdentity, trackIdentity, StringComparison.Ordinal))
            {
                _trackIdentity = trackIdentity;
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
            AutomationProperties.SetName(PlayPause, _view.PlayPauseLabel);
            Spectrum.Visibility = _view.Settings.IslandActivity.ShowSpectrum && _view.Spectrum.CaptureMode == AudioCaptureMode.ProcessLoopback ? Visibility.Visible : Visibility.Collapsed;
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
        if (_updating || !double.IsFinite(args.NewValue)) return;
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
        if (!Progress.IsEnabled || (!point.IsInContact && !point.Properties.IsLeftButtonPressed)) return;
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
        var target = _seekInteraction.Complete(canceled, DateTimeOffset.UtcNow);
        if (target is { } seconds) ExecuteSeek(seconds);
        else Render();
    }

    private void OnSeekCommitTimer(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_queuedSeekSeconds is not { } seconds || _seekInteraction.IsDragging) return;
        _queuedSeekSeconds = null;
        ExecuteSeek(_seekInteraction.Commit(seconds, DateTimeOffset.UtcNow));
    }

    private void ExecuteSeek(double seconds)
    {
        if (_view?.SeekCommand.CanExecute(seconds) == true)
        {
            _view.SeekCommand.Execute(seconds);
            return;
        }

        _seekInteraction.RejectPending();
        Render();
    }

    private void CancelSeekInteraction()
    {
        _seekCommitTimer.Stop();
        _queuedSeekSeconds = null;
        _activePointerId = null;
        _seekInteraction.Reset();
    }


}

internal sealed class MediaSeekInteraction(TimeSpan acknowledgementTimeout)
{
    private const double AcknowledgementToleranceSeconds = 1;
    private const double InputEqualityToleranceSeconds = 0.001;
    private double? _pendingSeconds;
    private DateTimeOffset _pendingUntil;

    public bool IsDragging { get; private set; }
    public bool IsPreviewing { get; private set; }
    public double PreviewSeconds { get; private set; }
    public double? HeldSeconds => IsDragging || IsPreviewing ? PreviewSeconds : _pendingSeconds;

    public void Begin(double seconds)
    {
        IsDragging = true;
        IsPreviewing = true;
        PreviewSeconds = Normalize(seconds);
        _pendingSeconds = null;
    }

    public void Preview(double seconds)
    {
        PreviewSeconds = Normalize(seconds);
        IsPreviewing = true;
    }

    public double? Complete(bool canceled, DateTimeOffset now)
    {
        if (!IsDragging) return null;
        IsDragging = false;
        IsPreviewing = false;
        if (canceled)
        {
            _pendingSeconds = null;
            return null;
        }
        return Commit(PreviewSeconds, now);
    }

    public double Commit(double seconds, DateTimeOffset now)
    {
        IsDragging = false;
        IsPreviewing = false;
        PreviewSeconds = Normalize(seconds);
        _pendingSeconds = PreviewSeconds;
        _pendingUntil = now + acknowledgementTimeout;
        return PreviewSeconds;
    }

    public bool ShouldApplyPlayback(double seconds, DateTimeOffset now, bool canSeek)
    {
        if (IsDragging || IsPreviewing) return false;
        if (_pendingSeconds is not { } pending) return true;
        if (now < _pendingUntil && Math.Abs(Normalize(seconds) - pending) > AcknowledgementToleranceSeconds)
            return false;
        _pendingSeconds = null;
        return true;
    }

    public bool IsPendingTarget(double seconds) =>
        _pendingSeconds is { } pending && Math.Abs(Normalize(seconds) - pending) <= InputEqualityToleranceSeconds;

    public void RejectPending() { _pendingSeconds = null; IsPreviewing = false; }

    public void Reset()
    {
        IsDragging = false;
        IsPreviewing = false;
        PreviewSeconds = 0;
        _pendingSeconds = null;
    }

    private static double Normalize(double seconds) => double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
}
