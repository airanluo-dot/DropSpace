using System.ComponentModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DropSpace.App.Views.Island;

public sealed partial class MediaExpandedView : UserControl
{
    private MediaViewModel? _view;
    private readonly MediaSeekInteraction _seekInteraction = new(TimeSpan.FromSeconds(2));
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _seekCommitTimer;
    private Thumb? _progressThumb;
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
        HookProgressThumb();
        Render();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_view is not null) _view.PropertyChanged -= OnChanged;
        UnhookProgressThumb();
        CancelSeekInteraction();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args) => Render();
    private void Render()
    {
        if (_view is null) return;
        _updating = true;
        try
        {
            var trackIdentity = _view.Session.TrackIdentity;
            if (!string.Equals(_trackIdentity, trackIdentity, StringComparison.Ordinal))
            {
                _trackIdentity = trackIdentity;
                CancelSeekInteraction();
            }
            Progress.Maximum = _view.DurationSeconds;
            Progress.IsEnabled = _view.Session.CanSeek && !_view.PositionEstimated;
            var playbackSeconds = _view.PositionEstimated ? 0 : Math.Clamp(_view.PositionSeconds, 0, Progress.Maximum);
            if (_seekInteraction.ShouldApplyPlayback(playbackSeconds, DateTimeOffset.UtcNow, Progress.IsEnabled))
                Progress.Value = playbackSeconds;
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
    private void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_updating || !double.IsFinite(args.NewValue)) return;
        var value = Math.Clamp(args.NewValue, Progress.Minimum, Progress.Maximum);
        _seekInteraction.Preview(value);
        if (_seekInteraction.IsDragging || _seekInteraction.IsPendingTarget(value)) return;

        // Track clicks and keyboard adjustments are committed after a short quiet period.
        // If this ValueChanged starts a thumb drag, DragStarted cancels the timer before it
        // can send a premature seek.
        _queuedSeekSeconds = value;
        _seekCommitTimer.Stop();
        _seekCommitTimer.Start();
    }

    private void HookProgressThumb()
    {
        Progress.ApplyTemplate();
        var thumb = FindDescendant<Thumb>(Progress);
        if (ReferenceEquals(thumb, _progressThumb)) return;
        UnhookProgressThumb();
        _progressThumb = thumb;
        if (_progressThumb is null) return;
        _progressThumb.DragStarted += OnSeekDragStarted;
        _progressThumb.DragCompleted += OnSeekDragCompleted;
    }

    private void UnhookProgressThumb()
    {
        if (_progressThumb is null) return;
        _progressThumb.DragStarted -= OnSeekDragStarted;
        _progressThumb.DragCompleted -= OnSeekDragCompleted;
        _progressThumb = null;
    }

    private void OnSeekDragStarted(object sender, DragStartedEventArgs args)
    {
        if (!Progress.IsEnabled) return;
        _seekCommitTimer.Stop();
        _queuedSeekSeconds = null;
        _seekInteraction.Begin(Progress.Value);
    }

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs args)
    {
        _seekCommitTimer.Stop();
        _queuedSeekSeconds = null;
        _seekInteraction.Preview(Progress.Value);
        var target = _seekInteraction.Complete(args.Canceled, DateTimeOffset.UtcNow);
        if (target is { } seconds) ExecuteSeek(seconds);
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
        _seekInteraction.Reset();
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}

internal sealed class MediaSeekInteraction(TimeSpan acknowledgementTimeout)
{
    private const double AcknowledgementToleranceSeconds = 1;
    private const double InputEqualityToleranceSeconds = 0.001;
    private double? _pendingSeconds;
    private DateTimeOffset _pendingUntil;

    public bool IsDragging { get; private set; }
    public double PreviewSeconds { get; private set; }

    public void Begin(double seconds)
    {
        IsDragging = true;
        PreviewSeconds = Normalize(seconds);
        _pendingSeconds = null;
    }

    public void Preview(double seconds) => PreviewSeconds = Normalize(seconds);

    public double? Complete(bool canceled, DateTimeOffset now)
    {
        if (!IsDragging) return null;
        IsDragging = false;
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
        PreviewSeconds = Normalize(seconds);
        _pendingSeconds = PreviewSeconds;
        _pendingUntil = now + acknowledgementTimeout;
        return PreviewSeconds;
    }

    public bool ShouldApplyPlayback(double seconds, DateTimeOffset now, bool canSeek)
    {
        if (IsDragging) return false;
        if (_pendingSeconds is not { } pending) return true;
        if (canSeek && now < _pendingUntil && Math.Abs(Normalize(seconds) - pending) > AcknowledgementToleranceSeconds)
            return false;
        _pendingSeconds = null;
        return true;
    }

    public bool IsPendingTarget(double seconds) =>
        _pendingSeconds is { } pending && Math.Abs(Normalize(seconds) - pending) <= InputEqualityToleranceSeconds;

    public void RejectPending() => _pendingSeconds = null;

    public void Reset()
    {
        IsDragging = false;
        PreviewSeconds = 0;
        _pendingSeconds = null;
    }

    private static double Normalize(double seconds) => double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
}
