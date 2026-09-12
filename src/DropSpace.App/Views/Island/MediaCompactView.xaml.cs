using System.ComponentModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DropSpace.App.Views.Island;

public sealed partial class MediaCompactView : UserControl
{
    private MediaViewModel? _view;
    private bool _subscribed;
    private readonly TextBlock _measure = new() { FontSize = 13, TextWrapping = TextWrapping.NoWrap };
    private double _textWidth;
    public double IdealIslandWidth { get; private set; } = 280;
    public double IdealIslandHeight { get; private set; } = 40;
    public event EventHandler? IdealWidthChanged;
    public MediaCompactView()
    {
        InitializeComponent();
        Loaded += (_, _) => { Subscribe(); Refresh(); };
        Unloaded += (_, _) => Unsubscribe();
    }
    public MediaViewModel? ViewModel
    {
        get => _view;
        set
        {
            Unsubscribe(); _view = value; Layout.DataContext = value;
            if (IsLoaded) Subscribe();
            Refresh();
        }
    }
    private void Subscribe() { if (!_subscribed && _view is not null) { _view.PropertyChanged += OnChanged; _subscribed = true; } }
    private void Unsubscribe() { if (_subscribed && _view is not null) _view.PropertyChanged -= OnChanged; _subscribed = false; }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MediaViewModel.Lyrics) or nameof(MediaViewModel.Spectrum) or nameof(MediaViewModel.Settings) or nameof(MediaViewModel.Session) or nameof(MediaViewModel.IsReducedMotion)) Refresh();
    }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ViewportClip.Rect = new Rect(0, 0, Math.Max(0, args.NewSize.Width), Math.Max(0, args.NewSize.Height));
        RefreshHighlight();
    }
    private void Refresh()
    {
        if (_view is null || BaseLine is null) return;
        var previousHeight = IdealIslandHeight;
        var settings = _view.Settings;
        var text = settings.IslandActivity.ShowLyricsInCompact && settings.Lyrics.Enabled ? _view.CurrentLyricText : _view.Title;
        if (BaseLine.Text != text || _textWidth == 0)
        {
            BaseLine.Text = text; HighlightLine.Text = text;
            _textWidth = Measure(text);
            LyricCanvas.Width = _textWidth;
            LyricViewport.Height = Math.Max(28, _measure.DesiredSize.Height);
            IdealIslandHeight = Math.Max(40, _measure.DesiredSize.Height + 12);
        }
        ArtworkHost.Visibility = settings.IslandActivity.ShowArtwork ? Visibility.Visible : Visibility.Collapsed;
        var spectrum = settings.IslandActivity.ShowSpectrum && _view.Spectrum.CaptureMode == AudioCaptureMode.ProcessLoopback;
        SpectrumBars.Visibility = spectrum ? Visibility.Visible : Visibility.Collapsed;
        var bands = new[] { Band0, Band1, Band2, Band3, Band4, Band5 };
        for (var index = 0; index < bands.Length; index++)
            bands[index].Height = 2 + 20 * Math.Clamp(_view.Spectrum.Bands.ElementAtOrDefault(index), 0, 1);
        var textWidth = settings.IslandActivity.CompactDynamicWidth ? Math.Clamp(_textWidth, 80, settings.Lyrics.ScrollingMaxWidth) : 180;
        var width = 28 + textWidth + (settings.IslandActivity.ShowArtwork ? 36 : 12) + (spectrum ? 45 : 12);
        width = Math.Clamp(width, 180, 460);
        if (Math.Abs(width - IdealIslandWidth) > 0.5 || Math.Abs(previousHeight - IdealIslandHeight) > 0.5)
        { IdealIslandWidth = width; IdealWidthChanged?.Invoke(this, EventArgs.Empty); }
        RefreshHighlight();
    }
    private void RefreshHighlight()
    {
        if (_view is null) return;
        var frame = _view.Lyrics;
        var highlight = _textWidth;
        if (_view.Settings.Lyrics.WordSyncedHighlighting && frame.Line is { Words.Count: > 0 } line && _view.Settings.IslandActivity.ShowLyricsInCompact)
        {
            highlight = 0;
            if (frame.WordIndex >= 0 && frame.WordIndex < line.Words.Count)
            {
                var before = string.Concat(line.Words.Take(frame.WordIndex).Select(word => word.Text));
                var beforeWidth = Measure(before);
                highlight = beforeWidth + (Measure(before + line.Words[frame.WordIndex].Text) - beforeWidth) * frame.WordProgress;
            }
        }
        HighlightClip.Rect = new Rect(0, 0, Math.Max(0, highlight), Math.Max(28, BaseLine.ActualHeight));
        var overflow = Math.Max(0, _textWidth - LyricViewport.ActualWidth);
        var scroll = 0d;
        if (_view.Settings.Lyrics.Scrolling && !_view.IsReducedMotion)
        {
            scroll = frame.Line is { Words.Count: > 0 } ? highlight - LyricViewport.ActualWidth * 0.6 :
                frame.Line is { } current ? (_view.Position - current.Start).TotalSeconds * 24 - LyricViewport.ActualWidth / 3 : 0;
        }
        LyricTranslation.TranslateX = -Math.Clamp(scroll, 0, overflow);
    }
    private double Measure(string text)
    {
        _measure.FontFamily = BaseLine.FontFamily; _measure.FontSize = BaseLine.FontSize; _measure.FontWeight = BaseLine.FontWeight;
        _measure.Text = text; _measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _measure.DesiredSize.Width;
    }
}
