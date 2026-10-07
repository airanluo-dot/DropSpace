using System.ComponentModel;
using System.Diagnostics;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views.Music;

public sealed class NeteaseEnhancementCard : UserControl
{
    private readonly NeteaseEnhancementViewModel _view;
    private readonly IAppStringLocalizer _strings;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true };
    private readonly Button _install, _update, _reinstall, _remove;
    private bool _dialogOpen;
    private CancellationTokenSource? _lifetime;
    private int _generation;
    private Task _confirmation = Task.CompletedTask;
    private string _localError = string.Empty;

    public NeteaseEnhancementCard(NeteaseEnhancementViewModel view, IAppStringLocalizer strings)
    {
        _view = view; _strings = strings;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = strings.Get("NeteaseEnhancementTitle"), FontSize = 18,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        content.Children.Add(_status); content.Children.Add(_error); content.Children.Add(_progress);
        _install = MakeButton("NeteaseEnhancementInstall", Install);
        _update = MakeButton("NeteaseEnhancementUpdate", Update);
        _reinstall = MakeButton("NeteaseEnhancementReinstall", Reinstall);
        _remove = MakeButton("NeteaseEnhancementRemove", Remove);
        // Vertical buttons retain readable labels at the minimum window width and large text sizes.
        var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        actions.Children.Add(_install); actions.Children.Add(_update); actions.Children.Add(_reinstall); actions.Children.Add(_remove);
        content.Children.Add(actions);
        Content = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(8),
            Style = (Style)Application.Current.Resources["DropSpaceCardStyle"],
            BorderThickness = new Thickness(1), Child = content };
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        Refresh();
    }

    private Button MakeButton(string key, RoutedEventHandler action)
    {
        var button = new Button { Content = _strings.Get(key) };
        AutomationProperties.SetName(button, _strings.Get(key));
        button.Click += action;
        return button;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_lifetime is not null) return;
        _lifetime = new CancellationTokenSource();
        ++_generation;
        _view.PropertyChanged += Changed;
        await RunAsync(_view.InspectAsync);
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _view.PropertyChanged -= Changed;
        var lifetime = _lifetime;
        _lifetime = null;
        ++_generation;
        _dialogOpen = false;
        lifetime?.Cancel();
        _ = RetireConfirmationAsync(_confirmation, lifetime);
    }
    private static async Task RetireConfirmationAsync(Task task, CancellationTokenSource? lifetime)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { Debug.WriteLine(error.GetType().Name); }
        finally { lifetime?.Dispose(); }
    }
    private bool IsCurrent(int generation) => generation == _generation && _lifetime is { IsCancellationRequested: false };
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (_lifetime is not null) Refresh(); }
    private async void Install(object sender, RoutedEventArgs args) => await StartConfirmationAsync(false);
    private async void Reinstall(object sender, RoutedEventArgs args) => await StartConfirmationAsync(true);
    private async void Update(object sender, RoutedEventArgs args) => await RunAsync(() => _view.EnhanceAsync());
    private async void Remove(object sender, RoutedEventArgs args) => await RunAsync(_view.RemoveAsync);

    private Task StartConfirmationAsync(bool reinstall)
    {
        if (_dialogOpen || _view.IsBusy || _lifetime is null) return Task.CompletedTask;
        return _confirmation = ConfirmInstallAsync(reinstall);
    }

    private async Task ConfirmInstallAsync(bool reinstall)
    {
        if (_dialogOpen || _view.IsBusy || _lifetime is null) return;
        var generation = _generation;
        var token = _lifetime.Token;
        _localError = string.Empty; _dialogOpen = true; Refresh();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = _strings.Get("NeteaseEnhancementConfirmTitle"),
                Content = new TextBlock { Text = _strings.Get("NeteaseEnhancementConfirmBody"), TextWrapping = TextWrapping.Wrap },
                CloseButtonText = _strings.Get("NeteaseEnhancementCancel"),
                PrimaryButtonText = _strings.Get("NeteaseEnhancementConfirm"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ContentDialogLifetime.ShowAsync(dialog, token) == ContentDialogResult.Primary && IsCurrent(generation))
                await _view.EnhanceAsync(reinstall); // Do not cancel an already approved deployment on navigation.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { if (IsCurrent(generation)) ShowError(exception); }
        finally { if (IsCurrent(generation)) { _dialogOpen = false; Refresh(); } }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_lifetime is null) return;
        var generation = _generation;
        _localError = string.Empty;
        try { await action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { if (IsCurrent(generation)) ShowError(exception); }
    }
    private void ShowError(Exception exception)
    {
        Debug.WriteLine($"DropSpace enhancement UI operation failed: {exception.GetType().Name}");
        _localError = _strings.Get("NeteaseEnhancementErrorUnexpected");
        _error.Text = _localError;
        _error.Visibility = Visibility.Visible;
    }
    private void Refresh()
    {
        _status.Text = _view.Status; _error.Text = string.IsNullOrEmpty(_localError) ? _view.Error : _localError;
        _error.Visibility = string.IsNullOrEmpty(_error.Text) ? Visibility.Collapsed : Visibility.Visible;
        _progress.Visibility = _view.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        _install.Visibility = !_view.IsEnhanced && !_view.IsManaged ? Visibility.Visible : Visibility.Collapsed;
        _update.Visibility = _view.IsManaged ? Visibility.Visible : Visibility.Collapsed;
        _reinstall.Visibility = _view.IsManaged ? Visibility.Visible : Visibility.Collapsed;
        _remove.Visibility = _view.IsManaged ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { _install, _update, _reinstall, _remove }) button.IsEnabled = !_view.IsBusy && !_dialogOpen;
    }
}
