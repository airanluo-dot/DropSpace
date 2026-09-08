using System.Text.Json;
using DropSpace.Core.Diagnostics;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Updates;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Updates;

public sealed class UpdateService : IUpdateService, IAsyncDisposable
{
    private readonly object _lifetimeSync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _operations = [];
    private Task? _shutdown;
    private bool _stopping;
    private readonly object _checkSync = new();
    private readonly object _downloadSync = new();
    private readonly object _installSync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IUpdateSource _source;
    private readonly UpdateManifestParser _manifestParser;
    private readonly IUpdateDownloader _downloader;
    private readonly IUpdateVerifier _verifier;
    private readonly ITrustedUpdateVerifier _trustedVerifier;
    private readonly IUpdateInstallerLauncher _installerLauncher;
    private readonly IDeploymentModeService _deploymentMode;
    private readonly UpdateStateStore _stateStore;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<UpdateService> _logger;
    private Task<UpdateStatusSnapshot>? _activeCheck;
    private Task<UpdateStatusSnapshot>? _activeDownload;
    private Task<UpdateStatusSnapshot>? _activeInstall;
    private int _startupCheckStarted;
    private UpdateStatusSnapshot _status;

    public UpdateService(
        ReleaseVersion currentVersion,
        IUpdateSource source,
        UpdateManifestParser manifestParser,
        IUpdateDownloader downloader,
        IUpdateVerifier verifier,
        ITrustedUpdateVerifier trustedVerifier,
        IUpdateInstallerLauncher installerLauncher,
        IDeploymentModeService deploymentMode,
        UpdateStateStore stateStore,
        IAppStringLocalizer strings,
        ILogger<UpdateService> logger)
    {
        CurrentVersion = currentVersion;
        _source = source;
        _manifestParser = manifestParser;
        _downloader = downloader;
        _verifier = verifier;
        _trustedVerifier = trustedVerifier;
        _installerLauncher = installerLauncher;
        _deploymentMode = deploymentMode;
        _stateStore = stateStore;
        _strings = strings;
        _logger = logger;
        _status = UpdateStatusSnapshot.Initial(deploymentMode.Current);
    }

    public ReleaseVersion CurrentVersion { get; }

    public UpdateStatusSnapshot Status => Volatile.Read(ref _status);

    public event EventHandler<UpdateStatusSnapshot>? StatusChanged;

