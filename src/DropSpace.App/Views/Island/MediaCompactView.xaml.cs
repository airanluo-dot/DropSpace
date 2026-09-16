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
    private double _primaryHeight = 28;
    private string? _measuredFontFamily;
    private double _measuredFontSize;
    private ushort _measuredFontWeight;
    private double _measuredRasterizationScale = 1;
    private readonly TextBlock _secondaryMeasure = new() { FontSize = 11, TextWrapping = TextWrapping.NoWrap };
    private XamlRoot? _xamlRoot;
    public double IdealIslandWidth { get; private set; } = 280;
    public double IdealIslandHeight { get; private set; } = 40;
    public event EventHandler? IdealWidthChanged;
    public MediaCompactView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => InvalidateTextMeasure();
        Layout.SizeChanged += (_, _) => InvalidateTextMeasure();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Subscribe();
        AttachXamlRoot();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        DetachXamlRoot();
        Unsubscribe();
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
    private void AttachXamlRoot()
    {
        var root = XamlRoot;
        if (ReferenceEquals(_xamlRoot, root)) return;
        DetachXamlRoot();
        _xamlRoot = root;
        if (_xamlRoot is not null) _xamlRoot.Changed += OnXamlRootChanged;
    }

    private void DetachXamlRoot()
    {
        if (_xamlRoot is not null) _xamlRoot.Changed -= OnXamlRootChanged;
        _xamlRoot = null;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        AttachXamlRoot();
        _textWidth = 0;
        InvalidateMeasure();
        Layout.InvalidateMeasure();
        Refresh();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MediaViewModel.Lyrics) or nameof(MediaViewModel.LyricsLines) or nameof(MediaViewModel.CurrentLyricIndex) or nameof(MediaViewModel.LyricsStatus) or nameof(MediaViewModel.Spectrum) or nameof(MediaViewModel.Settings) or nameof(MediaViewModel.Session) or nameof(MediaViewModel.IsReducedMotion)) Refresh();
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
        var fontFamily = BaseLine.FontFamily?.Source;
        var fontSize = BaseLine.FontSize;
        var fontWeight = BaseLine.FontWeight.Weight;
        var rasterizationScale = XamlRoot?.RasterizationScale ?? 1;
        var measureChanged = !string.Equals(_measuredFontFamily, fontFamily, StringComparison.Ordinal) ||
            _measuredFontSize != fontSize || _measuredFontWeight != fontWeight ||
            Math.Abs(_measuredRasterizationScale - rasterizationScale) > 0.001;
        if (BaseLine.Text != text || _textWidth == 0 || measureChanged)
        {
            BaseLine.Text = text; HighlightLine.Text = text;
            _textWidth = Measure(text);
            LyricCanvas.Width = _textWidth;
            _primaryHeight = Math.Max(28, _measure.DesiredSize.Height);
            LyricViewport.Height = _primaryHeight;
            _measuredFontFamily = fontFamily;
            _measuredFontSize = fontSize;
            _measuredFontWeight = fontWeight;
            _measuredRasterizationScale = rasterizationScale;
        }
        var secondary = settings.IslandActivity.ShowLyricsInCompact ? _view.SecondaryLyricText : null;
        SecondaryLine.Text = secondary ?? string.Empty;
        SecondaryLine.Visibility = string.IsNullOrWhiteSpace(secondary) ? Visibility.Collapsed : Visibility.Visible;
        _secondaryMeasure.FontFamily = SecondaryLine.FontFamily;
        _secondaryMeasure.FontSize = SecondaryLine.FontSize;
        _secondaryMeasure.FontWeight = SecondaryLine.FontWeight;
        _secondaryMeasure.Text = SecondaryLine.Text;
        _secondaryMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var secondaryHeight = SecondaryLine.Visibility == Visibility.Visible ? _secondaryMeasure.DesiredSize.Height : 0;
        IdealIslandHeight = Math.Max(40, _primaryHeight + secondaryHeight + 12);
        ArtworkHost.Visibility = settings.IslandActivity.ShowArtwork ? Visibility.Visible : Visibility.Collapsed;
        var spectrum = settings.IslandActivity.ShowSpectrum && _view.Spectrum.CaptureMode == AudioCaptureMode.ProcessLoopback;
        SpectrumBars.Visibility = spectrum ? Visibility.Visible : Visibility.Collapsed;
        var bands = new[] { Band0, Band1, Band2, Band3, Band4, Band5 };
        for (var index = 0; index < bands.Length; index++)
            bands[index].Height = 2 + 20 * Math.Clamp(_view.Spectrum.Bands.ElementAtOrDefault(index), 0, 1);
        var textWidth = settings.IslandActivity.CompactDynamicWidth ? Math.Clamp(Math.Max(_textWidth, _secondaryMeasure.DesiredSize.Width), 80, settings.Lyrics.ScrollingMaxWidth) : 180;
        var width = 28 + textWidth + (settings.IslandActivity.ShowArtwork ? 36 : 12) + (spectrum ? 45 : 12);
        width = Math.Clamp(width, 180, 460);
        if (Math.Abs(width - IdealIslandWidth) > 0.5 || Math.Abs(previousHeight - IdealIslandHeight) > 0.5)
        { IdealIslandWidth = width; IdealWidthChanged?.Invoke(this, EventArgs.Empty); }
        RefreshHighlight();
    }
    private void InvalidateTextMeasure()
    {
        _textWidth = 0;
        _measuredFontFamily = null;
        Refresh();
    }
    private void RefreshHighlight()
    {
        if (_view is null) return;
        var frame = _view.Lyrics;
        var highlight = _textWidth;
        if (_view.Settings.Lyrics.Enabled && _view.Settings.Lyrics.WordSyncedHighlighting && frame.Line is { Words.Count: > 0 } line && _view.Settings.IslandActivity.ShowLyricsInCompact)
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
        if (_view.Settings.Lyrics.Enabled && _view.Settings.IslandActivity.ShowLyricsInCompact && _view.Settings.Lyrics.Scrolling && !_view.IsReducedMotion)
        {
            scroll = frame.Line is { Words.Count: > 0 } ? highlight - LyricViewport.ActualWidth * 0.6 :
                frame.Line is { } current
                    ? (_view.Position - _view.Session.Timeline.Start - current.Start).TotalSeconds * 24 - LyricViewport.ActualWidth / 3
                    : 0;
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
