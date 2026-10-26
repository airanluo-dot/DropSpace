using System.Numerics;
using DropSpace.App.Services;
using DropSpace.Core.Lyrics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DropSpace.App.Views.Music;

/// <summary>
/// Presentation only: shared LyricsHighlightFrame drives the native text layout.
/// No lyric lookup, playback clock, audio capture or independent frame timer.
/// </summary>
internal sealed class MusicLyricRow : Grid
{
    private static readonly SolidColorBrush White = new(Colors.White);
    private static readonly SolidColorBrush Waiting = new(ColorHelper.FromArgb(94, 255, 255, 255));
    private readonly Grid _sourceHost = new();
    private readonly Grid _output = new() { IsHitTestVisible = false };
    private readonly StackPanel _content = new() { Spacing = 10, Padding = new Thickness(8) };
    private readonly Grid _text = new();
    private readonly List<(TextBlock Text, RectangleGeometry Clip)> _completedLayers = [];
    private readonly TextBlock _original = Original();
    private readonly TextBlock _partial = Original();
    private readonly TextBlock _translation = new() { TextWrapping = TextWrapping.Wrap, Opacity = .76, Foreground = White };
    private readonly Run _run = new();
    private readonly RectangleGeometry _partialClip = new();
    private LyricsLine? _line;
    private readonly List<(int Start, int End)> _words = [];
    private readonly List<(int Start, int End, Rect Bounds, bool RightToLeft)> _glyphs = [];
    private bool _layoutDirty = true, _timed, _effectFailed;
    private int _completedLength = -1;
    private CompositionVisualSurface? _surface;
    private CompositionSurfaceBrush? _surfaceBrush;
    private CompositionEffectFactory? _factory;
    private CompositionEffectBrush? _blur;
    private CompositionLinearGradientBrush? _edge;
    private CompositionMaskBrush? _mask;
    private SpriteVisual? _sprite;
    private float _blurAmount = -1, _emphasis = -1;
    internal bool TranslationVisible => _translation.Visibility == Visibility.Visible;
    internal double HighlightedCharacters { get; private set; }
    internal float BlurAmount => Math.Max(0, _blurAmount);
    internal bool EffectReady => _sprite is not null;
    internal string? EffectFailure { get; private set; }

    internal MusicLyricRow()
    {
        _original.Inlines.Add(_run);
        _partial.Clip = _partialClip;
        _partial.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(_partial, AccessibilityView.Raw);
        _text.Children.Add(_original); _text.Children.Add(_partial);
        _content.Children.Add(_text); _content.Children.Add(_translation);
        _sourceHost.Children.Add(_content);
        Children.Add(_sourceHost); Children.Add(_output);
        SizeChanged += (_, _) => { _layoutDirty = true; UpdateSurfaceSize(); };
        Unloaded += (_, _) => ReleaseEffect();
    }

    private static TextBlock Original() => new()
    { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, CharacterSpacing = 12, Foreground = White };