    public Task<UpdateStatusSnapshot> RecoverPendingAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            RecoverPendingCoreAsync,
            cancellationToken);

    private async Task<UpdateStatusSnapshot> RecoverPendingCoreAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _stateStore.LoadHighestAsync(CurrentVersion, _deploymentMode.Current, cancellationToken)
            .ConfigureAwait(false);
        if (pending is null)
        {
            return Status;
        }

        var (download, state) = pending.Value;
        if (!await _verifier.VerifyIntegrityAsync(download, cancellationToken).ConfigureAwait(false))
        {
            return Publish(Status with
            {
                State = UpdateState.Failed,
                Message = _strings.Get("UpdateLastDownloadIncomplete"),
                PreviousInstallIncomplete = true,
            });
        }

        var incomplete = string.Equals(state, "Installing", StringComparison.OrdinalIgnoreCase);
        return Publish(new UpdateStatusSnapshot(
            UpdateState.ReadyToInstall,
            incomplete ? _strings.Get("UpdateLastInstallIncomplete") : _strings.Get("UpdateDownloadedReadyToInstall"),
            _deploymentMode.Current,
            Candidate: download.Candidate,
            Download: download,
            PreviousInstallIncomplete: incomplete));
    }


    public Task<UpdateStatusSnapshot> CheckAtStartupAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.AutoCheckForUpdates || Interlocked.Exchange(ref _startupCheckStarted, 1) != 0)
        {
            return Task.FromResult(Status);
        }

        return CheckSingleFlightAsync(settings, automatic: true, cancellationToken);
    }

    public Task<UpdateStatusSnapshot> CheckManuallyAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return CheckSingleFlightAsync(settings, automatic: false, cancellationToken);
    }

    public Task<UpdateStatusSnapshot> DownloadAsync(CancellationToken cancellationToken = default)
    {
        lock (_downloadSync)
        {
            if (_activeDownload is { IsCompleted: false })
            {
                return _activeDownload.WaitAsync(cancellationToken);
            }

            _activeDownload = RunExclusiveAsync(
                DownloadCoreAsync,
                CancellationToken.None);
            return _activeDownload.WaitAsync(cancellationToken);
        }
    }

    private async Task<UpdateStatusSnapshot> DownloadCoreAsync(CancellationToken cancellationToken = default)
    {
        var operationId = OperationCorrelation.New();
        var candidate = Status.Candidate ?? throw new InvalidOperationException("No validated update is available.");
        if (_deploymentMode.Current == DeploymentMode.Packaged)
        {
            return Publish(Status with { Message = _strings.Get("UpdateManagedByWindows") });
        }

        Publish(Status with { State = UpdateState.Downloading, Message = _strings.Get("UpdateDownloading"), Progress = null });
        try
        {
            var progress = new InlineProgress<UpdateDownloadProgress>(value =>
                Publish(Status with { State = UpdateState.Downloading, Message = _strings.Get("UpdateDownloading"), Progress = value }));
            var download = await _downloader.DownloadAsync(candidate, progress, cancellationToken).ConfigureAwait(false);
            if (!await _verifier.VerifyIntegrityAsync(download, cancellationToken).ConfigureAwait(false))
            {
                TryDelete(download.FilePath);
                throw new InvalidDataException("The completed update failed its second integrity verification.");
            }

            var trust = await _trustedVerifier.VerifyPublisherAsync(download.FilePath, cancellationToken)
                .ConfigureAwait(false);
            return Publish(Status with
            {
                State = UpdateState.ReadyToInstall,
                Message = _deploymentMode.Current == DeploymentMode.Portable
                    ? _strings.Get("UpdatePortableVerified")
                    : _strings.Get("UpdateDownloadedVerified"),
                Download = download,
                Progress = new UpdateDownloadProgress(download.Size, download.Size),
                TrustedAutoInstallAvailable = trust.IsTrusted,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Publish(Status with { State = UpdateState.UpdateAvailable, Message = _strings.Get("UpdateDownloadCancelled"), Progress = null });
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Update operation {OperationId} download or integrity verification failed.", operationId);
            return Publish(Status with
            {
                State = UpdateState.Failed,
                Message = exception is InvalidDataException
                    ? _strings.Get("UpdateDownloadIntegrityFailed")
                    : _strings.Get("UpdateDownloadFailed"),
                Progress = null,
            });
        }
    }


    public Task<UpdateStatusSnapshot> InstallAsync(
        bool unattended,
        CancellationToken cancellationToken = default)
    {
        lock (_installSync)
        {
            if (_activeInstall is { IsCompleted: false })
            {
                return _activeInstall.WaitAsync(cancellationToken);
            }

            _activeInstall = RunExclusiveAsync(
                token => InstallCoreAsync(unattended, token),
                CancellationToken.None);
            return _activeInstall.WaitAsync(cancellationToken);
        }
    }

    private async Task<UpdateStatusSnapshot> InstallCoreAsync(
        bool unattended,
        CancellationToken cancellationToken = default)
    {
        var operationId = OperationCorrelation.New();
        var download = Status.Download ?? throw new InvalidOperationException("No verified update is ready to install.");
        if (_deploymentMode.Current != DeploymentMode.Installer)
        {
            return Publish(Status with
            {
                Message = _deploymentMode.Current == DeploymentMode.Packaged
                    ? _strings.Get("UpdateManagedByWindows")
                    : _strings.Get("UpdatePortableManualReplacement"),
            });
        }

        if (!await _verifier.VerifyIntegrityAsync(download, cancellationToken).ConfigureAwait(false))
        {
            TryDelete(download.FilePath);
            return Publish(Status with { State = UpdateState.Failed, Message = _strings.Get("UpdateInstallIntegrityFailed") });
        }

        var trust = await _trustedVerifier.VerifyPublisherAsync(download.FilePath, cancellationToken).ConfigureAwait(false);
        // D-035: a manual install is an explicit user action and may install an unsigned Preview
        // after the manifest/size/hash checks above. Only unattended installation is gated on
        // publisher trust; Preview builds remain usable without pretending to be signed.
        if (unattended && !trust.IsTrusted)
        {
            return Publish(Status with
            {
                Message = _strings.Get("UpdateUntrustedAutoInstall"),
                TrustedAutoInstallAvailable = false,
            });
        }

        Publish(Status with { State = UpdateState.Installing, Message = _strings.Get("UpdateInstalling") });
        await _stateStore.SaveAsync(download, "Installing", cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await _installerLauncher.LaunchAsync(download, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The installer process could not be started.");
            }

            return Status;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(exception, "Update operation {OperationId} installer launch failed.", operationId);
            await _stateStore.SaveAsync(download, "ReadyToInstall", CancellationToken.None).ConfigureAwait(false);
            return Publish(Status with { State = UpdateState.ReadyToInstall, Message = _strings.Get("UpdateInstallerLaunchFailed") });
        }
    }


    public async Task MarkUpdatedLaunchAsync(ReleaseVersion updatedVersion, CancellationToken cancellationToken = default)
    {
        await RunExclusiveAsync(async token =>
        {
            await _stateStore.MarkUpdatedLaunchAsync(updatedVersion, token).ConfigureAwait(false);
            _logger.LogInformation("DropSpace completed an update launch at version {UpdatedVersion}.", updatedVersion);
            return Status;
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<UpdateStatusSnapshot> CheckSingleFlightAsync(
        AppSettings settings,
        bool automatic,
        CancellationToken cancellationToken)
    {
        lock (_checkSync)
        {
            if (_activeCheck is { IsCompleted: false })
            {
                return _activeCheck.WaitAsync(cancellationToken);
            }

            _activeCheck = CheckCoreAsync(settings, automatic, CancellationToken.None);
            return _activeCheck.WaitAsync(cancellationToken);
        }
    }

    private Task<UpdateStatusSnapshot> CheckCoreAsync(
        AppSettings settings,
        bool automatic,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            token => CheckCoreExclusiveAsync(settings, automatic, token),
            cancellationToken);

    private async Task<UpdateStatusSnapshot> CheckCoreExclusiveAsync(
        AppSettings settings,
        bool automatic,
        CancellationToken cancellationToken)
    {
        var operationId = OperationCorrelation.New();
        Publish(new UpdateStatusSnapshot(UpdateState.Checking, _strings.Get("UpdateChecking"), _deploymentMode.Current));
        try
        {
            var releases = await _source.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            var release = UpdateReleaseSelector.SelectHighest(CurrentVersion, settings.UpdateChannel, releases);
            var checkedAt = DateTimeOffset.UtcNow;
            if (release is null)
            {
                var stable = UpdateReleaseSelector.HighestStable(releases);
                var message = settings.UpdateChannel == UpdateChannel.Stable && stable is { } highest && highest < CurrentVersion
                    ? _strings.Get("UpdateNoDowngrade")
                    : _strings.Get("UpdateUpToDate");
                return Publish(new UpdateStatusSnapshot(UpdateState.UpToDate, message, _deploymentMode.Current, checkedAt));
            }

            var bytes = await _source.GetManifestAsync(release, cancellationToken).ConfigureAwait(false);
            var manifest = _manifestParser.ParseAndValidate(bytes, release);
            var descriptor = _deploymentMode.Current == DeploymentMode.Portable ? manifest.Portable : manifest.Installer;
            var asset = release.Assets.Single(item => string.Equals(item.Name, descriptor.AssetName, StringComparison.Ordinal));
            var candidate = new UpdateCandidate(release, manifest, asset, _deploymentMode.Current);
            var available = Publish(new UpdateStatusSnapshot(
                UpdateState.UpdateAvailable,
                _deploymentMode.Current == DeploymentMode.Packaged
                    ? _strings.Format("UpdateFoundManaged", manifest.Version)
                    : _strings.Format("UpdateFound", manifest.Version),
                _deploymentMode.Current,
                checkedAt,
                candidate));

            if (_deploymentMode.Current != DeploymentMode.Packaged && settings.AutoDownloadUpdates)
            {
                var downloaded = await DownloadCoreAsync(cancellationToken).ConfigureAwait(false);
                if (downloaded.State == UpdateState.ReadyToInstall && settings.AutoInstallUpdates && downloaded.TrustedAutoInstallAvailable)
                {
                    return await InstallCoreAsync(unattended: true, cancellationToken).ConfigureAwait(false);
                }

                return downloaded;
            }

            return available;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Publish(Status with { State = UpdateState.Idle, Message = _strings.Get("UpdateCheckCancelled") });
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Update operation {OperationId} {UpdateCheckKind} check failed.", operationId, automatic ? "Automatic" : "Manual");
            var message = exception switch
            {
                InvalidDataException or JsonException => _strings.Get("UpdateServiceValidationFailed"),
                TaskCanceledException => _strings.Get("UpdateServiceTimedOut"),
                _ => _strings.Get("UpdateServiceUnavailable"),
            };
            return Publish(new UpdateStatusSnapshot(
                UpdateState.Failed,
                automatic ? _strings.Format("UpdateAutomaticCheckFailed", message) : message,
                _deploymentMode.Current,
                DateTimeOffset.UtcNow));
        }
    }

    private Task<UpdateStatusSnapshot> RunExclusiveAsync(
        Func<CancellationToken, Task<UpdateStatusSnapshot>> operation,
        CancellationToken cancellationToken)
    {
        lock (_lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _operations.RemoveAll(task =>
            {
                if (!task.IsCompleted) return false;
                if (task.Exception is { } exception)
                    _logger.LogWarning(exception, "An owned update operation failed.");
                return true;
            });
            var task = RunExclusiveCoreAsync(operation, cancellationToken);
            _operations.Add(task);
            return task;
        }
    }

    private async Task<UpdateStatusSnapshot> RunExclusiveCoreAsync(
        Func<CancellationToken, Task<UpdateStatusSnapshot>> operation,
        CancellationToken callerToken)
    {
        // Register ownership before invoking dependencies or status subscribers.
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        var cancellationToken = linked.Token;
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeSync)
        {
            if (_shutdown is not null) return new ValueTask(_shutdown);
            _stopping = true;
            _shutdown = StopAsync(_operations.ToArray());
            return new ValueTask(_shutdown);
        }
    }

    private async Task StopAsync(Task[] operations)
    {
        // Do not invoke cancellation callbacks while holding the admission lock.
        await Task.Yield();
        try
        {
            try
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (AggregateException exception)
            {
                _logger.LogError(exception, "An update cancellation callback failed during shutdown.");
            }
            try
            {
                await Task.WhenAll(operations).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Waiting operations are cancelled as part of owned shutdown.
            }
            catch (Exception exception)
            {
                // All operations have ended; an earlier operation failure must not
                // prevent the service provider from releasing its other services.
                _logger.LogError(exception, "An update operation failed before shutdown completed.");
            }
        }
        finally
        {
            _operationGate.Dispose();
            _lifetime.Dispose();
            lock (_lifetimeSync) _operations.Clear();
        }
    }

    private UpdateStatusSnapshot Publish(UpdateStatusSnapshot snapshot)
    {
        Volatile.Write(ref _status, snapshot);
        StatusChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // The invalid payload is never executed; cleanup can be retried on the next process start.
        }
        catch (UnauthorizedAccessException)
        {
            // The invalid payload remains outside executable state and is never launched.
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
