using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Compatibility;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Network;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace DropSpace.App;

public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<MainWindow> _logger;
    private Views.MainPage _mainPage;
    private readonly Func<Views.MainPage> _createMainPage;
    private AppLanguagePreference _displayLanguage;
    private readonly MediaViewModel _media;
    private NativeTrayService? _tray;
    private bool _allowClose;
    private bool _startupInteractionEnabled = true;
    private bool _closeExplanationInProgress;
    private readonly CancellationTokenSource _closeExplanationCancellation = new();
    private Task? _closeExplanationTask;
    private Task<AppSettings?>? _privacyChoicesTask;

    public MainWindow(
        MainViewModel viewModel,
        IAppStringLocalizer strings,
        ILogger<MainWindow> logger,
        IWindowsCapabilityService capabilities,
        QuickPreviewService previews,
        IItemActionRegistry actions,
        QuickActionDialogService quickActionDialog,
        ILogger<Views.MainPage> pageLogger,
        DeviceHandoffUseCase deviceHandoff,
        CrossDeviceClipboardService crossDeviceClipboard,
        DropLinkHost dropLinkHost,
        SharingUseCase sharing,
        NativeSettingsEditor settingsEditor,
        MediaViewModel media,
        Services.Media.WindowsMediaSessionService sessions,
        Services.Media.MediaExperienceService mediaExperience,
        Services.Media.MediaApplicationIconService mediaIcons,
        NeteaseEnhancementViewModel enhancement,
        Services.Dlc.DlcManagerService dlc)
    {
        _viewModel = viewModel;
        _media = media;
        _strings = strings;
        _logger = logger;
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            foreach (System.Collections.DictionaryEntry detail in exception.Data)
                if (detail.Value is string text)
                    logger.LogError("Main-window XAML diagnostic {Key}: {Description}", detail.Key, text);
            throw new InvalidOperationException("Main-window XAML initialization failed.", exception);
        }

        AppTitleBar.Loaded += (_, _) => XamlResourceOverride.Apply(AppTitleBar, "MainTitleBar");
        XamlResourceOverride.Apply(this, "MainWindow");

        if (capabilities.IsAvailable(WindowsCapability.ModernWindowAppearance) &&
            capabilities.IsAvailable(WindowsCapability.DesktopAcrylic))
        {
            try
            {
                SystemBackdrop = new IslandAcrylicBackdrop();
                // The Win10 base brush keeps the window opaque when Desktop Acrylic is unavailable.
                // Remove it only after the optional Windows 11 backdrop has been installed so
                // the material can show through on supported builds.
                RootSurface.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            catch (Exception exception)
            {
                logger.LogInformation(exception, "Desktop Acrylic is unavailable at runtime; using the base window visual.");
            }
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        NativeApplicationIcon.ApplyToWindow(WindowNative.GetWindowHandle(this), AppWindow);
        AppWindow.Resize(new SizeInt32(980, 680));
        AppWindow.Closing += OnAppWindowClosing;
        _displayLanguage = viewModel.Language;
        _createMainPage = () => new Views.MainPage(
            viewModel,
            WindowNative.GetWindowHandle(this),
            strings,
            capabilities,
            previews,
            actions,
            quickActionDialog,
            pageLogger,
            deviceHandoff,
            crossDeviceClipboard,
            dropLinkHost,
            sharing,
            settingsEditor, media, sessions, mediaExperience, mediaIcons, enhancement, dlc);
        _mainPage = _createMainPage();
        RootContent.Content = _mainPage;
        AppWindow.Changed += OnWindowPresentationChanged;
        _viewModel.PropertyChanged += OnMediaSectionChanged;
    }

    public event EventHandler? ExitRequested;

    private void OnWindowPresentationChanged(AppWindow sender, AppWindowChangedEventArgs args) => UpdateMediaVisibility();
    private void OnMediaSectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.CurrentSection)) UpdateMediaVisibility();
        if (args.PropertyName == nameof(MainViewModel.Language) && _displayLanguage != _viewModel.Language)
        {
            _displayLanguage = _viewModel.Language;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_allowClose) return;
                _mainPage.Retire();
                _mainPage = _createMainPage();
                RootContent.Content = _mainPage;
                XamlResourceOverride.Apply(this, "MainWindow");
                XamlResourceOverride.Apply(AppTitleBar, "MainTitleBar");
            });
        }
    }
    private void UpdateMediaVisibility() => _media.SetPresentationVisible(this, _viewModel.IsMusicVisible && AppWindow.IsVisible &&
        AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized });

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
        PrepareForShutdown();
        Close();
    }

    public void PrepareForShutdown()
    {
        if (_allowClose) return;
        _allowClose = true;
        AppWindow.Changed -= OnWindowPresentationChanged;
        _viewModel.PropertyChanged -= OnMediaSectionChanged;
        _media.SetPresentationVisible(this, false);
        _closeExplanationCancellation.Cancel();
        Views.ContentDialogLifetime.RetireRoot(_mainPage.XamlRoot);
        _mainPage.Retire();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        _tray?.Dispose();
        _tray = null;
    }

    public void SetStartupInteractionEnabled(bool enabled)
    {
        _startupInteractionEnabled = enabled;
        _mainPage.IsEnabled = enabled;
    }

    public Task<AppSettings?> ChooseInitialPrivacyAsync(AppSettings settings, CancellationToken token) =>
        _privacyChoicesTask = ChooseInitialPrivacyCoreAsync(settings, token);

    private async Task<AppSettings?> ChooseInitialPrivacyCoreAsync(AppSettings settings, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _closeExplanationCancellation.Token);
        token = lifetime.Token;
        ShowAndActivate();
        var root = await WaitForXamlRootAsync(token);
        if (root is null) return null;
        var capture = new CheckBox { Content = _strings.Get("FirstRunClipboardChoice"), IsChecked = false };
        var startup = new CheckBox { Content = _strings.Get("FirstRunStartupChoice"), IsChecked = false };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = _strings.Get("FirstRunPrivacyBody"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(capture);
        content.Children.Add(startup);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = _strings.Get("FirstRunPrivacyTitle"), Content = content,
            PrimaryButtonText = _strings.Get("FirstRunContinue"), CloseButtonText = _strings.Get("FirstRunSkip"),
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await Views.ContentDialogLifetime.ShowAsync(dialog, token);
        token.ThrowIfCancellationRequested();
        return settings with
        {
            PrivacyChoicesCompleted = true,
            ClipboardPaused = result != ContentDialogResult.Primary || capture.IsChecked != true,
            StartWithWindows = result == ContentDialogResult.Primary && startup.IsChecked == true,
        };
    }

    public async Task<bool> ShowRecoveryAsync(CancellationToken cancellationToken = default)
    {
        ShowAndActivate();
        var xamlRoot = await WaitForXamlRootAsync(cancellationToken).ConfigureAwait(true);
        if (xamlRoot is null)
        {
            _logger.LogError("A startup recovery dialog could not be shown because the main page never received a XamlRoot.");
            return false;
        }

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = _strings.Get("StartupRecoveryTitle"),
                Content = _strings.Get("StartupRecoveryContent"),
                CloseButtonText = _strings.Get("CommonClose"),
            };
            await Views.ContentDialogLifetime.ShowAsync(dialog, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "The startup recovery dialog could not be displayed.");
            return false;
        }
    }

    private async Task<XamlRoot?> WaitForXamlRootAsync(CancellationToken cancellationToken)
    {
        if (_mainPage.XamlRoot is { } current)
        {
            return current;
        }

        var completion = new TaskCompletionSource<XamlRoot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs args)
        {
            completion.TrySetResult(_mainPage.XamlRoot);
        }

        _mainPage.Loaded += OnLoaded;
        try
        {
            if (_mainPage.XamlRoot is { } attached)
            {
                completion.TrySetResult(attached);
            }

            try
            {
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                return _mainPage.XamlRoot;
            }
        }
        finally
        {
            _mainPage.Loaded -= OnLoaded;
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (!_startupInteractionEnabled)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_viewModel.Settings.CloseBehavior == CloseBehavior.HideToTray && _tray?.IsAvailable == true)
        {
            if (!_viewModel.Settings.CloseExplanationShown && !_closeExplanationInProgress)
            {
                _closeExplanationInProgress = true;
                _closeExplanationTask = ExplainCloseToTrayAsync(_closeExplanationCancellation.Token);
            }
            else
            {
                AppWindow.Hide();
            }

            return;
        }

        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExplainCloseToTrayAsync(CancellationToken cancellationToken)
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
            await Views.ContentDialogLifetime.ShowAsync(dialog, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { CloseExplanationShown = true },
                cancellationToken);
            AppWindow.Hide();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the application cancels the owned explanation task.
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

    public async ValueTask DisposeAsync()
    {
        _closeExplanationCancellation.Cancel();
        if (_closeExplanationTask is not null)
        {
            try
            {
                await _closeExplanationTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_closeExplanationCancellation.IsCancellationRequested)
            {
                // The close explanation is owned by the window and is canceled during shutdown.
            }
        }

        if (_privacyChoicesTask is not null)
        {
            try { await _privacyChoicesTask.ConfigureAwait(true); }
            catch (OperationCanceledException) when (_closeExplanationCancellation.IsCancellationRequested) { }
        }
        _closeExplanationCancellation.Dispose();
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
