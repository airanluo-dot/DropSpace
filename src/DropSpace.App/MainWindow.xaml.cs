using DropSpace.App.Services;
using DropSpace.App.ViewModels;
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
    private readonly ILogger<MainWindow> _logger;
    private readonly Views.MainPage _mainPage;
    private NativeTrayService? _tray;
    private bool _allowClose;
    private bool _closeExplanationInProgress;

    public MainWindow(MainViewModel viewModel, ILogger<MainWindow> logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(980, 680));
        AppWindow.Closing += OnAppWindowClosing;
        _mainPage = new Views.MainPage(viewModel, WindowNative.GetWindowHandle(this));
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
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            _tray = new NativeTrayService(WindowNative.GetWindowHandle(this), iconPath, logger);
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

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    public async Task ShowRecoveryAsync(string errorCategory)
    {
        ShowAndActivate();
        var dialog = new ContentDialog
        {
            XamlRoot = _mainPage.XamlRoot,
            Title = "DropSpace 无法安全启动",
            Content = $"本地数据初始化失败（{errorCategory}）。为避免覆盖历史记录，应用已停止写入。请保留数据目录并查看本地诊断日志。",
            CloseButtonText = "关闭",
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
                Title = "DropSpace 将在后台继续运行",
                Content = "窗口关闭后，DropSpace 会留在系统托盘并继续记录剪贴板。你可以在设置中改为直接退出。",
                PrimaryButtonText = "知道了",
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
