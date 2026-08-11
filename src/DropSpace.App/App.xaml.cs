using System.Diagnostics;
using System.Text.Json;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Overlay;
using DropSpace.Core.Policies;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Logging;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using DropSpace.Infrastructure.Updates;
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
            var commandLine = Environment.GetCommandLineArgs();
            if (commandLine.Contains("--shutdown-for-maintenance", StringComparer.OrdinalIgnoreCase))
            {
                var result = await MaintenanceShutdownService.RequestRunningInstanceAsync(TimeSpan.FromSeconds(15));
                Environment.Exit(result);
                return;
            }

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
            var isShareActivation = activation.Kind == ExtendedActivationKind.ShareTarget;
            _services.GetRequiredService<CrashDiagnosticsService>().Start();
            var settingsService = _services.GetRequiredService<ISettingsService>();
            if (commandLine.Contains("--reset-ui-settings", StringComparer.OrdinalIgnoreCase) ||
                commandLine.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase))
            {
                await settingsService.ResetUiSettingsAsync();
            }

            var viewModel = _services.GetRequiredService<MainViewModel>();
            _window = new MainWindow(viewModel, _services.GetRequiredService<ILogger<MainWindow>>());
            _window.ExitRequested += OnExitRequested;
            _services.GetRequiredService<MaintenanceShutdownService>().Start(ShutdownAsync);
            _window.Activate();
            if (isShareActivation)
            {
                _window.Hide();
            }
            _services.GetRequiredService<ClipboardNotificationService>().Initialize(
                WinRT.Interop.WindowNative.GetWindowHandle(_window));

            try
            {
                await viewModel.InitializeAsync();
                if (settingsService.LastLoadRecovery is { Recovered: true } recovery)
                {
                    _services.GetRequiredService<ILogger<App>>().LogWarning(
                        "UI settings recovery completed after {ErrorCategory}; quarantine file {QuarantineFileName}; non-UI preferences preserved={PreservedNonUi}.",
                        recovery.ErrorCategory,
                        recovery.QuarantineFileName,
                        recovery.PreservedNonUiPreferences);
                }

                _window.ApplyTheme(viewModel.Settings.Theme);
                _window.InitializeTray(_services.GetRequiredService<ILogger<NativeTrayService>>());
                _overlayWindows = _services.GetRequiredService<OverlayWindowService>();
                await _overlayWindows.InitializeAsync(_window.ShowAndActivate);
                if (isShareActivation)
                {
                    await _services.GetRequiredService<ShareTargetActivationService>()
                        .HandleAsync(activation);
                }
                if (commandLine.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                {
                    _window.Hide();
                }

                var updatedArgument = Array.FindIndex(commandLine, value =>
                    string.Equals(value, "--updated", StringComparison.OrdinalIgnoreCase));
                if (updatedArgument >= 0 && updatedArgument + 1 < commandLine.Length &&
                    ReleaseVersion.TryParse(commandLine[updatedArgument + 1], out var updatedVersion))
                {
                    await _services.GetRequiredService<IUpdateService>().MarkUpdatedLaunchAsync(updatedVersion);
                    viewModel.ShowUpdatedVersion(updatedVersion);
                }

                if (Environment.GetCommandLineArgs().Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
                {
                    WriteSmokeProgressMarker("clipboard-integration");
                    var clipboardMetrics = await _services.GetRequiredService<ClipboardIntegrationSmoke>()
                        .RunAsync();
                    WriteSmokeProgressMarker("overlay-lifecycle");
                    var metrics = await _overlayWindows.RunLifecycleSmokeAsync(100);
                    WriteSmokeProgressMarker("visible-overlay-cf-hdrop");
                    var visibleDropMetrics = await _overlayWindows.RunVisibleOverlayDropSmokeAsync();
                    WriteSmokeProgressMarker("projection-deletion-stress");
                    var projectionMetrics = await _overlayWindows.RunProjectionDeletionStressAsync(200);
                    WriteSmokeMarker(
                        _services.GetRequiredService<AppStoragePaths>(),
                        metrics,
                        visibleDropMetrics,
                        projectionMetrics,
                        clipboardMetrics,
                        _services.GetRequiredService<IStartupRegistrationService>().IsEnabled);
                    if (Environment.GetCommandLineArgs().Contains("--smoke-hold", StringComparer.OrdinalIgnoreCase))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));
                    }

                    await ShutdownAsync();
                    Environment.Exit(0);
                }

                else if (!commandLine.Contains("--test-mode", StringComparer.OrdinalIgnoreCase) &&
                    !string.Equals(Environment.GetEnvironmentVariable("DROPSPACE_TEST_MODE"), "1", StringComparison.Ordinal))
                {
                    // Process-lifetime ownership: opening or hiding windows never calls this path.
                    _ = viewModel.CheckForUpdatesAtStartupAsync();
                }
            }
            catch (Exception exception)
            {
                WriteCrashMarker("startup", exception);
                if (Environment.GetCommandLineArgs().Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
                {
                    WriteSmokeFailureMarker("startup", exception);
                }

                await _window.ShowRecoveryAsync(exception.GetType().Name);
            }
        }
        catch (Exception exception)
        {
            WriteCrashMarker("launch", exception);
            if (Environment.GetCommandLineArgs().Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                WriteSmokeFailureMarker("launch", exception);
            }

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
        services.AddSingleton<ClipboardNotificationService>();
        services.AddSingleton<ClipboardCaptureService>();
        services.AddSingleton<ClipboardIntegrationSmoke>();
        services.AddSingleton<MaintenanceShutdownService>();
        services.AddSingleton<CrashDiagnosticsService>();
        services.AddSingleton<ShellActionService>();
        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<DragStorageItemService>();
        services.AddSingleton<IFileReferenceService, LocalFileReferenceService>();
        services.AddSingleton<ILocalStorageMetrics, LocalStorageMetrics>();
        services.AddSingleton<IStartupRegistrationService, StartupRegistrationService>();
        services.AddSingleton<WindowsShareIntegrationService>();
        services.AddSingleton<ShareTargetActivationService>();
        services.AddSingleton<ReleaseBuildInfo>();
        services.AddSingleton<IDeploymentModeService, DeploymentModeService>();
        services.AddSingleton<UpdateManifestParser>();
        services.AddSingleton<UpdateStateStore>();
        services.AddSingleton<IUpdateVerifier, UpdateFileVerifier>();
        services.AddSingleton<ITrustedUpdateVerifier, AuthenticodeTrustedUpdateVerifier>();
        services.AddSingleton<IUpdateInstallerLauncher, InnoUpdateInstallerLauncher>();
        services.AddSingleton<IUpdateSource>(provider => new GitHubReleaseUpdateSource(
            new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
            provider.GetRequiredService<ReleaseBuildInfo>().CurrentVersion));
        services.AddSingleton<IUpdateDownloader>(provider => new HttpUpdateDownloader(
            new HttpClient { Timeout = TimeSpan.FromMinutes(30) },
            paths,
            provider.GetRequiredService<UpdateStateStore>()));
        services.AddSingleton<IUpdateService>(provider => new UpdateService(
            provider.GetRequiredService<ReleaseBuildInfo>().CurrentVersion,
            provider.GetRequiredService<IUpdateSource>(),
            provider.GetRequiredService<UpdateManifestParser>(),
            provider.GetRequiredService<IUpdateDownloader>(),
            provider.GetRequiredService<IUpdateVerifier>(),
            provider.GetRequiredService<ITrustedUpdateVerifier>(),
            provider.GetRequiredService<IUpdateInstallerLauncher>(),
            provider.GetRequiredService<IDeploymentModeService>(),
            provider.GetRequiredService<UpdateStateStore>(),
            provider.GetRequiredService<ILogger<UpdateService>>()));
        services.AddSingleton<OverlayStateMachine>();
        services.AddSingleton<MonitorLayoutService>();
        services.AddSingleton<ForegroundWindowMonitor>();
        services.AddSingleton<OleDragDropService>();
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
        dispatcher?.TryEnqueue(async () =>
        {
            try
            {
                var shareTarget = _services?.GetService<ShareTargetActivationService>();
                if (shareTarget?.CanHandle(args) == true)
                {
                    await shareTarget.HandleAsync(args);
                    return;
                }

                _window?.ShowAndActivate();
            }
            catch (Exception exception)
            {
                WriteCrashMarker("redirected-activation", exception);
                _services?.GetService<ILogger<App>>()?.LogError(exception, "Redirected activation failed.");
            }
        });
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

    private static void WriteSmokeMarker(
        AppStoragePaths paths,
        OverlayLifecycleMetrics metrics,
        VisibleOverlayDropSmokeMetrics visibleDrop,
        ProjectionDeletionStressMetrics projection,
        ClipboardIntegrationMetrics clipboard,
        bool startupRegistrationEnabled)
    {
        var markerPath = Path.Combine(Path.GetTempPath(), $"DropSpace-smoke-{Environment.ProcessId}.json");
        var marker = JsonSerializer.Serialize(new
        {
            ready = true,
            failed = false,
            stage = "complete",
            schemaVersion = SqliteDatabase.CurrentSchemaVersion,
            storageWritable = Directory.Exists(paths.Data),
            overlayCycles = metrics.Cycles,
            overlayWindowCount = metrics.WindowCount,
            dragActivationHostCount = metrics.ActivationHostCount,
            overlayHandleDelta = metrics.HandleDelta,
            overlayGdiObjectDelta = metrics.GdiObjectDelta,
            overlayUserObjectDelta = metrics.UserObjectDelta,
            overlayPrivateBytesDelta = metrics.PrivateBytesDelta,
            noContinuousFrameLoop = metrics.NoContinuousFrameSubscription,
            notchGeometryStressCycles = metrics.GeometryStressCycles,
            overlayRegionFailureCount = metrics.RegionFailureCount,
            dragActivationTargetsDiscoverable = metrics.ActivationTargetsDiscoverable,
            compactVisualTargetDiscoverable = metrics.CompactVisualTargetDiscoverable,
            expandedVisualTargetDiscoverable = metrics.ExpandedVisualTargetDiscoverable,
            compactSyntheticCfHDropAccepted = visibleDrop.CompactDropAccepted,
            expandedSyntheticCfHDropAccepted = visibleDrop.ExpandedDropAccepted,
            expandedDropStayedOpen = visibleDrop.ExpandedStayedOpen,
            visibleDropAddedItemCount = visibleDrop.AddedItemCount,
            projectionDeletionStressCycles = projection.Cycles,
            projectionFinalSpaceItemCount = projection.FinalSpaceItemCount,
            projectionFinalRecentItemCount = projection.FinalRecentItemCount,
            projectionUnhandledExceptionDelta = projection.UnhandledExceptionDelta,
            projectionUnobservedTaskExceptionDelta = projection.UnobservedTaskExceptionDelta,
            projectionExternalSentinelPreserved = projection.ExternalSentinelPreserved,
            clipboardListenerRegistered = clipboard.ListenerRegistered,
            clipboardObservedUpdateDelta = clipboard.ObservedUpdateDelta,
            clipboardSuccessfulCaptureDelta = clipboard.SuccessfulCaptureDelta,
            clipboardSuppressedConsecutiveDuplicateDelta = clipboard.SuppressedConsecutiveDuplicateDelta,
            clipboardFailedReadDelta = clipboard.FailedReadDelta,
            clipboardFirstTextPersisted = clipboard.FirstTextPersisted,
            clipboardSecondTextPersisted = clipboard.SecondTextPersisted,
            clipboardConsecutiveDuplicateSuppressionVerified = clipboard.ConsecutiveDuplicateSuppressionVerified,
            clipboardNonConsecutiveDuplicatePreserved = clipboard.NonConsecutiveDuplicatePreserved,
            clipboardFileReferencePersisted = clipboard.FileReferencePersisted,
            clipboardPauseVerified = clipboard.PauseVerified,
            clipboardResumeVerified = clipboard.ResumeVerified,
            clipboardSelfWriteSuppressionVerified = clipboard.SelfWriteSuppressionVerified,
            startupRegistrationEnabled,
        });
        File.WriteAllText(markerPath, marker);
    }

    private static void WriteSmokeProgressMarker(string stage)
    {
        WriteSmokeDiagnosticMarker(new
        {
            ready = false,
            failed = false,
            stage,
        });
    }

    private static void WriteSmokeFailureMarker(string stage, Exception exception)
    {
        WriteSmokeDiagnosticMarker(new
        {
            ready = false,
            failed = true,
            stage,
            exceptionType = exception.GetType().Name,
            errorCode = exception.HResult,
            error = LogRedactor.Redact(exception.Message),
        });
    }

    private static void WriteSmokeDiagnosticMarker<T>(T marker)
    {
        var markerPath = Path.Combine(Path.GetTempPath(), $"DropSpace-smoke-{Environment.ProcessId}.json");
        var temporaryPath = markerPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(marker));
        File.Move(temporaryPath, markerPath, true);
    }
}
