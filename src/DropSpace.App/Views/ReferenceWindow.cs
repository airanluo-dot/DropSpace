using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace DropSpace.App.Views;

/// <summary>A view over the selected item. The service owns its identity and lifetime.</summary>
public sealed class ReferenceWindow : Window
{
    private readonly IAppStringLocalizer _strings;
    private readonly Grid _root;
    private readonly ToggleButton _topmost = new() { IsChecked = true };
    private readonly Button _copy = new() { IsEnabled = false };
    private readonly Button _zoomOut = new() { Content = new FontIcon { Glyph = "\uE71F" } };
    private readonly Button _zoomIn = new() { Content = new FontIcon { Glyph = "\uE71E" } };
    private readonly Button _fit = new();
    private readonly TextBlock _zoomValue = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _zoomControls = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _heading = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, FontSize = 16 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer _scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        ZoomMode = ZoomMode.Disabled,
    };
    private string? _statusKey;
    private string _itemTitle = string.Empty;
    private double _imageWidth, _imageHeight, _zoom = 1;
    private bool _fitImage = true;
    private bool _closed;

    public ReferenceWindow(IAppStringLocalizer strings)
    {
        _strings = strings;
        foreach (var button in new[] { _zoomOut, _zoomIn })
        {
            button.Style = (Style)Application.Current.Resources["DropSpaceIconButtonStyle"];
            ((FontIcon)button.Content).Style = (Style)Application.Current.Resources["DropSpaceActionIconStyle"];
        }
        _root = new Grid
        {
            Padding = new Thickness(16), RowSpacing = 12,
            Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"],
            Language = strings.Culture.Name,
        };
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.Children.Add(_heading);

        var toolbar = new Grid { ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _topmost.HorizontalAlignment = HorizontalAlignment.Left;
        toolbar.Children.Add(_topmost);
        Grid.SetColumn(_copy, 1); toolbar.Children.Add(_copy);
        Grid.SetRow(toolbar, 1); _root.Children.Add(toolbar);

        var body = new Grid();
        body.Children.Add(_scroller); body.Children.Add(_status);
        Grid.SetRow(body, 2); _root.Children.Add(body);
        _zoomControls.Children.Add(_zoomOut); _zoomControls.Children.Add(_zoomValue);
        _zoomControls.Children.Add(_zoomIn); _zoomControls.Children.Add(_fit);
        Grid.SetRow(_zoomControls, 3); _root.Children.Add(_zoomControls);
        Content = _root;

        AppWindow.Resize(new SizeInt32(560, 560));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
        }
        NativeApplicationIcon.ApplyToWindow(WindowNative.GetWindowHandle(this), AppWindow);
        _topmost.Checked += OnTopmostChanged; _topmost.Unchecked += OnTopmostChanged;
        _copy.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);
        _zoomOut.Click += (_, _) => ChangeZoom(_zoom / 1.25);
        _zoomIn.Click += (_, _) => ChangeZoom(_zoom * 1.25);
        _fit.Click += (_, _) => { _fitImage = true; UpdateImageSize(); };
        _scroller.SizeChanged += (_, _) => { if (_fitImage) UpdateImageSize(); };
        _root.ActualThemeChanged += (_, _) =>
            _root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        Closed += (_, _) => { _closed = true; _image.Source = null; _text.Text = string.Empty; };
        AutomationProperties.SetAutomationId(_copy, "ReferenceCopy");
        AutomationProperties.SetAutomationId(_topmost, "ReferenceTopmost");
        AutomationProperties.SetAutomationId(_scroller, "ReferenceContent");
        AutomationProperties.SetAutomationId(_zoomIn, "ReferenceZoomIn");
        AutomationProperties.SetAutomationId(_zoomOut, "ReferenceZoomOut");
        RefreshLanguage();
    }

    public event EventHandler? CopyRequested;
    internal string? CurrentText => ReferenceEquals(_scroller.Content, _text) && _copy.IsEnabled ? _text.Text : null;
    internal bool HasContent => _copy.IsEnabled;
    internal bool IsAlwaysOnTop => _topmost.IsChecked == true;
    internal double ZoomFactor => _zoom;
    internal void SetAlwaysOnTop(bool enabled) => _topmost.IsChecked = enabled;
    internal void SetZoom(double scale) => ChangeZoom(scale);
    internal void FitImage() { _fitImage = true; UpdateImageSize(); }
    internal void ScrollTo(double horizontal, double vertical) => _scroller.ChangeView(horizontal,vertical,null,disableAnimation:true);

    public void RefreshLanguage()
    {
        Title = string.IsNullOrEmpty(_itemTitle) ? _strings.Get("ReferenceWindowTitle") :
            string.Concat(_itemTitle, " — ", _strings.Get("ReferenceWindowTitle"));
        _root.Language = _strings.Culture.Name;
        _topmost.Content = _strings.Get("ReferenceKeepOnTop");
        _copy.Content = _strings.Get("CopyItemButton.Content");
        _fit.Content = _strings.Get("ReferenceFitImage");
        AutomationProperties.SetName(_zoomIn, _strings.Get("ReferenceZoomIn"));
        AutomationProperties.SetName(_zoomOut, _strings.Get("ReferenceZoomOut"));
        AutomationProperties.SetName(_fit, _strings.Get("ReferenceFitImage"));
        if (_statusKey is not null) _status.Text = _strings.Get(_statusKey);
        UpdateZoomLabel();
    }

    public void SetItemTitle(string title)
    {
        _itemTitle = title; _heading.Text = title;
        ToolTipService.SetToolTip(_heading, title);
        RefreshLanguage();
    }

    public void ShowStatus(string resourceKey)
    {
        _statusKey = resourceKey; _status.Text = _strings.Get(resourceKey);
        _status.Visibility = Visibility.Visible; _scroller.Visibility = Visibility.Collapsed;
        _zoomControls.Visibility = Visibility.Collapsed; _copy.IsEnabled = false;
        _image.Source = null; _text.Text = string.Empty;
    }

    public void ShowCopyFailure()
    {
        // The existing preview stays usable, while the failure remains explicit.
        _statusKey = "ReferenceCopyFailed"; _status.Text = _strings.Get(_statusKey);
        _status.Visibility = Visibility.Visible;
    }

    public void ShowText(string text, bool resetPosition)
    {
        if (_closed) return;
        _text.Text = text; _image.Source = null;
        _scroller.Content = _text;
        _scroller.HorizontalScrollMode = ScrollMode.Disabled;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ShowContent(image: false);
        if (resetPosition) _scroller.ChangeView(0, 0, null, disableAnimation: true);
    }

    public async Task ShowImageAsync(byte[] bytes, bool resetPosition, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync(); await writer.FlushAsync(); writer.DetachStream();
        }
        var decoder = await ImageDecoderPreflight.ValidateAsync(stream, 25L * 1024 * 1024,
            DropSpace.Core.Preview.PreviewLimits.DefaultImagePixels, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = checked((int)Math.Min(decoder.PixelWidth, 4096u)) };
        await bitmap.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed) return;
        _imageWidth = decoder.PixelWidth; _imageHeight = decoder.PixelHeight;
        _image.Source = bitmap; _text.Text = string.Empty;
        _scroller.Content = _image;
        _scroller.HorizontalScrollMode = ScrollMode.Enabled;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        if (resetPosition) _fitImage = true;
        ShowContent(image: true); UpdateImageSize();
        if (resetPosition) _scroller.ChangeView(0, 0, null, disableAnimation: true);
    }

    private void ShowContent(bool image)
    {
        _statusKey = null; _status.Visibility = Visibility.Collapsed;
        _scroller.Visibility = Visibility.Visible; _copy.IsEnabled = true;
        _zoomControls.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs args)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _topmost.IsChecked == true;
    }

    private void ChangeZoom(double zoom)
    {
        _fitImage = false; _zoom = Math.Clamp(zoom, 0.05, 8); UpdateImageSize();
    }

    private void UpdateImageSize()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0 || !ReferenceEquals(_scroller.Content, _image)) return;
        if (_fitImage && _scroller.ActualWidth > 0 && _scroller.ActualHeight > 0)
            _zoom = Math.Clamp(Math.Min((_scroller.ActualWidth - 8) / _imageWidth,
                (_scroller.ActualHeight - 8) / _imageHeight), 0.01, 1);
        _image.Width = _imageWidth * _zoom; _image.Height = _imageHeight * _zoom;
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel() => _zoomValue.Text = _zoom.ToString("P0", _strings.Culture);
}