    internal void Update(LyricsLine line, string? secondary, double originalSize, double translationSize)
    {
        _line = line;
        if (_run.Text != line.Text)
        {
            _run.Text = _partial.Text = line.Text;
            _layoutDirty = true; _completedLength = -1;
        }
        if (_original.FontSize != originalSize)
        {
            _original.FontSize = _partial.FontSize = originalSize;
            _original.LineHeight = _partial.LineHeight = originalSize * 1.3;
            _layoutDirty = true;
        }
        _translation.Text = secondary ?? string.Empty;
        _translation.FontSize = translationSize; _translation.LineHeight = translationSize * 1.35;
        _translation.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var layer in _completedLayers) UpdateCompletedLayer(layer.Text);
        _words.Clear();
        var timedText = string.Concat(line.Words.Select(word => word.Text));
        if (string.Equals(timedText.Trim(), line.Text, StringComparison.Ordinal))
        {
            // Enhanced LRC keeps whitespace in its timed words but trims the
            // display body. Keep provider indices, including whitespace-only
            // words, while projecting their ranges into the displayed text.
            var offset = -(timedText.Length - timedText.TrimStart().Length);
            foreach (var word in line.Words)
            {
                var end = offset + word.Text.Length;
                _words.Add((Math.Clamp(offset, 0, line.Text.Length), Math.Clamp(end, 0, line.Text.Length)));
                offset = end;
            }
            return;
        }
        var cursor = 0;
        foreach (var word in line.Words)
        {
            var start = line.Text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
            // Provider words occasionally use a different normalized body. Keep
            // the existing line fallback instead of highlighting the wrong text.
            if (start < 0) { _words.Clear(); break; }
            _words.Add((start, start + word.Text.Length)); cursor = start + word.Text.Length;
        }
    }

    internal void Highlight(LyricsHighlightFrame frame, bool active, bool wordHighlighting)
    {
        var timed = active && wordHighlighting && _line is { Words.Count: > 0 } && _words.Count == _line.Words.Count;
        if (_timed != timed)
        {
            _timed = timed; _completedLength = -1;
            _original.Foreground = timed ? Waiting : White;
            _partial.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
            if (!timed) foreach (var layer in _completedLayers) layer.Text.Visibility = Visibility.Collapsed;
        }
        if (!timed) { HighlightedCharacters = 0; return; }
        if (_layoutDirty) MeasureGlyphs();
        var done = 0;
        var clip = new Rect();
        HighlightedCharacters = 0;
        if (frame.WordIndex >= 0 && frame.WordIndex < _words.Count)
        {
            var word = _words[frame.WordIndex];
            done = word.Start;
            var glyphs = _glyphs.Where(glyph => glyph.Start >= word.Start && glyph.Start < word.End).ToArray();
            var remaining = glyphs.Sum(glyph => glyph.Bounds.Width) * Math.Clamp(frame.WordProgress, 0, 1);
            foreach (var glyph in glyphs)
            {
                if (remaining >= glyph.Bounds.Width)
                { done = glyph.End; remaining -= glyph.Bounds.Width; continue; }
                clip = glyph.Bounds;
                clip.Width = Math.Max(0, remaining);
                if (glyph.RightToLeft) clip.X = glyph.Bounds.Right - clip.Width;
                HighlightedCharacters = done + (glyph.Bounds.Width > 0 ? remaining / glyph.Bounds.Width : 0);
                break;
            }
            if (clip.Width <= 0) HighlightedCharacters = done;
        }
        if (_completedLength != done)
        {
            // Clip a retained white text layer to native glyph geometry. Mutating
            // TextHighlighter.Ranges did not reliably redraw the captured source
            // in WinUI, leaving already sung text dim while only the partial glyph lit.
            var clips = new List<Rect>();
            foreach (var row in _glyphs.Where(glyph => glyph.End <= done && glyph.Bounds.Width > 0)
                         .GroupBy(glyph => Math.Round(glyph.Bounds.Y, 1)))
            {
                Rect? run = null;
                foreach (var glyph in row.OrderBy(glyph => glyph.Bounds.X))
                {
                    if (run is { } span && glyph.Bounds.X <= span.Right + .5)
                        run = new Rect(span.X, span.Y, Math.Max(span.Right, glyph.Bounds.Right) - span.X, Math.Max(span.Height, glyph.Bounds.Height));
                    else
                    {
                        if (run is { } previous) clips.Add(previous);
                        run = glyph.Bounds;
                    }
                }
                if (run is { } last) clips.Add(last);
            }
            SetCompletedClips(clips);
            _completedLength = done;
        }
        _partialClip.Rect = clip;
    }

    private void UpdateCompletedLayer(TextBlock text)
    {
        text.Text = _run.Text;
        text.FontSize = _original.FontSize;
        text.LineHeight = _original.LineHeight;
    }

    private void SetCompletedClips(List<Rect> clips)
    {
        // WinUI UIElement.Clip supports rectangles only. Retain one native white
        // layer per completed visual-row span (normally one or two), rather than
        // reflowing a substring or manufacturing another lyric/timing source.
        while (_completedLayers.Count < clips.Count)
        {
            var text = Original(); var clip = new RectangleGeometry();
            text.Clip = clip; text.IsHitTestVisible = false;
            AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
            UpdateCompletedLayer(text);
            _completedLayers.Add((text, clip)); _text.Children.Add(text);
        }
        for (var i = 0; i < _completedLayers.Count; i++)
        {
            var layer = _completedLayers[i];
            layer.Text.Visibility = i < clips.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i < clips.Count) layer.Clip.Rect = clips[i];
        }
    }

    private void MeasureGlyphs()
    {
        _glyphs.Clear();
        _completedLength = -1;
        if (_original.ActualWidth <= 0 || _original.ActualHeight <= 0) return;
        var positions = System.Globalization.StringInfo.ParseCombiningCharacters(_run.Text);
        for (var i = 0; i < positions.Length; i++)
        {
            var start = positions[i];
            var end = i + 1 < positions.Length ? positions[i + 1] : _run.Text.Length;
            var pointer = _run.ContentStart.GetPositionAtOffset(start, LogicalDirection.Forward);
            var endPointer = _run.ContentStart.GetPositionAtOffset(end, LogicalDirection.Backward);
            if (pointer is null || endPointer is null) continue;
            var bounds = pointer.GetCharacterRect(LogicalDirection.Forward);
            var trailing = endPointer.GetCharacterRect(LogicalDirection.Backward);
            // WinUI returns caret rectangles (zero width), not glyph advances.
            // Use both insertion positions; Backward keeps the trailing caret
            // on the same wrapped row. Text elements preserve surrogate/mark pairs.
            var rtl = trailing.X < bounds.X;
            if (Math.Abs(trailing.Y - bounds.Y) < 1)
                bounds = new Rect(Math.Min(bounds.X, trailing.X), bounds.Y, Math.Abs(trailing.X - bounds.X), Math.Max(bounds.Height, trailing.Height));
            if (bounds.Height > 0) _glyphs.Add((start, end, bounds, rtl));
        }
        _layoutDirty = _glyphs.Count == 0;
    }

    internal void Focus(int distance, bool idle, double top, double viewportHeight, bool reducedMotion)
    {
        var visible = top < viewportHeight + 80 && top + ActualHeight > -80;
        if (!visible) { ReleaseEffect(); return; }
        EnsureEffect();
        var amount = !idle || distance == 0 ? 0 : (float)Math.Min(7, .9 + distance * 1.15);
        var opacity = distance == 0 ? 1f : idle ? .50f : .66f;
        if (_blur is not null && Math.Abs(_blurAmount - amount) > .01f)
            Animate(_blur.Properties, "Softness.BlurAmount", amount, reducedMotion);
        if (Math.Abs(_emphasis - opacity) > .01f)
            Animate(ElementCompositionPreview.GetElementVisual(this), "Opacity", opacity, reducedMotion);
        _blurAmount = amount; _emphasis = opacity;
        if (_edge is not null)
        {
            // The same viewport-relative gradient crosses every row, so the
            // first/last glyph fades continuously rather than one whole row at a time.
            _edge.StartPoint = new Vector2(0, (float)-top);
            _edge.EndPoint = new Vector2(0, (float)(viewportHeight - top));
        }
    }

    private void EnsureEffect()
    {
        if (_sprite is not null || _effectFailed || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _surface = compositor.CreateVisualSurface();
            _surface.SourceVisual = ElementCompositionPreview.GetElementVisual(_content);
            _surfaceBrush = compositor.CreateSurfaceBrush(_surface);
            _surfaceBrush.Stretch = CompositionStretch.None;
            _surfaceBrush.HorizontalAlignmentRatio = _surfaceBrush.VerticalAlignmentRatio = 0;
            _factory = compositor.CreateEffectFactory(new GaussianBlurEffectDescription("Softness", new CompositionEffectSourceParameter("content")), ["Softness.BlurAmount"]);
            _blur = _factory.CreateBrush(); _blur.SetSourceParameter("content", _surfaceBrush);
            _edge = compositor.CreateLinearGradientBrush();
            _edge.MappingMode = CompositionMappingMode.Absolute;
            _edge.ColorStops.Add(compositor.CreateColorGradientStop(0, Colors.Transparent));
            _edge.ColorStops.Add(compositor.CreateColorGradientStop(.10f, Colors.White));
            _edge.ColorStops.Add(compositor.CreateColorGradientStop(.88f, Colors.White));
            _edge.ColorStops.Add(compositor.CreateColorGradientStop(1, Colors.Transparent));
            _mask = compositor.CreateMaskBrush(); _mask.Source = _blur; _mask.Mask = _edge;
            _sprite = compositor.CreateSpriteVisual(); _sprite.Brush = _mask;
            ElementCompositionPreview.SetElementChildVisual(_output, _sprite);
            // Hide only the source's ancestor: the captured source subtree remains
            // opaque. It still participates in native layout and accessibility.
            // XAML Opacity=0 culls text drawing before the compositor can sample
            // it. Hide the ancestor visual only, retaining the live source tree.
            ElementCompositionPreview.GetElementVisual(_sourceHost).Opacity = 0;
            _blurAmount = -1;
            UpdateSurfaceSize();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { EffectFailure = error.GetType().Name + ": " + error.Message; _effectFailed = true; ReleaseEffect(); }
    }

    private void UpdateSurfaceSize()
    {
        if (_surface is null || _sprite is null) return;
        var size = new Vector2((float)Math.Max(1, ActualWidth), (float)Math.Max(1, ActualHeight));
        _surface.SourceSize = size; _sprite.Size = size;
    }

    private static void Animate(CompositionObject target, string property, float value, bool reducedMotion)
    {
        if (reducedMotion)
        {
            target.StopAnimation(property);
            if (target is Visual visual) visual.Opacity = value;
            else ((CompositionPropertySet)target).InsertScalar(property, value);
            return;
        }
        using var animation = target.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, value); animation.Duration = TimeSpan.FromMilliseconds(320);
        target.StartAnimation(property, animation);
    }

    private void ReleaseEffect()
    {
        if (_surface is null && _sprite is null) return;
        ElementCompositionPreview.SetElementChildVisual(_output, null);
        ElementCompositionPreview.GetElementVisual(_sourceHost).Opacity = 1;
        _sprite?.Dispose(); _sprite = null;
        _mask?.Dispose(); _mask = null; _edge?.Dispose(); _edge = null;
        _blur?.Dispose(); _blur = null; _factory?.Dispose(); _factory = null;
        _surfaceBrush?.Dispose(); _surfaceBrush = null; _surface?.Dispose(); _surface = null;
        _blurAmount = -1;
    }
}
