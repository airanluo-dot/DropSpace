using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Numerics;
using DropSpace.App.Controls;
using DropSpace.App.Services;
using DropSpace.App.Services.Media;
using DropSpace.App.Services.Volume;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Island;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace DropSpace.App.Views.Music;

/// <summary>A session-only presentation of the same media, artwork and lyrics as the island.</summary>
public sealed class MusicWindow : Window
{
    private readonly MediaViewModel _media;
    private readonly NativeSettingsEditor _editor;
    private readonly IAppStringLocalizer _strings;
    private readonly WindowsVolumeActivityService _volume;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Grid _root = new() { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(ColorHelper.FromArgb(255, 24, 25, 30)) };
    private readonly MusicFlowBackground _background = new();
    private readonly Canvas _body = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ScrollViewer _bodyViewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Grid _artworkSlot = new();
    private readonly StackPanel _metadata = new() { Spacing = 3 };
    private readonly Grid _controls = new();
    private readonly Grid _volumeRow = new() { ColumnSpacing = 12 };
    private readonly Grid _playShapes = new();
    private readonly FrameworkElement _playShape = MusicWindowVisuals.Symbol("play", 40);
    private readonly FrameworkElement _pauseShape = MusicWindowVisuals.Symbol("pause", 40);
    private readonly Border _cover = new() { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(ColorHelper.FromArgb(24, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _artwork = new() { Stretch = Stretch.UniformToFill };
    private readonly StackPanel _noArtwork = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _noArtworkText = new() { FontSize = 14, Opacity = 0.65 };
    private readonly TextBlock _title = new() { FontSize = 25, FontWeight = FontWeights.SemiBold, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { FontSize = 18, Opacity = 0.66, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, StepFrequency = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _elapsed = new() { FontSize = 12, Opacity = 0.64 };
    private readonly TextBlock _remaining = new() { FontSize = 12, Opacity = 0.64, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _previous = IconButton("previous", 27);
    private readonly Button _play = IconButton("play", 40);
    private readonly Button _next = IconButton("next", 27);
    private readonly Slider _volumeSlider = new() { Minimum = 0, Maximum = 100, StepFrequency = 1 };
    private readonly FontIcon _volumeIcon = new() { Glyph = "\uE994", FontSize = 15, Opacity = 0.7 };
    private readonly TextBlock _error = new() { FontSize = 12, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.75 };
    private readonly Grid _lyricsPanel = new();
    private readonly ToggleButton _translation = new() { HorizontalAlignment = HorizontalAlignment.Right, Foreground = new SolidColorBrush(Colors.White), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(12, 6, 12, 6) };
    private readonly ToggleButton _lyricsToggle = new() { IsChecked = true, Foreground = new SolidColorBrush(Colors.White), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ScrollViewer _lyricsScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    private readonly StackPanel _lyricsRows = new() { Spacing = 26, Padding = new Thickness(0, 80, 12, 100) };
    private readonly StackPanel _lyricsEmpty = new() { Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _lyricsStatus = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly TextBlock _translationStatus = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Opacity = 0.65 };
    private readonly LyricsRowCollection<MusicLyricRow> _rows;
    private readonly LyricsGlowAudioResponse _audioResponse = new();
    private long _audioResponseTick;
    private bool _lyricsIdle, _lyricFocusDirty = true;
    private long _controlsInteractionAt = Stopwatch.GetTimestamp(), _lyricInteractionAt = Stopwatch.GetTimestamp();
    private readonly MediaRenderQueue _renderQueue = new();
    private readonly MediaSeekInteraction _seekInteraction = new(TimeSpan.FromSeconds(2));
    private readonly DispatcherQueueTimer _seekCommit, _seekAcknowledgement, _volumeCommit, _manualFollowTimer, _idleTimer;
    private Task _volumeLeaseTask = Task.CompletedTask;
    private byte[]? _backgroundArtwork;
    private string _trackIdentity = string.Empty;
    private string _lyricsOptions = string.Empty;
    private bool _loaded, _closed, _minimized, _lyricsVisible = true, _updating, _lyricsDirty = true, _metadataDirty = true;
    private bool _volumeDesiredActive, _volumeWriting, _volumeWriteFailed, _keyboardControlsIntent;
    private readonly HashSet<uint> _interactingPointers = [];
    private readonly HashSet<uint> _manualScrollPointers = [];
    private Point? _lastPointerPosition;
    private bool _manualScrollPending, _manualScrollInertia;
    private bool _portrait, _controlsVisible = true, _lyricsChoiceExplicit, _nativeVisible;
    private bool _transitionRunning, _transitionHooked, _clampingSize;
    private long _transitionGeneration, _transitionStarted;
    private double _transitionProgress = 1;
    private MusicWindowFrame? _presented, _transitionFrom, _transitionTarget;
    private uint? _seekPointer;
    private double? _queuedSeek, _queuedVolume;
    private double _coverScale = 1;
    private int _activeLyric = -1, _lastCentered = -1;
    private bool CanRender => _loaded && !_closed && !_minimized && AppWindow.IsVisible;

    public MusicWindow(MediaViewModel media, NativeSettingsEditor editor, IAppStringLocalizer strings,
        WindowsVolumeActivityService volume, ILogger logger)
    {
        _media = media; _editor = editor; _strings = strings; _volume = volume; _logger = logger;
        _rows = new(2_000, CreateLyricRow, UpdateLyricRow);
        _seekCommit = Timer(120, OnSeekCommit);
        _seekAcknowledgement = Timer(2_000, (_, _) => { _seekInteraction.RejectPending(); RequestRender(); });
        _volumeCommit = Timer(100, (_, _) => { if (!_volumeWriting) _ = WriteVolumeAsync(); });
        _manualFollowTimer = Timer(5_000, (_, _) => CompleteManualScroll());
        _idleTimer = Timer(4_000, (_, _) => OnIdle());
        BuildContent();
        Content = _root;
        AppWindow.Resize(new SizeInt32(1180, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.PreferredMinimumWidth = 360;
            presenter.PreferredMinimumHeight = 600;
        }
        NativeApplicationIcon.ApplyToWindow(WindowNative.GetWindowHandle(this), AppWindow);
        _media.PropertyChanged += OnMediaChanged;
        _volume.SnapshotChanged += OnVolumeChanged;
        _background.Faulted += OnBackgroundFaulted;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;
        _root.Loaded += (_, _) => { _loaded = true; UpdateViewport(); UpdateLayoutGeometry(); UpdateOwnership(); RequestRender(); };
        _bodyViewport.SizeChanged += (_, _) => UpdateViewport();
        _body.SizeChanged += (_, _) => { UpdateLayoutGeometry(); _lastCentered = -1; };
        foreach (var button in new ButtonBase[] { _previous, _play, _next, _translation, _lyricsToggle }) AddButtonMotion(button);
        _translation.Click += async (_, _) => await SetTranslationEnabledAsync(_translation.IsChecked == true);
        _lyricsToggle.Click += (_, _) => SetLyricsVisible(_lyricsToggle.IsChecked == true);
        _lyricsScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => OnManualScroll()), true);
        _lyricsScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => { _manualScrollPointers.Add(args.Pointer.PointerId); OnManualScroll(); }), true);
        _lyricsScroll.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
                or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown
                or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End) OnManualScroll();
        }), true);
        _lyricsScroll.ViewChanged += OnLyricsViewChanged;
        _lyricsScroll.SizeChanged += (_, _) =>
        {
            var padding = Math.Max(80, _lyricsScroll.ActualHeight / 2);
            _lyricsRows.Padding = new Thickness(0, padding, 12, padding);
            _lastCentered = -1; _lyricFocusDirty = true; RequestRender();
        };
        _root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerActivity), true);
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => { _lastPointerPosition = args.GetCurrentPoint(_root).Position; _keyboardControlsIntent = false; _interactingPointers.Add(args.Pointer.PointerId); NotifyInteraction(); }), true);
        _root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) => { _interactingPointers.Remove(args.Pointer.PointerId); EndManualScrollPointer(args, released: true); NotifyInteraction(); }), true);
        _root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, args) => { _interactingPointers.Remove(args.Pointer.PointerId); EndManualScrollPointer(args, released: true); NotifyInteraction(); }), true);
        _root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, args) => { var wasPressed = _interactingPointers.Remove(args.Pointer.PointerId); EndManualScrollPointer(args, released: false); if (wasPressed) NotifyInteraction(); }), true);
        _root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => { _keyboardControlsIntent = true; NotifyInteraction(); }), true);
        _root.GotFocus += (_, _) => NotifyInteraction();
        _root.LostFocus += (_, _) => NotifyInteraction();
        _seek.ValueChanged += OnSeekChanged;
        _seek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSeekPressed), true);
        _seek.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSeekReleased), true);
        _seek.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnSeekCanceled), true);
        _seek.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnSeekCaptureLost), true);
        _volumeSlider.ValueChanged += (_, args) =>
        {
            if (_updating || !CanRender || !_volumeSlider.IsEnabled) return;
            _queuedVolume = args.NewValue / 100;
            _volumeCommit.Stop(); _volumeCommit.Start();
        };
        RefreshLanguage();
    }

    private DispatcherQueueTimer Timer(int milliseconds, TypedEventHandler<DispatcherQueueTimer, object> action)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds); timer.IsRepeating = false; timer.Tick += action;
        return timer;
    }

    private static Button IconButton(string symbol, double size) => new()
    {
        Content = MusicWindowVisuals.Symbol(symbol, size), MinWidth = 68, MinHeight = 58,
        Foreground = new SolidColorBrush(Colors.White),
        Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private void BuildContent()
    {
        _root.Children.Add(_background);
        _root.Children.Add(new Border { Background = new SolidColorBrush(ColorHelper.FromArgb(58, 0, 0, 0)), IsHitTestVisible = false });
        var chrome = new Grid();
        chrome.RowDefinitions.Add(new() { Height = new GridLength(32) });
        chrome.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        chrome.Children.Add(titleBar);
        ExtendsContentIntoTitleBar = true; SetTitleBar(titleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Colors.Gray;
        var art = new Grid(); art.Children.Add(_artwork);
        _noArtwork.Children.Add(new FontIcon { Glyph = "\uE8D6", FontSize = 48, Opacity = 0.45 });
        _noArtwork.Children.Add(_noArtworkText); art.Children.Add(_noArtwork); _cover.Child = art;
        _artworkSlot.Children.Add(_cover); _body.Children.Add(_artworkSlot);
        _metadata.Children.Add(_title); _metadata.Children.Add(_artist); _body.Children.Add(_metadata);
        foreach (var height in new double[] { 48, 8, 58, 8, 40, 66, 28 })
            _controls.RowDefinitions.Add(new() { Height = new GridLength(height) });
        var timeline = new StackPanel { Spacing = 0 }; timeline.Children.Add(_seek);
        var times = new Grid(); times.Children.Add(_elapsed); times.Children.Add(_remaining); timeline.Children.Add(times); _controls.Children.Add(timeline);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26, HorizontalAlignment = HorizontalAlignment.Center };
        _previous.Command = _media.PreviousCommand; _play.Command = _media.PlayPauseCommand; _next.Command = _media.NextCommand;
        _playShapes.Width = _playShapes.Height = 40; _playShapes.Children.Add(_playShape); _playShapes.Children.Add(_pauseShape); _play.Content = _playShapes;
        transport.Children.Add(_previous); transport.Children.Add(_play); transport.Children.Add(_next); Grid.SetRow(transport, 2); _controls.Children.Add(transport);
        var volume = _volumeRow;
        volume.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); volume.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        volume.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        volume.Children.Add(_volumeIcon); Grid.SetColumn(_volumeSlider, 1); volume.Children.Add(_volumeSlider);
        var loud = new FontIcon { Glyph = "\uE995", FontSize = 15, Opacity = 0.7 }; Grid.SetColumn(loud, 2); volume.Children.Add(loud); Grid.SetRow(volume, 4); _controls.Children.Add(volume);
        Grid.SetRow(_error, 6); _controls.Children.Add(_error); _body.Children.Add(_controls);
        _lyricsPanel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _lyricsPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _lyricsScroll.Content = _lyricsRows; _lyricsPanel.Children.Add(_lyricsScroll);
        _lyricsEmpty.Children.Add(_lyricsStatus); _lyricsPanel.Children.Add(_lyricsEmpty);
        Grid.SetRow(_translationStatus, 1); _lyricsPanel.Children.Add(_translationStatus); _body.Children.Add(_lyricsPanel);
        _translation.Content = MusicWindowVisuals.Symbol("translation", 22);
        _lyricsToggle.Content = MusicWindowVisuals.Symbol("lyrics", 22);
        MusicWindowVisuals.StyleRoundToggle(_translation); MusicWindowVisuals.StyleRoundToggle(_lyricsToggle);
        MusicWindowVisuals.StyleSlider(_seek); MusicWindowVisuals.StyleSlider(_volumeSlider);
        _body.Children.Add(_translation); _body.Children.Add(_lyricsToggle);
        _bodyViewport.Content = _body; Grid.SetRow(_bodyViewport, 1); chrome.Children.Add(_bodyViewport); _root.Children.Add(chrome);
        AutomationProperties.SetAutomationId(_root, "MusicWindowRoot");
        AutomationProperties.SetAutomationId(_cover, "MusicWindowArtwork");
        AutomationProperties.SetAutomationId(_play, "MusicWindowPlayPause");
        AutomationProperties.SetAutomationId(_previous, "MusicWindowPrevious");
        AutomationProperties.SetAutomationId(_next, "MusicWindowNext");
        AutomationProperties.SetAutomationId(_seek, "MusicWindowSeek");
        AutomationProperties.SetAutomationId(_volumeSlider, "MusicWindowVolume");
        AutomationProperties.SetAutomationId(_lyricsToggle, "MusicWindowLyrics");
        AutomationProperties.SetAutomationId(_translation, "MusicWindowTranslation");
        AutomationProperties.SetAutomationId(_lyricsScroll, "MusicWindowLyricsContent");
    }

    public MusicWindowState GetState() => new(!_closed, _minimized, _lyricsVisible,
        _media.Settings.Lyrics.SecondaryLyrics, _media.Session.TrackIdentity, _media.IsPlaying,
        _seek.IsEnabled, _coverScale, _background.IsReady, CanRender && !_media.IsReducedMotion,
        _background.FrameCount, _background.PresentedFrameCount, _background.IsRendering, _background.LastFault,
        _portrait, _controlsVisible, _transitionRunning, _transitionGeneration, _transitionProgress,
        _lyricsPanel.Opacity, _controls.Opacity, _media.Spectrum.CaptureMode.ToString(), _audioResponse.Energy,
        _background.AudioPulse, _activeLyric >= 0 && _activeLyric < _rows.Count ? _rows[_activeLyric].HighlightedCharacters : 0,
        _lyricsIdle);

    public MusicFlowBackground Background => _background;
    internal object LayoutSnapshot
    {
        get
        {
            _root.UpdateLayout();
            return new
            {
                RootWidth = _root.ActualWidth, RootHeight = _root.ActualHeight,
                LeftX = ActualBounds(_controls).X, LeftWidth = _controls.ActualWidth, LyricsWidth = _lyricsPanel.ActualWidth,
                LyricsDisplayed = _lyricsPanel.Visibility == Visibility.Visible,
                ArtworkDisplayed = _artwork.Source is not null, LyricRows = _lyricsRows.Children.Count,
                TranslationDisplayed = _rows.Count > 0 && _rows[0].TranslationVisible,
                LyricsIdle = _lyricsIdle,
                ActivePointers = _interactingPointers.Count, ManualPointers = _manualScrollPointers.Count,
                IdleTimerRunning = _idleTimer.IsRunning, KeyboardControlsIntent = _keyboardControlsIntent,
                LyricViewportHeight = _lyricsScroll.ViewportHeight, LyricScrollOffset = _lyricsScroll.VerticalOffset,
                LyricScrollableHeight = _lyricsScroll.ScrollableHeight,
                ActiveHighlightedCharacters = _activeLyric >= 0 && _activeLyric < _rows.Count ? _rows[_activeLyric].HighlightedCharacters : 0,
                LyricEffects = Enumerable.Range(0, _rows.Count).Where(i => _rows[i].EffectReady).Select(i => new { Index = i, _rows[i].BlurAmount }).ToArray(),
                LyricEffectFailures = Enumerable.Range(0, _rows.Count).Select(i => _rows[i].EffectFailure).Where(value => value is not null).Distinct().ToArray(),
                VolumeEnabled = _volumeSlider.IsEnabled, VolumeValue = _volumeSlider.Value,
                Portrait = _portrait, ControlsVisible = _controlsVisible, TransitionRunning = _transitionRunning,
                TransitionGeneration = _transitionGeneration, TransitionProgress = _transitionProgress,
                PresentedCover = ActualBounds(_cover), PresentedCoverSlot = ActualBounds(_artworkSlot),
                PresentedLyrics = ActualBounds(_lyricsPanel), PresentedMetadata = ActualBounds(_metadata), PresentedControls = ActualBounds(_controls),
                PresentedFooter = ActualBounds(_lyricsToggle), PresentedTranslation = ActualBounds(_translation),
                PresentedVolume = ActualBounds(_volumeRow),
                ActualLyricsOpacity = _lyricsPanel.Opacity, ActualControlsOpacity = _controls.Opacity,
                ActualTranslationOpacity = _translation.Opacity, ActualFooterOpacity = _lyricsToggle.Opacity,
                ActualLyricsVisibility = _lyricsPanel.Visibility.ToString(),
                CoverIdentity = RuntimeHelpers.GetHashCode(_cover), LyricsIdentity = RuntimeHelpers.GetHashCode(_lyricsPanel),
                MetadataIdentity = RuntimeHelpers.GetHashCode(_metadata), ControlsIdentity = RuntimeHelpers.GetHashCode(_controls),
            };
        }
    }
    internal sealed record PresentedBounds(double X, double Y, double Width, double Height);
    private PresentedBounds ActualBounds(FrameworkElement element)
    {
        var point = element.TransformToVisual(_root).TransformPoint(new Point());
        return new(point.X, point.Y, element.ActualWidth, element.ActualHeight);
    }
    internal void DiagnosticNotifyInteraction() => NotifyInteraction();
    internal void DiagnosticNotifyLyricInteraction() => OnManualScroll();
    internal void DiagnosticFocusTransport() { _keyboardControlsIntent = true; _previous.Focus(FocusState.Keyboard); }

    private void UpdateViewport()
    {
        if (_closed || _bodyViewport.ActualWidth <= 0 || _bodyViewport.ActualHeight <= 0) return;
        var scale = _root.XamlRoot?.RasterizationScale ?? 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            var minimumWidth = (int)Math.Ceiling(Math.Min(360 * scale, area?.WorkArea.Width ?? int.MaxValue));
            var minimumHeight = (int)Math.Ceiling(Math.Min(600 * scale, area?.WorkArea.Height ?? int.MaxValue));
            presenter.PreferredMinimumWidth = minimumWidth;
            presenter.PreferredMinimumHeight = minimumHeight;
            if (!_clampingSize && presenter.State == OverlappedPresenterState.Restored &&
                (AppWindow.Size.Width < minimumWidth || AppWindow.Size.Height < minimumHeight))
            {
                _clampingSize = true;
                try { AppWindow.Resize(new SizeInt32(Math.Max(minimumWidth, AppWindow.Size.Width), Math.Max(minimumHeight, AppWindow.Size.Height))); }
                finally { _clampingSize = false; }
            }
        }
        // Small work areas retain scrolling access instead of clipping the controls.
        _body.Width = Math.Max(320, _bodyViewport.ActualWidth);
        _body.Height = Math.Max(480, _bodyViewport.ActualHeight);
    }

    public void SetMinimized(bool minimized)
    {
        if (_closed || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (minimized) presenter.Minimize();
        else if (presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        RefreshWindowVisibility();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange) UpdateViewport();
        RefreshWindowVisibility();
    }
    private void RefreshWindowVisibility()
    {
        var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        var visible = AppWindow.IsVisible;
        if (_minimized == minimized && _nativeVisible == visible) return;
        _minimized = minimized; _nativeVisible = visible; UpdateOwnership();
        if (!CanRender) { CancelRender(); CancelSeek(); _volumeCommit.Stop(); _queuedVolume = null; }
        else { _metadataDirty = _lyricsDirty = true; NotifyInteraction(); RequestRender(); }
    }

    public void SetLyricsVisible(bool visible)
    {
        if (_closed) return;
        _lyricsChoiceExplicit = true;
        NotifyInteraction();
        if (_lyricsVisible == visible) return;
        _lyricsVisible = visible; _lyricsToggle.IsChecked = visible;
        _lastCentered = -1; _lyricsDirty = true;
        UpdateLayoutGeometry(); RefreshLanguage(); ResetIdleTimer(); RequestRender();
    }

    public async Task SetTranslationEnabledAsync(bool enabled)
    {
        if (_closed) return;
        _translation.IsEnabled = false;
        try { await _editor.UpdateAsync(settings => settings with { Lyrics = settings.Lyrics with { SecondaryLyrics = enabled } }); }
        finally
        {
            if (!_closed)
            {
                _translation.IsEnabled = true; _translation.IsChecked = _editor.Settings.Lyrics.SecondaryLyrics;
                _lyricsDirty = true; RequestRender();
            }
        }
    }

    private void UpdateLayoutGeometry()
    {
        if (_closed || _body.ActualWidth <= 0 || _body.ActualHeight <= 0) return;
        var portrait = MusicWindowLayout.IsPortrait(_body.ActualWidth, _body.ActualHeight);
        if (_portrait != portrait)
        {
            _portrait = portrait;
            if (!_lyricsChoiceExplicit) { _lyricsVisible = !portrait; _lyricsToggle.IsChecked = _lyricsVisible; }
            _controlsVisible = true; RefreshLanguage();
        }
        _body.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _body.ActualWidth, _body.ActualHeight) };
        var next = MusicWindowLayout.Create(_body.ActualWidth, _body.ActualHeight, _portrait, _lyricsVisible, _controlsVisible, _media.IsPlaying);
        if (Equals(next, _transitionTarget)) return;
        // Retarget from the last frame actually assigned to XAML, without advancing it here.
        _transitionFrom = _presented ?? next; _transitionTarget = next;
        _transitionGeneration++; _transitionStarted = Stopwatch.GetTimestamp(); _transitionProgress = 0;
        if (_presented is null || !CanRender || _media.IsReducedMotion)
        {
            _transitionRunning = false; _transitionProgress = 1; ApplyFrame(next); FinishTransition();
        }
        else
        {
            _transitionRunning = true;
            // An exiting pane remains alive until its final transparent frame.
            _lyricsPanel.Visibility = _transitionFrom.LyricsOpacity > 0 || next.LyricsOpacity > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_transitionHooked) { CompositionTarget.Rendering += OnTransitionFrame; _transitionHooked = true; }
        }
        ResetIdleTimer();
    }
    private void OnTransitionFrame(object? sender, object args) => AdvanceTransition();
    private void AdvanceTransition()
    {
        if (!_transitionRunning || _transitionFrom is null || _transitionTarget is null) return;
        _transitionProgress = Math.Clamp(Stopwatch.GetElapsedTime(_transitionStarted).TotalMilliseconds / 480, 0, 1);
        var eased = 1 - Math.Pow(1 - _transitionProgress, 3);
        ApplyFrame(MusicWindowFrame.Interpolate(_transitionFrom, _transitionTarget, eased));
        if (_transitionProgress >= 1) { _transitionRunning = false; FinishTransition(); }
    }
    private void ApplyFrame(MusicWindowFrame frame)
    {
        _presented = frame;
        static void Place(FrameworkElement element, Rect rect)
        {
            Canvas.SetLeft(element, rect.X); Canvas.SetTop(element, rect.Y);
            element.Width = Math.Max(1, rect.Width); element.Height = Math.Max(1, rect.Height);
        }
        Place(_artworkSlot, frame.Cover); Place(_metadata, frame.Metadata); Place(_controls, frame.Controls);
        Place(_lyricsPanel, frame.Lyrics); Place(_translation, frame.Translation); Place(_lyricsToggle, frame.Footer);
        // Slot geometry and playback scale are independent layers of the same timeline.
        _cover.Width = frame.Cover.Width * frame.CoverScale; _cover.Height = frame.Cover.Height * frame.CoverScale;
        _cover.CornerRadius = new CornerRadius(frame.CoverRadius); _coverScale = frame.CoverScale;
        _title.FontSize = frame.TitleSize; _artist.FontSize = frame.ArtistSize;
        _noArtworkText.Visibility = _cover.Width < 140 ? Visibility.Collapsed : Visibility.Visible;
        ((FontIcon)_noArtwork.Children[0]).FontSize = Math.Min(48, _cover.Width * .42);
        _lyricsPanel.Opacity = frame.LyricsOpacity; _controls.Opacity = frame.ControlsOpacity;
        _translation.Opacity = frame.ControlsOpacity; _lyricsToggle.Opacity = frame.ControlsOpacity;
        _lyricsPanel.IsHitTestVisible = _lyricsVisible && frame.LyricsOpacity > .02;
        _controls.IsHitTestVisible = _controlsVisible; _translation.IsHitTestVisible = _controlsVisible;
        _lyricsToggle.IsHitTestVisible = _controlsVisible;
        _playShape.Opacity = 1 - frame.PlaybackMix; _pauseShape.Opacity = frame.PlaybackMix;
    }
    private void FinishTransition()
    {
        if (_transitionHooked) { CompositionTarget.Rendering -= OnTransitionFrame; _transitionHooked = false; }
        if (_transitionTarget is { } target) ApplyFrame(target);
        _lyricsPanel.Visibility = _lyricsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (CanRender)
        {
            _lastCentered = -1;
            DispatcherQueue.TryEnqueue(() => { if (CanRender && !_transitionRunning) CenterCurrentLyric(); });
        }
    }
    private void OnPointerActivity(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_root);
        EndManualScrollPointer(args, released: false);
        if (!point.Properties.IsLeftButtonPressed && !point.IsInContact)
            _interactingPointers.Remove(args.Pointer.PointerId);
        // XAML can re-hit-test a stationary pointer as the controls move away.
        // Such layout-generated moves must not immediately undo idle presentation.
        if (_lastPointerPosition is { } previous && Math.Abs(previous.X - point.Position.X) < .1 && Math.Abs(previous.Y - point.Position.Y) < .1)
            return;
        _lastPointerPosition = point.Position;
        _keyboardControlsIntent = false;
        NotifyInteraction();
    }
    private void NotifyInteraction()
    {
        if (!CanRender) return;
        _controlsInteractionAt = Stopwatch.GetTimestamp();
        if (!_controlsVisible) { _controlsVisible = true; UpdateLayoutGeometry(); }
        ResetIdleTimer();
    }
    private void ResetIdleTimer()
    {
        _idleTimer.Stop();
        if (!CanRender || !_lyricsVisible) return;
        // One existing UI timer serves two independent deadlines. Transport,
        // volume, resize and focus changes never reset the lyric-reading state.
        var due = double.PositiveInfinity;
        if (!_lyricsIdle && _manualScrollPointers.Count == 0 && !_manualScrollInertia)
            due = Math.Min(due, Math.Max(40, 4_000 - Stopwatch.GetElapsedTime(_lyricInteractionAt).TotalMilliseconds));
        if (_portrait && _controlsVisible && !InteractionHoldsControls())
            due = Math.Min(due, Math.Max(40, 4_000 - Stopwatch.GetElapsedTime(_controlsInteractionAt).TotalMilliseconds));
        if (double.IsFinite(due)) { _idleTimer.Interval = TimeSpan.FromMilliseconds(due); _idleTimer.Start(); }
    }
    private bool GestureInProgress() => _interactingPointers.Count > 0 || _manualScrollPointers.Count > 0 || _seekInteraction.IsDragging;
    private bool InteractionHoldsControls()
    {
        if (GestureInProgress()) return true;
        // WinUI automatically focuses the first control when opening a window.
        // Only real keyboard navigation should keep the portrait controls pinned.
        if (!_keyboardControlsIntent) return false;
        if (_root.XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(_root.XamlRoot) as DependencyObject;
        if (focused is not Control { FocusState: FocusState.Keyboard }) return false;
        for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, _controls) || ReferenceEquals(element, _translation) || ReferenceEquals(element, _lyricsToggle)) return true;
        return false;
    }
    private void OnIdle()
    {
        if (!CanRender || !_lyricsVisible) return;
        if (!_lyricsIdle && _manualScrollPointers.Count == 0 && !_manualScrollInertia &&
            Stopwatch.GetElapsedTime(_lyricInteractionAt).TotalMilliseconds >= 4_000)
        { _lyricsIdle = true; _lyricFocusDirty = true; RequestRender(); }
        // Keyboard focus keeps its controls reachable, but a resting focus ring
        // is not ongoing interaction and must not suppress lyric focus effects.
        if (_portrait && _controlsVisible && !InteractionHoldsControls() &&
            Stopwatch.GetElapsedTime(_controlsInteractionAt).TotalMilliseconds >= 4_000)
        { _controlsVisible = false; UpdateLayoutGeometry(); }
        ResetIdleTimer();
    }
    private void OnManualScroll()
    {
        _lyricInteractionAt = Stopwatch.GetTimestamp();
        if (_lyricsIdle) { _lyricsIdle = false; _lyricFocusDirty = true; RequestRender(); }
        _manualScrollPending = true;
        ScheduleManualReturn();
        NotifyInteraction();
    }
    private void OnLyricsViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        _lyricFocusDirty = true; RequestRender();
        // Only explicit pointer/wheel input starts this state. Our ChangeView follow
        // events can never create another manual-scroll timeout.
        if (!_manualScrollPending) return;
        _lyricInteractionAt = Stopwatch.GetTimestamp();
        _manualScrollInertia = args.IsIntermediate;
        ScheduleManualReturn();
        ResetIdleTimer();
    }
    private void EndManualScrollPointer(PointerRoutedEventArgs args, bool released)
    {
        if (!_manualScrollPointers.Contains(args.Pointer.PointerId)) return;
        var point = args.GetCurrentPoint(_lyricsScroll);
        if (!released && (point.IsInContact || point.Properties.IsLeftButtonPressed)) return;
        _manualScrollPointers.Remove(args.Pointer.PointerId);
        _lyricInteractionAt = Stopwatch.GetTimestamp();
        ScheduleManualReturn();
        ResetIdleTimer();
    }
    private void ScheduleManualReturn()
    {
        _manualFollowTimer.Stop();
        if (_manualScrollPending && _manualScrollPointers.Count == 0 && !_manualScrollInertia && CanRender && _lyricsVisible)
            _manualFollowTimer.Start();
    }
    private void CompleteManualScroll()
    {
        if (!_manualScrollPending || _manualScrollPointers.Count > 0 || _manualScrollInertia || !CanRender) return;
        _manualScrollPending = false;
        _lastCentered = -1; CenterCurrentLyric();
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_closed) return;
        var property = args.PropertyName;
        if (string.IsNullOrEmpty(property) || property is nameof(MediaViewModel.Settings) or nameof(MediaViewModel.Session) or nameof(MediaViewModel.Artwork) or nameof(MediaViewModel.IsPlaying) or nameof(MediaViewModel.IsReducedMotion))
            _metadataDirty = true;
        if (string.IsNullOrEmpty(property) || property is nameof(MediaViewModel.Settings) or nameof(MediaViewModel.LyricsLines) or nameof(MediaViewModel.CurrentLyricIndex) or nameof(MediaViewModel.LyricsStatusText))
            _lyricsDirty = true;
        if (property == nameof(MediaViewModel.IsReducedMotion)) UpdateOwnership();
        if (property is nameof(MediaViewModel.IsPresentationVisible) or nameof(MediaViewModel.IsSpectrumPresentationVisible) or nameof(MediaViewModel.IsMusicWindowAudioActive) or nameof(MediaViewModel.IsIslandGlowActive)) return;
        RequestRender();
    }

    private void RequestRender() { if (_renderQueue.Request(CanRender)) CompositionTarget.Rendering += OnRendering; }
    private void OnRendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnRendering;
        if (_renderQueue.BeginRender(CanRender)) Render();
    }
    private void CancelRender() { CompositionTarget.Rendering -= OnRendering; _renderQueue.Cancel(); }

    private void Render()
    {
        _updating = true;
        try
        {
            if (!string.Equals(_trackIdentity, _media.Session.TrackIdentity, StringComparison.Ordinal))
            {
                _trackIdentity = _media.Session.TrackIdentity; CancelSeek(); _activeLyric = _lastCentered = -1;
                _audioResponse.Reset(); _audioResponseTick = 0;
                _manualFollowTimer.Stop(); _manualScrollPending = _manualScrollPointers.Count > 0; _manualScrollInertia = false;
                _metadataDirty = _lyricsDirty = true;
            }
            if (_metadataDirty)
            {
                _metadataDirty = false; RefreshLanguage();
                _lyricFocusDirty = true;
                _title.Text = string.IsNullOrWhiteSpace(_media.Title) ? _strings.Get("MediaNoSession.Text") : _media.Title;
                _artist.Text = _media.Artist; _artwork.Source = _media.Artwork;
                _noArtwork.Visibility = _media.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
                UpdateLayoutGeometry();
                AutomationProperties.SetName(_play, _media.PlayPauseLabel);
                _background.SetPlaybackState(_media.IsPlaying, _media.IsReducedMotion, CanRender);
                if (!ReferenceEquals(_backgroundArtwork, _media.Session.Artwork))
                {
                    _backgroundArtwork = _media.Session.Artwork;
                    _ = UpdateBackgroundAsync(_backgroundArtwork);
                }
            }
            var seekEnabled = _media.Session.CanSeek && !_media.PositionEstimated;
            if (!seekEnabled && (_seekInteraction.IsDragging || _seekInteraction.IsPreviewing)) CancelSeek();
            _seek.IsEnabled = seekEnabled; _seek.Maximum = _media.DurationSeconds;
            var playback = _media.PositionEstimated ? 0 : Math.Clamp(_media.PositionSeconds, 0, _seek.Maximum);
            if (_seekInteraction.ShouldApplyPlayback(playback, DateTimeOffset.UtcNow, seekEnabled)) _seek.Value = playback;
            ToolTipService.SetToolTip(_seek, seekEnabled ? null : _strings.Get("MusicWindowSeekUnavailable"));
            UpdateTimeLabels(); RefreshVolume();
            _error.Text = _media.ControlError.Length > 0 ? _media.ControlError :
                _volumeWriteFailed ? _strings.Get("MusicWindowVolumeUnavailable") : _editor.Error;
            ToolTipService.SetToolTip(_error, _error.Text);
            _error.Visibility = string.IsNullOrWhiteSpace(_error.Text) ? Visibility.Collapsed : Visibility.Visible;
            var spectrum = _media.Spectrum;
            var realAudio = _media.IsPlaying && spectrum.CaptureMode == AudioCaptureMode.ProcessLoopback && spectrum.Bands.Count > 0;
            var now = Stopwatch.GetTimestamp();
            var elapsed = _audioResponseTick == 0 ? TimeSpan.FromMilliseconds(33) : Stopwatch.GetElapsedTime(_audioResponseTick, now);
            _audioResponseTick = now;
            if (realAudio)
                _audioResponse.Advance(spectrum.Bands.Where(double.IsFinite).DefaultIfEmpty().Max(), spectrum.Bands, elapsed);
            else _audioResponse.Reset();
            // Spectrum bars are already dB-normalized. Reuse the island's local
            // contrast response so sustained loudness does not flatten every beat.
            _background.UpdateSpectrum((float)_audioResponse.Energy, realAudio);
            if (_lyricsDirty) { _lyricsDirty = false; RefreshLyrics(); }
            CenterCurrentLyric();
            if (_activeLyric >= 0 && _activeLyric < _rows.Count)
                _rows[_activeLyric].Highlight(_media.Lyrics, true, _media.Settings.Lyrics.WordSyncedHighlighting);
            if (_lyricFocusDirty) RefreshLyricFocus();
        }
        finally { _updating = false; }
    }

    private void RefreshLanguage()
    {
        Title = _strings.Get("MusicWindowTitle"); _root.Language = _strings.Culture.Name;
        _noArtworkText.Text = _strings.Get("MusicWindowNoArtwork");
        AutomationProperties.SetName(_translation, _strings.Get("MusicWindowTranslation"));
        ToolTipService.SetToolTip(_translation, _strings.Get("MusicWindowTranslation"));
        _translation.IsChecked = _media.Settings.Lyrics.SecondaryLyrics;
        var lyricsLabel = _strings.Get(_lyricsVisible ? "MusicWindowHideLyrics" : "MusicWindowShowLyrics");
        AutomationProperties.SetName(_lyricsToggle, lyricsLabel); ToolTipService.SetToolTip(_lyricsToggle, lyricsLabel);
        AutomationProperties.SetName(_previous, _strings.Get("MediaPrevious.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"));
        AutomationProperties.SetName(_next, _strings.Get("MediaNext.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"));
        AutomationProperties.SetName(_seek, _strings.Get("MediaSeek.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"));
        AutomationProperties.SetName(_volumeSlider, _strings.Get("MusicWindowSystemVolume"));
        ToolTipService.SetToolTip(_volumeSlider, _strings.Get("MusicWindowSystemVolume"));
    }

    private static Microsoft.UI.Composition.CubicBezierEasingFunction Ease(Microsoft.UI.Composition.Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.18f, 0.76f), new Vector2(0.22f, 1));

    private void AddButtonMotion(ButtonBase button)
    {
        var over = false;
        button.PointerEntered += (_, _) => { over = true; AnimateButton(button, 1.045f); };
        button.PointerExited += (_, _) => { over = false; AnimateButton(button, 1); };
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => AnimateButton(button, 0.94f)), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => AnimateButton(button, over ? 1.045f : 1)), true);
        button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => AnimateButton(button, 1)), true);
        if (button is ToggleButton toggle)
        {
            foreach (var suffix in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
                toggle.Resources["ToggleButtonBackground" + suffix] = new SolidColorBrush(ColorHelper.FromArgb(36, 255, 255, 255));
            foreach (var suffix in new[] { "", "PointerOver", "Pressed", "Checked", "CheckedPointerOver", "CheckedPressed" })
                toggle.Resources["ToggleButtonForeground" + suffix] = new SolidColorBrush(Colors.White);
        }
    }
    private void AnimateButton(FrameworkElement button, float scale)
    {
        if (_closed) return;
        var visual = ElementCompositionPreview.GetElementVisual(button);
        visual.CenterPoint = new Vector3((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, 0);
        if (_media.IsReducedMotion) { visual.StopAnimation("Scale"); visual.Scale = Vector3.One; return; }
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, new Vector3(scale, scale, 1), Ease(visual.Compositor));
        animation.Duration = TimeSpan.FromMilliseconds(scale < 1 ? 100 : 200); visual.StartAnimation("Scale", animation);
    }
    private async Task UpdateBackgroundAsync(byte[]? artwork)
    {
        try { await _background.SetArtworkAsync(artwork, _stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _logger.LogDebug(exception, "Music background artwork could not be refreshed."); }
    }
    private void OnBackgroundFaulted(object? sender, Exception exception) => _logger.LogDebug(exception, "Music background rendering unavailable.");

    private void RefreshLyrics()
    {
        var lines = _media.LyricsLines;
        var hasLyrics = _media.Settings.Lyrics.Enabled && lines.Count > 0;
        _lyricsScroll.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;
        _lyricsEmpty.Visibility = hasLyrics ? Visibility.Collapsed : Visibility.Visible;
        _lyricsStatus.Text = string.IsNullOrWhiteSpace(_media.Title) ? _strings.Get("MediaNoSession.Text") :
            !_media.Settings.Lyrics.Enabled ? _strings.Get("MusicWindowLyricsDisabled") :
            string.IsNullOrWhiteSpace(_media.LyricsStatusText) ? _strings.Get("LyricsNotFound") : _media.LyricsStatusText;
        var target = LyricsTranslationTargetPolicy.ToLanguageTag(_media.Settings.LyricsTranslationTarget);
        var options = $"{target}|{_media.Settings.Lyrics.SecondaryLyrics}|{_media.Settings.Lyrics.ShowAiLyricsLabel}|{_media.Settings.Lyrics.OriginalFontSize}|{_media.Settings.Lyrics.TranslationFontSize}";
        if (_lyricsOptions != options) { _lyricsOptions = options; _lastCentered = -1; }
        if (_rows.Update(lines, _media.Session.TrackIdentity, options))
        {
            _lyricsRows.Children.Clear();
            for (var i = 0; i < _rows.Count; i++) _lyricsRows.Children.Add(_rows[i]);
            _activeLyric = -1; _lastCentered = -1;
        }
        if (_activeLyric != _media.CurrentLyricIndex)
        {
            if (_activeLyric >= 0 && _activeLyric < _rows.Count) _rows[_activeLyric].Highlight(LyricsHighlightFrame.Empty, false, false);
            _activeLyric = _media.CurrentLyricIndex;
        }
        _lyricFocusDirty = true;
        var hasTranslation = lines.Any(line => !string.IsNullOrWhiteSpace(LyricsDisplayPolicy.SecondaryPresentation(line, target, true, _media.Settings.Lyrics.ShowAiLyricsLabel)));
        _translationStatus.Text = _strings.Get("MusicWindowNoTranslation");
        _translationStatus.Visibility = hasLyrics && _media.Settings.Lyrics.SecondaryLyrics && !hasTranslation ? Visibility.Visible : Visibility.Collapsed;
        CenterCurrentLyric();
    }

    private MusicLyricRow CreateLyricRow(LyricsLine line)
    {
        var row = new MusicLyricRow();
        row.SizeChanged += (_, _) => { _lastCentered = -1; _lyricFocusDirty = true; RequestRender(); };
        UpdateLyricRow(row, line); return row;
    }
    private void UpdateLyricRow(MusicLyricRow row, LyricsLine line)
    {
        var secondary = LyricsDisplayPolicy.SecondaryPresentation(line,
            LyricsTranslationTargetPolicy.ToLanguageTag(_media.Settings.LyricsTranslationTarget),
            _media.Settings.Lyrics.Enabled && _media.Settings.Lyrics.SecondaryLyrics, _media.Settings.Lyrics.ShowAiLyricsLabel);
        row.Update(line, secondary, Math.Clamp(_media.Settings.Lyrics.OriginalFontSize * 1.6, 28, 46),
            Math.Clamp(_media.Settings.Lyrics.TranslationFontSize * 1.4, 18, 30));
    }
    private void RefreshLyricFocus()
    {
        _lyricFocusDirty = false;
        if (!_lyricsScroll.IsLoaded || _lyricsScroll.ViewportHeight <= 0) return;
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            var top = row.TransformToVisual(_lyricsScroll).TransformPoint(new Point()).Y;
            row.Focus(_activeLyric < 0 ? 1 : Math.Abs(index - _activeLyric), _lyricsIdle,
                top, _lyricsScroll.ViewportHeight, _media.IsReducedMotion);
        }
    }
    private void CenterCurrentLyric()
    {
        var index = _media.CurrentLyricIndex;
        if (!CanRender || !_lyricsVisible || _transitionRunning || _manualScrollPending || _manualScrollPointers.Count > 0 || index < 0 || index >= _rows.Count || index == _lastCentered || !_lyricsScroll.IsLoaded) return;
        try
        {
            var row = _rows[index];
            if (row.ActualHeight <= 0 || _lyricsScroll.ViewportHeight <= 0) return;
            var point = row.TransformToVisual(_lyricsScroll).TransformPoint(new Point());
            var offset = Math.Max(0, _lyricsScroll.VerticalOffset + point.Y - Math.Max(0, (_lyricsScroll.ViewportHeight - row.ActualHeight) / 2));
            _lyricsScroll.ChangeView(null, offset, null, _media.IsReducedMotion); _lastCentered = index;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { _logger.LogDebug("Music lyric layout pending ({Kind}).", exception.GetType().Name); }
    }

    private void UpdateOwnership()
    {
        var active = CanRender;
        _media.SetPresentationVisible(this, active, showSpectrum: false);
        _media.SetMusicWindowAudioActive(this, active && !_media.IsReducedMotion);
        _background.SetPlaybackState(_media.IsPlaying, _media.IsReducedMotion, active);
        if (!active || _media.IsReducedMotion) StopUiMotion();
        if (!active)
        {
            _idleTimer.Stop(); _manualFollowTimer.Stop(); _interactingPointers.Clear(); _manualScrollPointers.Clear();
            _manualScrollPending = _manualScrollInertia = false;
        }
        else ResetIdleTimer();
        if (_volumeDesiredActive != active)
        {
            _volumeDesiredActive = active;
            _volumeLeaseTask = SetVolumeLeaseAsync(_volumeLeaseTask, active);
        }
    }
    private void StopUiMotion()
    {
        _transitionRunning = false; _transitionProgress = 1; FinishTransition();
        foreach (var button in new FrameworkElement[] { _previous, _play, _next, _translation, _lyricsToggle })
        {
            var visual = ElementCompositionPreview.GetElementVisual(button);
            visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
        }
    }
    private async Task SetVolumeLeaseAsync(Task previous, bool active)
    {
        try { await previous; await _volume.SetControlActiveAsync(this, active); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _logger.LogDebug("Music volume observation unavailable ({Kind}).", exception.GetType().Name); }
        if (!_closed) DispatcherQueue.TryEnqueue(RequestRender);
    }
    private void OnVolumeChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() => { if (!_closed) RequestRender(); });
    private void RefreshVolume()
    {
        _volumeSlider.IsEnabled = _volume.IsAvailable && _volume.Current is not null;
        ToolTipService.SetToolTip(_volumeSlider, _strings.Get(_volumeSlider.IsEnabled ? "MusicWindowSystemVolume" : "MusicWindowVolumeUnavailable"));
        if (_volume.Current is not { } state) return;
        if (_queuedVolume is null && !_volumeWriting) _volumeSlider.Value = state.Percent;
        _volumeIcon.Glyph = state.Muted ? "\uE74F" : "\uE994";
    }
    private async Task WriteVolumeAsync()
    {
        _volumeWriting = true;
        try
        {
            while (!_closed && !_minimized && _queuedVolume is { } value)
            {
                _queuedVolume = null;
                await _volume.SetMasterVolumeAsync(Math.Clamp(value, 0, 1), _stop.Token);
                _volumeWriteFailed = false;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _volumeWriteFailed = true;
            _logger.LogDebug("Music system volume update unavailable ({Kind}).", exception.GetType().Name);
        }
        finally { _volumeWriting = false; if (!_closed) RequestRender(); }
    }

    private void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_updating || !CanRender || !_seek.IsEnabled || !double.IsFinite(args.NewValue)) return;
        var value = Math.Clamp(args.NewValue, 0, _seek.Maximum);
        if (_seekInteraction.IsPendingTarget(value)) return;
        _seekAcknowledgement.Stop(); _seekInteraction.Preview(value); UpdateTimeLabels();
        if (_seekInteraction.IsDragging) return;
        _queuedSeek = value; _seekCommit.Stop(); _seekCommit.Start();
    }
    private void OnSeekPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_seek);
        if (!CanRender || !_seek.IsEnabled || _seekPointer is not null || (!point.IsInContact && !point.Properties.IsLeftButtonPressed)) return;
        _seekPointer = args.Pointer.PointerId; _seekAcknowledgement.Stop(); _seekCommit.Stop(); _queuedSeek = null; _seekInteraction.Begin(_seek.Value);
    }
    private void OnSeekReleased(object sender, PointerRoutedEventArgs args) { if (_seekPointer == args.Pointer.PointerId) FinishSeek(false); }
    private void OnSeekCanceled(object sender, PointerRoutedEventArgs args) { if (_seekPointer == args.Pointer.PointerId) FinishSeek(true); }
    private void OnSeekCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_seekPointer != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(_seek); FinishSeek(point.IsInContact || point.Properties.IsLeftButtonPressed);
    }
    private void FinishSeek(bool canceled)
    {
        _seekPointer = null; _seekCommit.Stop(); _queuedSeek = null; _seekInteraction.Preview(_seek.Value);
        if (_seekInteraction.Complete(canceled || !CanRender || !_seek.IsEnabled, DateTimeOffset.UtcNow) is { } value) ExecuteSeek(value);
        else RequestRender();
    }
    private void OnSeekCommit(DispatcherQueueTimer sender, object args)
    {
        if (!CanRender || _seekInteraction.IsDragging || _queuedSeek is not { } value) return;
        _queuedSeek = null; ExecuteSeek(_seekInteraction.Commit(value, DateTimeOffset.UtcNow));
    }
    private void ExecuteSeek(double value)
    {
        if (CanRender && _media.SeekCommand.CanExecute(value))
        {
            _media.SeekCommand.Execute(value); _seekAcknowledgement.Stop(); _seekAcknowledgement.Start();
        }
        else { _seekInteraction.RejectPending(); RequestRender(); }
    }
    private void CancelSeek()
    {
        _seekCommit.Stop(); _seekAcknowledgement.Stop(); _queuedSeek = null; _seekPointer = null; _seekInteraction.Reset();
    }
    private void UpdateTimeLabels()
    {
        if (_seekInteraction.HeldSeconds is { } seconds)
        {
            _elapsed.Text = FormatSeconds(seconds); _remaining.Text = "−" + FormatSeconds(Math.Max(0, _seek.Maximum - seconds));
        }
        else { _elapsed.Text = _media.ElapsedText; _remaining.Text = _media.RemainingText; }
    }
    private static string FormatSeconds(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        _closed = true; _idleTimer.Stop(); _manualFollowTimer.Stop(); UpdateOwnership(); CancelRender(); CancelSeek(); _volumeCommit.Stop(); _queuedVolume = null;
        _media.PropertyChanged -= OnMediaChanged; _volume.SnapshotChanged -= OnVolumeChanged;
        AppWindow.Changed -= OnAppWindowChanged; _background.Faulted -= OnBackgroundFaulted;
        _stop.Cancel(); _background.Dispose(); _artwork.Source = null; _backgroundArtwork = null;
        _lyricsRows.Children.Clear();
    }
}
