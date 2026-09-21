using System.ComponentModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;

namespace DropSpace.App.Views.Island;

public sealed partial class MediaExpandedView : UserControl
{
    private MediaViewModel? _view;
    private bool _updating;
    public MediaExpandedView()
    {
        InitializeComponent();
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
    private void OnLoaded(object sender, RoutedEventArgs args) { if (_view is not null) _view.PropertyChanged += OnChanged; Render(); }
    private void OnUnloaded(object sender, RoutedEventArgs args) { if (_view is not null) _view.PropertyChanged -= OnChanged; }
    private void OnChanged(object? sender, PropertyChangedEventArgs args) => Render();
    private void Render()
    {
        if (_view is null) return;
        _updating = true;
        try
        {
            Progress.Maximum = _view.DurationSeconds;
            Progress.Value = _view.PositionEstimated ? 0 : Math.Clamp(_view.PositionSeconds, 0, Progress.Maximum);
            Progress.IsEnabled = _view.Session.CanSeek && !_view.PositionEstimated;
            Progress.Visibility = _view.Settings.IslandActivity.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            var empty = string.IsNullOrEmpty(_view.Title);
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            var showArtwork = !empty && _view.Settings.IslandActivity.ShowArtwork;
            ArtworkColumn.Width = new GridLength(showArtwork ? 100 : 0);
            ArtworkHost.Visibility = showArtwork ? Visibility.Visible : Visibility.Collapsed;
            TimelineRow.Visibility = ControlsRow.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            RepeatIcon.Symbol = _view.Session.RepeatMode == MediaRepeatMode.Track ? Symbol.RepeatOne : Symbol.RepeatAll;
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
        if (!_updating && _view?.SeekCommand.CanExecute(args.NewValue) == true)
            _view.SeekCommand.Execute(args.NewValue);
    }
}
