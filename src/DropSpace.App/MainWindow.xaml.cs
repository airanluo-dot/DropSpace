using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace DropSpace.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<MainWindow> _logger;
    private readonly Views.MainPage _mainPage;
    private NativeTrayService? _tray;
    private bool _allowClose;
    private bool _closeExplanationInProgress;

    public MainWindow(
        MainViewModel viewModel,
        IAppStringLocalizer strings,
        ILogger<MainWindow> logger)
    {
        _viewModel = viewModel;
        _strings = strings;
        _logger = logger;
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Main-window XAML initialization failed.", exception);
        }

        XamlResourceOverride.Apply(AppTitleBar, "MainTitleBar");
        XamlResourceOverride.Apply(this, "MainWindow");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        NativeApplicationIcon.ApplyToWindow(WindowNative.GetWindowHandle(this), AppWindow);
        AppWindow.Resize(new SizeInt32(980, 680));
        AppWindow.Closing += OnAppWindowClosing;
        _mainPage = new Views.MainPage(viewModel, WindowNative.GetWindowHandle(this), strings);
        RootContent.Content = _mainPage;
    }

    public event EventHandler? ExitRequested;

    public void InitializeTray(ILogger<NativeTrayService> logger)
    {
        if (_tray is not null)
        {
            return;
        }

        try
        {
            _tray = new NativeTrayService(WindowNative.GetWindowHandle(this), _strings, logger);
            _tray.OpenRequested += (_, _) => DispatcherQueue.TryEnqueue(ShowAndActivate);
            _tray.TogglePauseRequested += OnTrayTogglePauseRequested;
            _tray.ClearRequested += (_, _) => DispatcherQueue.TryEnqueue(async () =>
            {
                ShowAndActivate();
                await _mainPage.ConfirmClearAsync(ClearRange.All);
            });
            _tray.ExitRequested += (_, _) => DispatcherQueue.TryEnqueue(() => ExitRequested?.Invoke(this, EventArgs.Empty));
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _tray.Add();
            _tray.SetPaused(_viewModel.IsClipboardPaused);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The notification-area icon could not be initialized.");
            _tray?.Dispose();
            _tray = null;
        }
    }

    public void ApplyTheme(ThemePreference preference)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = preference switch
            {
                ThemePreference.Light => ElementTheme.Light,
                ThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    public void ShowAndActivate()
    {
        AppWindow.Show();
        Activate();
    }

    public void Hide() => AppWindow.Hide();

    public void VerifyLocalizedResources()
    {
        VerifyResourceValue(Title, "MainWindow.Title");
        _mainPage.VerifyLocalizedResources();
        _tray?.VerifyLocalizedResources();
    }

    private void VerifyResourceValue(object? actual, string key)
    {
        if (!string.Equals(actual as string, _strings.Get(key), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Localized main-window resource '{key}' did not resolve.");
        }
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    public async Task ShowRecoveryAsync()
    {
        ShowAndActivate();
        var dialog = new ContentDialog
        {
            XamlRoot = _mainPage.XamlRoot,
            Title = _strings.Get("StartupRecoveryTitle"),
            Content = _strings.Get("StartupRecoveryContent"),
            CloseButtonText = _strings.Get("CommonClose"),
        };
        await dialog.ShowAsync();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_viewModel.Settings.CloseBehavior == CloseBehavior.HideToTray && _tray?.IsAvailable == true)
        {
            if (!_viewModel.Settings.CloseExplanationShown && !_closeExplanationInProgress)
            {
                _closeExplanationInProgress = true;
                _ = ExplainCloseToTrayAsync();
            }
            else
            {
                AppWindow.Hide();
            }

            return;
        }

        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExplainCloseToTrayAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _mainPage.XamlRoot,
                Title = _strings.Get("CloseToTrayTitle"),
                Content = _strings.Get("CloseToTrayContent"),
                PrimaryButtonText = _strings.Get("CommonAcknowledge"),
            };
            await dialog.ShowAsync();
            await _viewModel.UpdateSettingsAsync(_viewModel.Settings with { CloseExplanationShown = true });
            AppWindow.Hide();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The close-to-tray explanation could not be displayed.");
        }
        finally
        {
            _closeExplanationInProgress = false;
        }
    }

    private async void OnTrayTogglePauseRequested(object? sender, EventArgs args)
    {
        try
        {
            await _viewModel.SetClipboardPausedAsync(!_viewModel.IsClipboardPaused);
            _tray?.SetPaused(_viewModel.IsClipboardPaused);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tray pause command failed.");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Settings))
        {
            _tray?.SetPaused(_viewModel.IsClipboardPaused);
            ApplyTheme(_viewModel.Theme);
        }
    }
}
