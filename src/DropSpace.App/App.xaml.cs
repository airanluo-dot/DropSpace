using System.Diagnostics;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Overlay;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Logging;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DropSpace.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private MainWindow? _window;
    private OverlayWindowService? _overlayWindows;
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
                _overlayWindows = _services.GetRequiredService<OverlayWindowService>();
                await _overlayWindows.InitializeAsync(_window.ShowAndActivate);
                if (Environment.GetCommandLineArgs().Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
                {
                    WriteSmokeMarker(_services.GetRequiredService<AppStoragePaths>());
                    if (Environment.GetCommandLineArgs().Contains("--smoke-hold", StringComparer.OrdinalIgnoreCase))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));
                    }

                    await ShutdownAsync();
                    Environment.Exit(0);
                }
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
        _overlayWindows?.Dispose();
        _overlayWindows = null;
        _window?.AllowCloseAndClose();
        if (services is not null)
        {
            await services.DisposeAsync();
        }
    }

    private ServiceProvider BuildServices()
    {
        var paths = AppStoragePaths.CreateForCurrentUser();
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
        services.AddSingleton<DragStorageItemService>();
        services.AddSingleton<IFileReferenceService, LocalFileReferenceService>();
        services.AddSingleton<ILocalStorageMetrics, LocalStorageMetrics>();
        services.AddSingleton<OverlayStateMachine>();
        services.AddSingleton<MonitorLayoutService>();
        services.AddSingleton<ForegroundWindowMonitor>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<OverlayWindowService>();
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
            var paths = AppStoragePaths.CreateForCurrentUser();
            Directory.CreateDirectory(paths.Logs);
            var marker = $"{DateTimeOffset.UtcNow:O} stage={stage} exception={exception.GetType().Name}";
            File.WriteAllText(Path.Combine(paths.Logs, "crash.marker"), marker);
        }
        catch (Exception markerException) when (markerException is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(markerException.GetType().Name);
        }
    }

    private static void WriteSmokeMarker(AppStoragePaths paths)
    {
        var markerPath = Path.Combine(Path.GetTempPath(), $"DropSpace-smoke-{Environment.ProcessId}.json");
        var marker = $$"""
            {"ready":true,"schemaVersion":{{SqliteDatabase.CurrentSchemaVersion}},"storageWritable":{{Directory.Exists(paths.Data).ToString().ToLowerInvariant()}}}
            """;
        File.WriteAllText(markerPath, marker);
    }
}
