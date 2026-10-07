using DropSpace.App.Services.Media;
using DropSpace.Core.Abstractions;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views.Music;

public sealed class QqMusicLoginCard : UserControl
{
    private readonly QqMusicLoginService _login;
    private readonly IAppStringLocalizer _strings;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _signOut = new(), _check = new();
    private readonly Button _signIn = new();
    private bool _busy;
    public QqMusicLoginCard(MediaExperienceService experience, IAppStringLocalizer strings)
    {
        _login = experience.QqMusicLogin; _strings = strings;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = strings.Get("QqMusicLoginTitle"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AutomationProperties.SetAutomationId(_signIn, "QqMusicSignIn");
        _signIn.Click += (_, _) => _login.Open(experience.VerifyQqMusicAsync, ActualTheme);
        _check.Content = strings.Get("QqMusicCheck");
        _check.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; Refresh(); _status.Text = strings.Get("QqMusicChecking");
            try { await experience.VerifyQqMusicAsync(default); _busy = false; Refresh(); }
            catch (Exception error) when (error is not OutOfMemoryException) { _status.Text = strings.Get("QqMusicCheckFailed"); }
            finally { _busy = false; _check.IsEnabled = true; _signIn.IsEnabled = true; _signOut.IsEnabled = _login.Session.HasSavedSession; }
        };
        _signOut.Content = strings.Get("QqMusicSignOut");
        _signOut.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; Refresh();
            try { await _login.SignOutAsync(); _busy = false; Refresh(); }
            catch (Exception error) when (error is not OutOfMemoryException) { _status.Text = strings.Get("QqMusicSaveFailed"); }
            finally { _busy = false; _check.IsEnabled = true; _signIn.IsEnabled = true; _signOut.IsEnabled = _login.Session.HasSavedSession; }
        };
        buttons.Children.Add(_signIn); buttons.Children.Add(_check); buttons.Children.Add(_signOut);
        body.Children.Add(buttons);
        Content = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(8),
            Style = (Style)Application.Current.Resources["DropSpaceCardStyle"],
            BorderThickness = new Thickness(1), Child = body };
        Loaded += async (_, _) => { _login.Session.Changed += OnChanged; await _login.Session.LoadAsync(); Refresh(); };
        Unloaded += (_, _) => _login.Session.Changed -= OnChanged;
        Refresh();
    }
    private void OnChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() => { if (!_busy) Refresh(); });
    private void Refresh()
    {
        _status.Text = _strings.Get("QqMusicState" + _login.Session.State);
        var signedIn = _login.Session.State is QqMusicSessionState.Saved or QqMusicSessionState.Connected;
        _signIn.Content = _strings.Get(_login.Session.HasSavedSession ? "QqMusicSignInAgain" : "QqMusicSignIn");
        _signIn.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        _signIn.IsEnabled = !_busy;
        _check.Visibility = _login.Session.State == QqMusicSessionState.Rejected ? Visibility.Visible : Visibility.Collapsed;
        _signOut.Visibility = _login.Session.HasSavedSession ? Visibility.Visible : Visibility.Collapsed;
        _check.IsEnabled = !_busy; _signOut.IsEnabled = !_busy && _login.Session.HasSavedSession;
    }
}
