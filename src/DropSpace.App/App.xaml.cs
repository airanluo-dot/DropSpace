using System.Diagnostics;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Logging;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage;

namespace DropSpace.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private MainWindow? _window;
    private AppInstance? _mainInstance;
    private int _shuttingDown;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static App CurrentApp => (App)Current;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            _mainInstance = AppInstance.FindOrRegisterForKey("DropSpace.Main");
            if (!_mainInstance.IsCurrent)
            {
                await _mainInstance.RedirectActivationToAsync(activation);
                Environment.Exit(0);
                return;
            }

            _mainInstance.Activated += OnInstanceActivated;
            _services = BuildServices();
            var viewModel = _services.GetRequiredService<MainViewModel>();
            _window = new MainWindow(viewModel, _services.GetRequiredService<ILogger<MainWindow>>());
            _window.ExitRequested += OnExitRequested;
            _window.Activate();

            try
            {
                await viewModel.InitializeAsync();
                _window.ApplyTheme(viewModel.Settings.Theme);
                _window.InitializeTray(_services.GetRequiredService<ILogger<NativeTrayService>>());
            }
            catch (Exception exception)
            {
                WriteCrashMarker("startup", exception);
                await _window.ShowRecoveryAsync(exception.GetType().Name);
            }
        }
        catch (Exception exception)
        {
            WriteCrashMarker("launch", exception);
            Debug.WriteLine(exception);
        }
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
        {
            return;
        }

        var services = _services;
        _services = null;
        _window?.AllowCloseAndClose();
        if (services is not null)
        {
            await services.DisposeAsync();
        }
    }

    private ServiceProvider BuildServices()
    {
        var root = Path.Combine(ApplicationData.Current.LocalFolder.Path, "DropSpace");
        var paths = new AppStoragePaths(root);
        var fileLogger = new RedactingFileLoggerProvider(paths);
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(fileLogger);
        });
        services.AddSingleton(DispatcherQueue.GetForCurrentThread());
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IItemRepository, SqliteItemRepository>();
        services.AddSingleton<IPayloadStore, FilePayloadStore>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<ClipboardCaptureService>();
        services.AddSingleton<ShellActionService>();
        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        var dispatcher = _window?.DispatcherQueue;
        dispatcher?.TryEnqueue(() => _window?.ShowAndActivate());
    }

    private async void OnExitRequested(object? sender, EventArgs args)
    {
        try
        {
            await ShutdownAsync();
        }
        catch (Exception exception)
        {
            WriteCrashMarker("shutdown", exception);
            _window?.AllowCloseAndClose();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        WriteCrashMarker("unhandled", args.Exception);
        _services?.GetService<ILogger<App>>()?.LogCritical(args.Exception, "Unhandled UI exception.");
    }

    private static void WriteCrashMarker(string stage, Exception exception)
    {
        try
        {
            var root = Path.Combine(ApplicationData.Current.LocalFolder.Path, "DropSpace", "logs");
            Directory.CreateDirectory(root);
            var marker = $"{DateTimeOffset.UtcNow:O} stage={stage} exception={exception.GetType().Name}";
            File.WriteAllText(Path.Combine(root, "crash.marker"), marker);
        }
        catch (Exception markerException) when (markerException is IOException or UnauthorizedAccessException)
        {
        }
    }
}
