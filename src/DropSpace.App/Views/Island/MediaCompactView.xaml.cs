using System.ComponentModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
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
    private double _secondaryTextWidth;
    private readonly LyricsMarqueeSession _secondaryMarquee = new();
    private bool _translationWasVisible;
    private string _displayedTranslation = string.Empty;
    private LyricsTranslationOrigin _displayedOrigin;
    private bool _secondaryMeasureInvalid = true;
    private double _interludeOpacity;
    private long _lastPresentationTick;
    private double _primaryHeight = 28;
    private string? _measuredFontFamily;
    private double _measuredFontSize;
    private ushort _measuredFontWeight;
    private double _measuredRasterizationScale = 1;
    private readonly TextBlock _secondaryMeasure = new() { FontSize = 11, TextWrapping = TextWrapping.NoWrap };
    private XamlRoot? _xamlRoot;
    public double IdealIslandWidth { get; private set; } = 280;
    public double IdealIslandHeight { get; private set; } = 40;
    // The glow asks the rendered text surface, not only whether a translation exists.
    internal bool IsTranslationActuallyVisible => IsLoaded && Visibility == Visibility.Visible &&
        SecondaryViewport.Visibility == Visibility.Visible && SecondaryViewport.ActualWidth > 0 && SecondaryViewport.ActualHeight > 0 &&
        SecondaryLine.Visibility == Visibility.Visible && SecondaryLine.Opacity > 0.01 &&
        SecondaryLine.ActualWidth > 0 && SecondaryLine.ActualHeight > 0 &&
        !string.IsNullOrWhiteSpace(SecondaryLine.Text) && TranslationIntersects(this) &&
        string.Equals(SecondaryLine.Text, LyricsDisplayPolicy.CompactText(_view?.SecondaryLyricText), StringComparison.Ordinal);
    internal bool IsTranslationVisibleWithin(FrameworkElement body) =>
        IsTranslationActuallyVisible && TranslationIntersects(body);

    private bool TranslationIntersects(FrameworkElement viewport)
    {
        var textBounds = SecondaryLine.TransformToVisual(viewport).TransformBounds(
            new Rect(0, 0, SecondaryLine.ActualWidth, SecondaryLine.ActualHeight));
        var clipBounds = SecondaryViewport.TransformToVisual(viewport).TransformBounds(
            new Rect(0, 0, SecondaryViewport.ActualWidth, SecondaryViewport.ActualHeight));
        var left = Math.Max(textBounds.Left, clipBounds.Left);
        var top = Math.Max(textBounds.Top, clipBounds.Top);
        return LyricsDisplayPolicy.IntersectsViewport(left, top,
            Math.Min(textBounds.Right, clipBounds.Right) - left,
            Math.Min(textBounds.Bottom, clipBounds.Bottom) - top, viewport.ActualWidth, viewport.ActualHeight);
    }
    public event EventHandler? IdealWidthChanged;
    public event EventHandler? TranslationVisibilityChanged;
    public MediaCompactView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => InvalidateTextMeasure();
        LayoutUpdated += (_, _) => NotifyTranslationVisibility();
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
        _secondaryMarquee.Reset();
        NotifyTranslationVisibility();
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
        InvalidateMeasure();
        Layout.InvalidateMeasure();
        InvalidateTextMeasure();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MediaViewModel.Lyrics) or nameof(MediaViewModel.LyricsLines) or nameof(MediaViewModel.CurrentLyricIndex) or nameof(MediaViewModel.LyricsStatus) or nameof(MediaViewModel.Spectrum) or nameof(MediaViewModel.Settings) or nameof(MediaViewModel.Session) or nameof(MediaViewModel.IsReducedMotion) or nameof(MediaViewModel.Position)) Refresh();
    }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ViewportClip.Rect = new Rect(0, 0, Math.Max(0, args.NewSize.Width), Math.Max(0, args.NewSize.Height));
        RefreshHighlight();
    }
    private void OnSecondaryViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        SecondaryViewportClip.Rect = new Rect(0, 0, Math.Max(0, args.NewSize.Width), Math.Max(0, args.NewSize.Height));
        RefreshHighlight();
        NotifyTranslationVisibility();
    }
    private void Refresh()
    {
        if (_view is null || BaseLine is null) return;
        var previousHeight = IdealIslandHeight;
        var settings = _view.Settings;
        var showDots = settings.Lyrics.Enabled && settings.IslandActivity.ShowLyricsInCompact && _view.LyricPresentation.IsInterlude;
        BaseLine.FontSize = settings.IslandActivity.ShowLyricsInCompact && settings.Lyrics.Enabled ? settings.Lyrics.OriginalFontSize : 13;
        HighlightLine.FontSize = BaseLine.FontSize;
        SecondaryLine.FontSize = settings.Lyrics.TranslationFontSize;
        var text = showDots ? string.Empty : LyricsDisplayPolicy.CompactText(settings.IslandActivity.ShowLyricsInCompact && settings.Lyrics.Enabled
            ? _view.CurrentLyricText : _view.Title);
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
            LyricCanvas.Height = _measure.DesiredSize.Height;
            Canvas.SetTop(LyricCanvas, Math.Max(0, (_primaryHeight - LyricCanvas.Height) / 2));
            _measuredFontFamily = fontFamily;
            _measuredFontSize = fontSize;
            _measuredFontWeight = fontWeight;
            _measuredRasterizationScale = rasterizationScale;
        }
        var secondary = showDots ? string.Empty : LyricsDisplayPolicy.CompactText(settings.IslandActivity.ShowLyricsInCompact ? _view.SecondaryLyricText : null);
        if (_secondaryMeasureInvalid || SecondaryLine.Text != secondary ||
            _secondaryMeasure.FontSize != SecondaryLine.FontSize || measureChanged)
        {
            SecondaryLine.Text = secondary;
            _secondaryMeasure.FontFamily = SecondaryLine.FontFamily;
            _secondaryMeasure.FontSize = SecondaryLine.FontSize;
            _secondaryMeasure.FontWeight = SecondaryLine.FontWeight;
            _secondaryMeasure.FontStyle = SecondaryLine.FontStyle;
            _secondaryMeasure.CharacterSpacing = SecondaryLine.CharacterSpacing;
            _secondaryMeasure.Language = SecondaryLine.Language;
            _secondaryMeasure.FlowDirection = SecondaryLine.FlowDirection;
            _secondaryMeasure.IsTextScaleFactorEnabled = SecondaryLine.IsTextScaleFactorEnabled;
            _secondaryMeasure.Text = SecondaryLine.Text;
            _secondaryMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _secondaryTextWidth = _secondaryMeasure.DesiredSize.Width;
            SecondaryLine.Width = _secondaryTextWidth;
            SecondaryViewport.Height = _secondaryMeasure.DesiredSize.Height;
            _secondaryMeasureInvalid = false;
        }
        SecondaryLine.Visibility = string.IsNullOrWhiteSpace(secondary) ? Visibility.Collapsed : Visibility.Visible;
        SecondaryViewport.Visibility = SecondaryLine.Visibility;
        var secondaryHeight = SecondaryLine.Visibility == Visibility.Visible ? _secondaryMeasure.DesiredSize.Height : 0;
        IdealIslandHeight = showDots ? 40 : IslandGeometry.MusicCompactHeight(_primaryHeight + secondaryHeight + 12);
        LyricViewport.Height = showDots ? 28 : _primaryHeight;
        ArtworkHost.Visibility = settings.IslandActivity.ShowArtwork ? Visibility.Visible : Visibility.Collapsed;
        var spectrum = settings.IslandActivity.ShowSpectrum && _view.Spectrum.CaptureMode == AudioCaptureMode.ProcessLoopback;
        SpectrumBars.Visibility = spectrum ? Visibility.Visible : Visibility.Collapsed;
        var bands = new[] { Band0, Band1, Band2, Band3, Band4, Band5 };
        for (var index = 0; index < bands.Length; index++)
            bands[index].Height = 2 + 20 * Math.Clamp(_view.Spectrum.Bands.ElementAtOrDefault(index), 0, 1);
        var textWidth = settings.IslandActivity.CompactDynamicWidth ? Math.Clamp(Math.Max(_textWidth, _secondaryMeasure.DesiredSize.Width), 80, settings.Lyrics.ScrollingMaxWidth) : 180;
        var width = 28 + textWidth + (settings.IslandActivity.ShowArtwork ? 36 : 12) + (spectrum ? 45 : 12);
        width = showDots ? 280 : Math.Clamp(width, 180, 460);
        if (Math.Abs(width - IdealIslandWidth) > 0.5 || Math.Abs(previousHeight - IdealIslandHeight) > 0.5)
        { IdealIslandWidth = width; IdealWidthChanged?.Invoke(this, EventArgs.Empty); }
        RefreshHighlight();
        RefreshInterlude();
        NotifyTranslationVisibility();
    }
    private void RefreshInterlude()
    {
        if (_view is null) return;
        var presentation = _view.LyricPresentation;
        var enabled = _view.Settings.Lyrics.Enabled && _view.Settings.IslandActivity.ShowLyricsInCompact;
        var target = enabled && presentation.IsInterlude ? 1d : 0d;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = _lastPresentationTick == 0 ? 1 : System.Diagnostics.Stopwatch.GetElapsedTime(_lastPresentationTick, now).TotalSeconds;
        _lastPresentationTick = now;
        var amount = !_view.IsPlaying || _view.IsReducedMotion ? 1 : Math.Clamp(elapsed / 0.167, 0, 1);
        _interludeOpacity += Math.Clamp(target - _interludeOpacity, -amount, amount);
        InterludeDots.Visibility = _interludeOpacity > 0 ? Visibility.Visible : Visibility.Collapsed;
        InterludeDots.Opacity = _interludeOpacity;
        LyricCanvas.Opacity = 1 - _interludeOpacity;
        SecondaryLine.Opacity = 1 - _interludeOpacity;
        var phase = _view.Position.TotalSeconds * Math.PI;
        InterludeDot0.Opacity = _view.IsReducedMotion ? 0.65 : 0.55 + 0.35 * Math.Sin(phase);
        InterludeDot1.Opacity = _view.IsReducedMotion ? 0.65 : 0.55 + 0.35 * Math.Sin(phase - 0.5);
        InterludeDot2.Opacity = _view.IsReducedMotion ? 0.65 : 0.55 + 0.35 * Math.Sin(phase - 1);
    }
    private void InvalidateTextMeasure()
    {
        _textWidth = 0;
        _measuredFontFamily = null;
        _secondaryMeasureInvalid = true;
        _secondaryMarquee.Reset();
        Refresh();
    }
    private void RefreshHighlight()
    {
        if (_view is null) return;
        var frame = _view.Lyrics;
        var highlight = _textWidth;
        var presentation = _view.LyricPresentation;
        var effectivePosition = (_view.Position - _view.Session.Timeline.Start).TotalMilliseconds +
            Math.Clamp(_view.Settings.Lyrics.DelayMilliseconds, -30_000, 30_000);
        if (presentation.IsWaiting && presentation.Line is { } waiting && effectivePosition < waiting.Start.TotalMilliseconds)
            highlight = 0;
        if (_view.Settings.Lyrics.Enabled && _view.Settings.Lyrics.WordSyncedHighlighting && frame.Line is { Words.Count: > 0 } line && _view.Settings.IslandActivity.ShowLyricsInCompact)
        {
            highlight = 0;
            if (frame.WordIndex >= 0 && frame.WordIndex < line.Words.Count)
            {
            var before = LyricsDisplayPolicy.CompactText(string.Concat(line.Words.Take(frame.WordIndex).Select(word => word.Text)));
            var beforeWidth = Measure(before);
            highlight = beforeWidth + (Measure(LyricsDisplayPolicy.CompactText(before + line.Words[frame.WordIndex].Text)) - beforeWidth) * frame.WordProgress;
            }
        }
        HighlightClip.Rect = new Rect(0, 0, Math.Max(0, highlight), Math.Max(28, BaseLine.ActualHeight));
        var scroll = 0d;
        if (_view.Settings.Lyrics.Enabled && _view.Settings.IslandActivity.ShowLyricsInCompact && _view.Settings.Lyrics.Scrolling && !_view.IsReducedMotion)
        {
            scroll = LyricsDisplayPolicy.CompactScrollOffset(frame.Line,
                _view.Position - _view.Session.Timeline.Start, _view.Settings.Lyrics.DelayMilliseconds,
                _view.Settings.Lyrics.WordSyncedHighlighting, highlight, _textWidth, LyricViewport.ActualWidth);
        }
        LyricTranslation.TranslateX = -scroll;
        SecondaryTranslation.TranslateX = -_secondaryMarquee.Update(new(
            _view.Session.TrackIdentity, presentation.Line?.Start.Ticks ?? 0, SecondaryLine.Text,
            SecondaryLine.FontSize, XamlRoot?.RasterizationScale ?? 1, _secondaryTextWidth, SecondaryViewport.ActualWidth,
            _view.Settings.Lyrics.Enabled && _view.Settings.IslandActivity.ShowLyricsInCompact && _view.Settings.Lyrics.Scrolling,
            _view.IsReducedMotion), _view.Position);
    }
    private void NotifyTranslationVisibility()
    {
        var visible = IsTranslationActuallyVisible;
        var origin = _view?.LyricPresentation.Line?.TranslationOrigin ?? LyricsTranslationOrigin.None;
        if (_translationWasVisible == visible && _displayedTranslation == SecondaryLine.Text && _displayedOrigin == origin) return;
        _translationWasVisible = visible;
        _displayedTranslation = SecondaryLine.Text;
        _displayedOrigin = origin;
        TranslationVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }
    private double Measure(string text)
    {
        _measure.FontFamily = BaseLine.FontFamily; _measure.FontSize = BaseLine.FontSize; _measure.FontWeight = BaseLine.FontWeight;
        _measure.FontStyle = BaseLine.FontStyle; _measure.CharacterSpacing = BaseLine.CharacterSpacing;
        _measure.Language = BaseLine.Language; _measure.FlowDirection = BaseLine.FlowDirection;
        _measure.IsTextScaleFactorEnabled = BaseLine.IsTextScaleFactorEnabled;
        _measure.Text = text; _measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _measure.DesiredSize.Width;
    }
}
