using System.ComponentModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DropSpace.App.Views.Island;

public sealed partial class MediaCompactView : UserControl
{
    private MediaViewModel? _view;
    private bool _subscribed;
    private readonly MediaRenderQueue _renderQueue = new();
    private EventHandler<object>? _renderHandler;
    private bool _presentationActive = true;
    internal long RefreshCount { get; private set; }
    private readonly TextBlock _measure = new() { FontSize = 13, TextWrapping = TextWrapping.NoWrap };
    private double _textWidth;
    private double _secondaryTextWidth;
    private double _secondaryHeight;
    private double _secondaryFontSize;
    private bool _layoutPending;
    private bool _geometrySettled = true;
    private readonly Dictionary<string, double> _prefixWidths = new(StringComparer.Ordinal);
    private string? _trackIdentity;
    private long _lineStartTicks;
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
    private XamlRoot? _xamlRoot;
    public double IdealIslandWidth { get; private set; } = 280;
    public double IdealIslandHeight { get; private set; } = 40;
    private double _availableWidth = 560;
    public void SetAvailableWidth(double value)
    {
        value = Math.Max(1, value);
        if (Math.Abs(value - _availableWidth) < 0.5) return;
        _availableWidth = value;
        RequestRefresh();
    }
    // The glow asks the rendered text surface, not only whether a translation exists.
    internal bool IsTranslationActuallyVisible => _presentationActive && IsLoaded && Visibility == Visibility.Visible &&
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
        LayoutUpdated += (_, _) =>
        {
            if (_layoutPending)
            {
                _layoutPending = false;
                RefreshHighlight();
            }
            NotifyTranslationVisibility();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        foreach (var line in new[] { BaseLine, SecondaryLine })
        foreach (var property in new[] { TextBlock.FontFamilyProperty, TextBlock.FontWeightProperty,
            TextBlock.FontStyleProperty, TextBlock.FontStretchProperty, TextBlock.CharacterSpacingProperty,
            TextBlock.IsTextScaleFactorEnabledProperty, FrameworkElement.LanguageProperty, FrameworkElement.FlowDirectionProperty })
            line.RegisterPropertyChangedCallback(property, (_, _) => InvalidateTextMeasure());
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Subscribe();
        AttachXamlRoot();
        InvalidateTextMeasure();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        DetachXamlRoot();
        Unsubscribe();
        CancelRefresh();
        ResetScroll();
        NotifyTranslationVisibility();
    }
    public MediaViewModel? ViewModel
    {
        get => _view;
        set
        {
            Unsubscribe(); _view = value; Layout.DataContext = value;
            if (IsLoaded) Subscribe();
            RequestRefresh();
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
        if (args.PropertyName is nameof(MediaViewModel.Lyrics) or nameof(MediaViewModel.LyricsLines) or nameof(MediaViewModel.CurrentLyricIndex) or nameof(MediaViewModel.LyricsStatus) or nameof(MediaViewModel.Spectrum) or nameof(MediaViewModel.Settings) or nameof(MediaViewModel.Session) or nameof(MediaViewModel.IsReducedMotion) or nameof(MediaViewModel.Position)) RequestRefresh();
    }

    // The owning host supplies ancestor visibility. A collapsed CompactPanel does
    // not unload this control, and its own Visibility may still be Visible.
    internal void SetActive(bool active)
    {
        if (_presentationActive == active) return;
        _presentationActive = active;
        if (CanRender) RequestRefresh();
        else { CancelRefresh(); ResetScroll(); }
        NotifyTranslationVisibility();
    }

    internal void RefreshForPresentation()
    {
        CancelRefresh();
        if (_renderQueue.BeginImmediateRender()) Refresh();
    }

    private bool CanRender => IsLoaded && _presentationActive && Visibility == Visibility.Visible;

    private void RequestRefresh()
    {
        if (!_renderQueue.Request(CanRender, out var generation)) return;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(_renderHandler, handler)) _renderHandler = null;
            if (_renderQueue.BeginRender(CanRender, generation)) Refresh();
        };
        _renderHandler = handler;
        CompositionTarget.Rendering += handler;
    }

    private void CancelRefresh()
    {
        if (_renderHandler is not null) CompositionTarget.Rendering -= _renderHandler;
        _renderHandler = null;
        _renderQueue.Cancel();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (CanRender) RequestRefresh();
        else { CancelRefresh(); ResetScroll(); }
    }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ViewportClip.Rect = new Rect(0, 0, Math.Max(0, args.NewSize.Width), Math.Max(0, args.NewSize.Height));
        DeferScrollUntilLayout();
    }
    private void OnSecondaryViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        SecondaryViewportClip.Rect = new Rect(0, 0, Math.Max(0, args.NewSize.Width), Math.Max(0, args.NewSize.Height));
        DeferScrollUntilLayout();
        NotifyTranslationVisibility();
    }
    private void Refresh()
    {
        if (_view is null || BaseLine is null) return;
        RefreshCount++;
        var previousHeight = IdealIslandHeight;
        var lineStart = _view.LyricPresentation.Line?.Start.Ticks ?? 0;
        if (_trackIdentity != _view.Session.TrackIdentity || _lineStartTicks != lineStart)
        {
            _trackIdentity = _view.Session.TrackIdentity;
            _lineStartTicks = lineStart;
            ResetScroll();
            _prefixWidths.Clear();
        }
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
            // Measure the attached text, including its small glyph inset, in the
            // same font/fallback, accessibility and rasterization context as rendering.
            BaseLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _textWidth = BaseLine.DesiredSize.Width;
            LyricTranslation.TranslateX = 0;
            LyricCanvas.Width = _textWidth;
            _primaryHeight = Math.Max(28, BaseLine.DesiredSize.Height);
            LyricViewport.Height = _primaryHeight;
            LyricCanvas.Height = BaseLine.DesiredSize.Height;
            _prefixWidths.Clear();
            DeferScrollUntilLayout();
            Canvas.SetTop(LyricCanvas, Math.Max(0, (_primaryHeight - LyricCanvas.Height) / 2));
            _measuredFontFamily = fontFamily;
            _measuredFontSize = fontSize;
            _measuredFontWeight = fontWeight;
            _measuredRasterizationScale = rasterizationScale;
        }
        var secondary = showDots ? string.Empty : LyricsDisplayPolicy.CompactText(settings.IslandActivity.ShowLyricsInCompact ? _view.SecondaryLyricText : null);
        if (_secondaryMeasureInvalid || SecondaryLine.Text != secondary || measureChanged ||
            _secondaryFontSize != SecondaryLine.FontSize)
        {
            SecondaryLine.Text = secondary;
            SecondaryLine.Visibility = string.IsNullOrWhiteSpace(secondary) ? Visibility.Collapsed : Visibility.Visible;
            SecondaryViewport.Visibility = SecondaryLine.Visibility;
            SecondaryLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SecondaryTranslation.TranslateX = 0;
            _secondaryMarquee.Reset();
            _secondaryFontSize = SecondaryLine.FontSize;
            _secondaryTextWidth = SecondaryLine.DesiredSize.Width;
            _secondaryHeight = SecondaryLine.DesiredSize.Height;
            SecondaryViewport.Height = _secondaryHeight;
            _secondaryMeasureInvalid = false;
            DeferScrollUntilLayout();
        }
        var secondaryHeight = SecondaryLine.Visibility == Visibility.Visible ? _secondaryHeight : 0;
        IdealIslandHeight = showDots ? 40 : IslandGeometry.MusicCompactHeight(_primaryHeight + secondaryHeight + 12);
        LyricViewport.Height = showDots ? 28 : _primaryHeight;
        ArtworkHost.Visibility = settings.IslandActivity.ShowArtwork ? Visibility.Visible : Visibility.Collapsed;
        var spectrum = settings.IslandActivity.ShowSpectrum && _view.HasSpectrumPresentation;
        SpectrumBars.Visibility = spectrum ? Visibility.Visible : Visibility.Collapsed;
        var bands = new[] { Band0, Band1, Band2, Band3, Band4, Band5 };
        for (var index = 0; index < bands.Length; index++)
            bands[index].Height = 2 + 20 * Math.Clamp(_view.Spectrum.Bands.ElementAtOrDefault(index), 0, 1);
        var maximum = settings.Lyrics.UnlimitedScrollingWidth ? _availableWidth : settings.Lyrics.ScrollingMaxWidth;
        var textWidth = settings.IslandActivity.CompactDynamicWidth ? Math.Min(Math.Max(_textWidth, _secondaryTextWidth), maximum) : 180;
        // Round outward once, rather than losing a fractional pixel at either edge.
        textWidth = Math.Ceiling(textWidth * rasterizationScale) / rasterizationScale;
        var width = 28 + textWidth + (settings.IslandActivity.ShowArtwork ? 36 : 12) + (spectrum ? 45 : 12);
        width = Math.Min(_availableWidth, showDots ? 280 : Math.Max(180, width));
        if (Math.Abs(width - IdealIslandWidth) > 0.5 || Math.Abs(previousHeight - IdealIslandHeight) > 0.5)
        { DeferScrollUntilLayout(); IdealIslandWidth = width; IdealWidthChanged?.Invoke(this, EventArgs.Empty); }
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
        _prefixWidths.Clear();
        ResetScroll();
        DeferScrollUntilLayout();
        RequestRefresh();
    }
    // The host signals its existing spring's state; no timer or extra frame loop.
    internal void SetGeometrySettled(bool settled)
    {
        if (_geometrySettled == settled) return;
        _geometrySettled = settled;
        if (settled) DeferScrollUntilLayout();
    }
    private void ResetScroll()
    {
        LyricTranslation.TranslateX = 0;
        SecondaryTranslation.TranslateX = 0;
        _secondaryMarquee.Reset();
    }
    private void DeferScrollUntilLayout()
    {
        _layoutPending = true;
        Layout.InvalidateArrange();
    }
    private double ScrollViewport(double textWidth, double actualWidth)
    {
        // At most half a physical pixel: absorb layout rounding, not true overflow.
        var tolerance = 0.5 / Math.Max(1, XamlRoot?.RasterizationScale ?? 1);
        return textWidth <= actualWidth + tolerance ? Math.Max(textWidth, actualWidth) : actualWidth;
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
        if (!CanRender || !_view.Settings.Lyrics.Enabled || !_view.Settings.IslandActivity.ShowLyricsInCompact ||
            !_view.Settings.Lyrics.Scrolling || _view.IsReducedMotion)
        {
            ResetScroll();
            return;
        }
        // Keep an unchanged overflowing row still while its host resizes. A new
        // row has already reset only its own offset; translated updates cannot
        // send the original row back to its beginning.
        if (_layoutPending) return;
        if (!_geometrySettled)
        {
            if (_textWidth <= ScrollViewport(_textWidth, LyricViewport.ActualWidth)) LyricTranslation.TranslateX = 0;
            if (_secondaryTextWidth <= ScrollViewport(_secondaryTextWidth, SecondaryViewport.ActualWidth)) SecondaryTranslation.TranslateX = 0;
            return;
        }
        var scroll = 0d;
        if (_view.Settings.Lyrics.Enabled && _view.Settings.IslandActivity.ShowLyricsInCompact && _view.Settings.Lyrics.Scrolling && !_view.IsReducedMotion)
        {
            scroll = LyricsDisplayPolicy.CompactScrollOffset(frame.Line,
                _view.Position - _view.Session.Timeline.Start, _view.Settings.Lyrics.DelayMilliseconds,
                _view.Settings.Lyrics.WordSyncedHighlighting, highlight, _textWidth, ScrollViewport(_textWidth, LyricViewport.ActualWidth));
        }
        LyricTranslation.TranslateX = -scroll;
        SecondaryTranslation.TranslateX = -_secondaryMarquee.Update(new(
            _view.Session.TrackIdentity, presentation.Line?.Start.Ticks ?? 0, SecondaryLine.Text,
            SecondaryLine.FontSize, XamlRoot?.RasterizationScale ?? 1, _secondaryTextWidth, ScrollViewport(_secondaryTextWidth, SecondaryViewport.ActualWidth),
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
        if (_prefixWidths.TryGetValue(text, out var width)) return width;
        _measure.Padding = BaseLine.Padding;
        _measure.UseLayoutRounding = BaseLine.UseLayoutRounding;
        _measure.FontStretch = BaseLine.FontStretch;
        _measure.FontFamily = BaseLine.FontFamily; _measure.FontSize = BaseLine.FontSize; _measure.FontWeight = BaseLine.FontWeight;
        _measure.FontStyle = BaseLine.FontStyle; _measure.CharacterSpacing = BaseLine.CharacterSpacing;
        _measure.Language = BaseLine.Language; _measure.FlowDirection = BaseLine.FlowDirection;
        _measure.IsTextScaleFactorEnabled = BaseLine.IsTextScaleFactorEnabled;
        _measure.Text = text; _measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _prefixWidths[text] = _measure.DesiredSize.Width;
    }
}
