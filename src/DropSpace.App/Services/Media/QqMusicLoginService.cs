using DropSpace.Core.Abstractions;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Media;

public sealed class QqMusicLoginService(QqMusicSession session, IAppStringLocalizer strings, ILogger<QqMusicLoginService> logger, AppLanguageService language) : IDisposable
{
    private LoginWindow? _window;
    private readonly CancellationTokenSource _stop = new();
    public QqMusicSession Session => session;
    public void Open(Func<CancellationToken, Task> verify, ElementTheme theme)
    {
        if (_window is not null) { _window.Activate(); return; }
        var window = new LoginWindow(session, strings, logger, theme);
        _window = window;
        language.Changed += OnLanguageChanged;
        window.Closed += (_, _) => { language.Changed -= OnLanguageChanged; _window = null; if (window.Saved && !_stop.IsCancellationRequested) _ = VerifySavedAsync(verify); };
        _window.Activate();
    }
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        var window = _window;
        if (window is not null) window.DispatcherQueue.TryEnqueue(window.RefreshLanguage);
    }
    public async Task SignOutAsync()
    {
        _window?.Close();
        await session.SignOutAsync();
    }
    private async Task VerifySavedAsync(Func<CancellationToken, Task> verify)
    {
        try { await verify(_stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { logger.LogWarning("QQ Music connection check incomplete ({Category}).", error.GetType().Name); }
    }
    public void Dispose() { language.Changed -= OnLanguageChanged; _stop.Cancel(); _window?.Close(); }

    private sealed class LoginWindow : Window
    {
        private readonly QqMusicSession _session;
        private readonly IAppStringLocalizer _strings;
        private readonly ILogger _logger;
        private readonly WebView2 _browser = new();
        private readonly TextBlock _heading = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _help = new() { TextWrapping = TextWrapping.Wrap };
        private readonly Button _close = new() { MinWidth = 96 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly Button _save = new() { IsEnabled = false, Visibility = Visibility.Collapsed, MinWidth = 96 };
        private readonly DispatcherQueueTimer _poll;
        private readonly CancellationTokenSource _stop = new();
        private bool _initialized, _closed, _polling, _saving, _ready;
        public bool Saved { get; private set; }

        public LoginWindow(QqMusicSession session, IAppStringLocalizer strings, ILogger logger, ElementTheme theme)
        {
            _session = session; _strings = strings; _logger = logger;
            _poll = DispatcherQueue.CreateTimer(); _poll.Interval = TimeSpan.FromSeconds(1);
            _poll.Tick += OnCookieTick;
            Title = strings.Get("QqMusicLoginTitle");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 760));
            var layout = new Grid { RowSpacing = 16, Padding = new Thickness(24), RequestedTheme = theme, Language = strings.Culture.Name,
                Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] };
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var heading = new StackPanel { Spacing = 8 };
            _heading.Text = Title;
            _help.Text = strings.Get("QqMusicLoginHelp");
            heading.Children.Add(_heading);
            heading.Children.Add(_help);
            layout.Children.Add(heading);
            var browserFrame = new Border { Child = _browser, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] };
            Grid.SetRow(browserFrame, 1); layout.Children.Add(browserFrame);
            var actions = new StackPanel { Spacing = 8 };
            actions.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            _save.Content = strings.Get("QqMusicUseLogin");
            _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_save, "QqMusicUseLogin");
            _save.Click += OnSave;
            _close.Content = strings.Get("QqMusicCloseLogin");
            _close.Click += (_, _) => Close(); buttons.Children.Add(_close); buttons.Children.Add(_save); actions.Children.Add(buttons);
            Grid.SetRow(actions, 2); layout.Children.Add(actions);
            Content = layout;
            layout.Loaded += OnLoaded;
            Closed += (_, _) => { _closed = true; _poll.Stop(); _stop.Cancel(); _browser.Close(); };
        }

        public void RefreshLanguage()
        {
            if (_closed) return;
            Title = _strings.Get("QqMusicLoginTitle");
            _heading.Text = Title;
            _help.Text = _strings.Get("QqMusicLoginHelp");
            _save.Content = _strings.Get("QqMusicUseLogin");
            _close.Content = _strings.Get("QqMusicCloseLogin");
            _status.Text = _strings.Relocalize(_status.Text);
            if (Content is FrameworkElement root) root.Language = _strings.Culture.Name;
        }

        private async void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_initialized) return;
            _initialized = true;
            _status.Text = _strings.Get("QqMusicOpening");
            try
            {
                await _session.LoadAsync(_stop.Token);
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _session.BrowserDirectory, null);
                if (_closed) return;
                var options = environment.CreateCoreWebView2ControllerOptions();
                // The official login page owns password/QR interaction. Only selected
                // music cookies are retained in our encrypted store after this window closes.
                options.ProfileName = "QqMusicLogin";
                options.IsInPrivateModeEnabled = true;
                await _browser.EnsureCoreWebView2Async(environment, options);
                if (_closed) { _browser.Close(); return; }
                var core = _browser.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.PermissionRequested += (_, permission) => permission.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, download) => download.Cancel = true;
                core.NavigationStarting += (_, navigation) =>
                {
                    if (!TrustedNavigation(navigation.Uri))
                    { navigation.Cancel = true; _status.Text = _strings.Get("QqMusicNavigationBlocked"); }
                };
                core.NewWindowRequested += (_, request) =>
                {
                    request.Handled = true;
                    if (TrustedNavigation(request.Uri)) core.Navigate(request.Uri);
                };
                foreach (var saved in _session.State is QqMusicSessionState.Expired or QqMusicSessionState.Rejected
                    ? [] : _session.GetBrowserCookies())
                {
                    var cookie = core.CookieManager.CreateCookie(saved.Name, saved.Value, saved.Domain, saved.Path);
                    cookie.IsSecure = true; cookie.IsHttpOnly = saved.HttpOnly;
                    if (saved.Expires is { } expiry) cookie.Expires = expiry;
                    core.CookieManager.AddOrUpdateCookie(cookie);
                }
                core.NavigationCompleted += (_, navigation) =>
                {
                    if (_closed) return;
                    if (!_ready) _status.Text = _strings.Get(navigation.IsSuccess ? "QqMusicCompleteLogin" : "QqMusicPageFailed");
                };
                core.Navigate("https://y.qq.com/n/ryqq_v2/search?w=Happier");
                _poll.Start();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _logger.LogWarning("QQ Music sign-in page unavailable ({Category}, 0x{HResult:X8}).", error.GetType().Name, error.HResult);
                if (!_closed) _status.Text = _strings.Get("QqMusicPageFailed");
            }
        }

        private async Task<List<QqMusicCookie>> CollectCookiesAsync()
        {
            var collected = new List<QqMusicCookie>();
            foreach (var origin in new[] { "https://y.qq.com/", "https://u.y.qq.com/", "https://c.y.qq.com/" })
            {
                var cookies = await _browser.CoreWebView2.CookieManager.GetCookiesAsync(origin);
                _stop.Token.ThrowIfCancellationRequested();
                collected.AddRange(cookies.Select(cookie => new QqMusicCookie { Name = cookie.Name, Value = cookie.Value,
                    Domain = cookie.Domain, Path = cookie.Path, Expires = cookie.IsSession ? null : cookie.Expires, HttpOnly = cookie.IsHttpOnly }));
            }
            return collected;
        }

        private async void OnCookieTick(DispatcherQueueTimer sender, object args)
        {
            if (_closed || _polling || _saving) return;
            _polling = true;
            try
            {
                var ready = QqMusicSession.HasUsableSession(await CollectCookiesAsync());
                if (_closed || ready == _ready) return;
                _ready = ready;
                _save.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
                _save.IsEnabled = ready;
                _status.Text = _strings.Get(ready ? "QqMusicLoginReady" : "QqMusicCompleteLogin");
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { _poll.Stop(); if (!_closed) _status.Text = _strings.Get("QqMusicPageFailed"); }
            finally { _polling = false; }
        }

        private async void OnSave(object sender, RoutedEventArgs args)
        {
            if (_closed || !_save.IsEnabled) return;
            _saving = true;
            _save.IsEnabled = false;
            try
            {
                var collected = await CollectCookiesAsync();
                await _session.SaveAsync(collected, _stop.Token);
                Saved = true;
                if (_closed) return;
                Close();
            }
            catch (InvalidDataException) { if (!_closed) _status.Text = _strings.Get("QqMusicLoginMissing"); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!_closed) _status.Text = _strings.Get("QqMusicSaveFailed"); }
            finally { _saving = false; if (!_closed) _save.IsEnabled = _ready; }
        }

        private static bool TrustedNavigation(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host is
                "y.qq.com" or "graph.qq.com" or "xui.ptlogin2.qq.com" or "ui.ptlogin2.qq.com" or "ssl.ptlogin2.qq.com" or "ptlogin2.qq.com" or "open.weixin.qq.com";
    }
}
