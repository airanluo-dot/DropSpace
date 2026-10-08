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
    private SharedUpdateOperation? _checkOperation;
    private SharedUpdateOperation? _downloadOperation;
    private SharedUpdateOperation? _installOperation;
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
        var operationId = OperationCorrelation.New();
        (DownloadedUpdate Update, string State)? pending;
        try
        {
            pending = await _stateStore.LoadHighestAsync(CurrentVersion, _deploymentMode.Current, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsHandledUpdateException(exception))
        {
            _logger.LogWarning(exception, "Update operation {OperationId} pending-state recovery failed.", operationId);
            return Publish(Status with
            {
                State = UpdateState.Failed,
                MessageIdentity = AppUiMessage.Resource("UpdateRecoveryFailed"),
                PreviousInstallIncomplete = true,
            });
        }

        if (pending is null)
        {
            return Status;
        }

        var (download, state) = pending.Value;
        try
        {
            if (!await _verifier.VerifyIntegrityAsync(download, cancellationToken).ConfigureAwait(false))
            {
                return Publish(Status with
                {
                    State = UpdateState.Failed,
                    MessageIdentity = AppUiMessage.Resource("UpdateLastDownloadIncomplete"),
                    PreviousInstallIncomplete = true,
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsHandledUpdateException(exception))
        {
            _logger.LogWarning(exception, "Pending update integrity verification could not complete.");
            return Publish(Status with
            {
                State = UpdateState.Failed,
                MessageIdentity = AppUiMessage.Resource("UpdateLastDownloadIncomplete"),
                PreviousInstallIncomplete = true,
            });
        }

        var incomplete = string.Equals(state, "Installing", StringComparison.OrdinalIgnoreCase);
        return Publish(new UpdateStatusSnapshot(
            UpdateState.ReadyToInstall,
            string.Empty,
            _deploymentMode.Current,
            Candidate: download.Candidate,
            Download: download,
            PreviousInstallIncomplete: incomplete,
            MessageIdentity: AppUiMessage.Resource(incomplete ? "UpdateLastInstallIncomplete" : "UpdateDownloadedReadyToInstall")));
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
                return WaitForSharedOperation(_downloadOperation!, cancellationToken);
            }

            _downloadOperation = new SharedUpdateOperation();
            _activeDownload = _downloadOperation.Start(
                token => RunExclusiveAsync(DownloadCoreAsync, token));
            return WaitForSharedOperation(_downloadOperation, cancellationToken);
        }
    }

    private async Task<UpdateStatusSnapshot> DownloadCoreAsync(CancellationToken cancellationToken = default)
    {
        var operationId = OperationCorrelation.New();
        DownloadedUpdate? download = null;
        try
        {
            var candidate = Status.Candidate ?? throw new InvalidOperationException("No validated update is available.");
            if (_deploymentMode.Current == DeploymentMode.Packaged)
            {
                return Publish(Status with { MessageIdentity = AppUiMessage.Resource("UpdateManagedByWindows") });
            }

            Publish(Status with { State = UpdateState.Downloading, MessageIdentity = AppUiMessage.Resource("UpdateDownloading"), Progress = null });
            var progress = new InlineProgress<UpdateDownloadProgress>(value =>
                Publish(Status with { State = UpdateState.Downloading,
                    MessageIdentity = AppUiMessage.Resource(value.Stage == DropSpace.Core.Downloads.DownloadStage.Queued ? "DownloadStageQueued" : "UpdateDownloading"), Progress = value }));
            download = await _downloader.DownloadAsync(candidate, progress, cancellationToken).ConfigureAwait(false);
            if (!await _verifier.VerifyIntegrityAsync(download, cancellationToken).ConfigureAwait(false))
            {
                TryDelete(download.FilePath);
                throw new InvalidDataException("The completed update failed its second integrity verification.");
            }

            TrustedUpdateVerification trust;
            try
            {
                trust = await _trustedVerifier.VerifyPublisherAsync(download.FilePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsHandledUpdateException(exception))
            {
                // Publisher verification is a fail-closed capability check. A manual install can
                // still be offered after the manifest hash succeeds, while unattended install
                // remains disabled because trust is unavailable rather than silently assumed.
                _logger.LogWarning(exception, "Update publisher verification was unavailable.");
                trust = new TrustedUpdateVerification(false, "Publisher verification was unavailable.");
            }
            return Publish(Status with
            {
                State = UpdateState.ReadyToInstall,
                MessageIdentity = _deploymentMode.Current == DeploymentMode.Portable
                    ? AppUiMessage.Resource("UpdatePortableVerified")
                    : AppUiMessage.Resource("UpdateDownloadedVerified"),
                Download = download,
                Progress = new UpdateDownloadProgress(download.Size, download.Size),
                TrustedAutoInstallAvailable = trust.IsTrusted,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (download is not null) TryDelete(download.FilePath);
            return Publish(Status with { State = UpdateState.UpdateAvailable, MessageIdentity = AppUiMessage.Resource("UpdateDownloadCancelled"), Download = null, Progress = null });
        }
        catch (Exception exception) when (IsHandledUpdateException(exception))
        {
            _logger.LogWarning(exception, "Update operation {OperationId} download or integrity verification failed.", operationId);
            if (download is not null) TryDelete(download.FilePath);
            return Publish(Status with
            {
                State = UpdateState.Failed,
                MessageIdentity = exception is DropSpace.Infrastructure.Downloads.DownloadQueueTimeoutException ? AppUiMessage.Resource("DownloadQueueTimedOut")
                    : exception is DropSpace.Infrastructure.Downloads.DownloadTransferTimeoutException ? AppUiMessage.Resource("DownloadTransferTimedOut")
                    : exception is InvalidDataException
                    ? AppUiMessage.Resource("UpdateDownloadIntegrityFailed")
                    : AppUiMessage.Resource("UpdateDownloadFailed"),
                Download = null,
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
                return WaitForSharedOperation(_installOperation!, cancellationToken);
            }

            _installOperation = new SharedUpdateOperation();
            _activeInstall = _installOperation.Start(
                token => RunExclusiveAsync(innerToken => InstallCoreAsync(unattended, innerToken), token));
            return WaitForSharedOperation(_installOperation, cancellationToken);
        }
    }

    private async Task<UpdateStatusSnapshot> InstallCoreAsync(
        bool unattended,
        CancellationToken cancellationToken = default)
    {
        var operationId = OperationCorrelation.New();
        DownloadedUpdate? download = null;
        try
        {
            download = Status.Download ?? throw new InvalidOperationException("No verified update is ready to install.");
            if (_deploymentMode.Current != DeploymentMode.Installer)
            {
                return Publish(Status with
                {
                    MessageIdentity = _deploymentMode.Current == DeploymentMode.Packaged
                        ? AppUiMessage.Resource("UpdateManagedByWindows")
                        : AppUiMessage.Resource("UpdatePortableManualReplacement"),
                });
            }

            if (!await _verifier.VerifyIntegrityAsync(download, cancellationToken).ConfigureAwait(false))
            {
                TryDelete(download.FilePath);
                return Publish(Status with { State = UpdateState.Failed, MessageIdentity = AppUiMessage.Resource("UpdateInstallIntegrityFailed"), Download = null });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsHandledUpdateException(exception))
        {
            _logger.LogWarning(exception, "Update operation {OperationId} install integrity verification failed.", operationId);
            if (download is not null)
            {
                TryDelete(download.FilePath);
            }
            return Publish(Status with { State = UpdateState.Failed, MessageIdentity = AppUiMessage.Resource("UpdateInstallIntegrityFailed"), Download = null });
        }

        TrustedUpdateVerification trust;
        try
        {
            trust = await _trustedVerifier.VerifyPublisherAsync(download.FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsHandledUpdateException(exception))
        {
            _logger.LogWarning(exception, "Update operation {OperationId} publisher verification was unavailable.", operationId);
            trust = new TrustedUpdateVerification(false, "Publisher verification was unavailable.");
        }
        // D-035: a manual install is an explicit user action and may install an unsigned Preview
        // after the manifest/size/hash checks above. Only unattended installation is gated on
        // publisher trust; Preview builds remain usable without pretending to be signed.
        if (unattended && !trust.IsTrusted)
        {
            return Publish(Status with
            {
                MessageIdentity = AppUiMessage.Resource("UpdateUntrustedAutoInstall"),
                TrustedAutoInstallAvailable = false,
            });
        }

        Publish(Status with { State = UpdateState.Installing, MessageIdentity = AppUiMessage.Resource("UpdateInstalling") });
        try
        {
            await _stateStore.SaveAsync(download, "Installing", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RestoreReadyStateAsync(download).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsHandledUpdateException(exception))
        {
            _logger.LogError(exception, "Update operation {OperationId} could not persist the installing state.", operationId);
            return Publish(Status with { State = UpdateState.Failed, MessageIdentity = AppUiMessage.Resource("UpdateInstallStateFailed") });
        }
        try
        {
            if (!await _installerLauncher.LaunchAsync(download, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The installer process could not be started.");
            }

            return Status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RestoreReadyStateAsync(download).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsHandledUpdateException(exception))
        {
            _logger.LogError(exception, "Update operation {OperationId} installer launch failed.", operationId);
            return await RestoreReadyStateAsync(download).ConfigureAwait(false);
        }
    }

    private async Task<UpdateStatusSnapshot> RestoreReadyStateAsync(DownloadedUpdate download)
    {
        try
        {
            // Cancellation before launch is not an incomplete installation. Restore both
            // durable and visible state without reusing the cancelled operation token.
            await _stateStore.SaveAsync(download, "ReadyToInstall", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsHandledUpdateException(exception))
        {
            _logger.LogError(exception, "Could not restore the ready state before installer launch.");
            return Publish(Status with { State = UpdateState.Failed, MessageIdentity = AppUiMessage.Resource("UpdateInstallStateFailed") });
        }
        return Publish(Status with { State = UpdateState.ReadyToInstall, MessageIdentity = AppUiMessage.Resource("UpdateInstallerLaunchFailed") });
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
                return WaitForSharedOperation(_checkOperation!, cancellationToken);
            }

            _checkOperation = new SharedUpdateOperation();
            _activeCheck = _checkOperation.Start(
                token => CheckCoreAsync(settings, automatic, token));
            return WaitForSharedOperation(_checkOperation, cancellationToken);
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
        Publish(new UpdateStatusSnapshot(UpdateState.Checking, string.Empty, _deploymentMode.Current,
            MessageIdentity: AppUiMessage.Resource("UpdateChecking")));
        try
        {
            var releases = await _source.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            var release = UpdateReleaseSelector.SelectHighest(CurrentVersion, settings.UpdateChannel, releases);
            var checkedAt = DateTimeOffset.UtcNow;
            if (release is null)
            {
                var stable = UpdateReleaseSelector.HighestStable(releases);
                return Publish(new UpdateStatusSnapshot(UpdateState.UpToDate, string.Empty, _deploymentMode.Current, checkedAt,
                    MessageIdentity: AppUiMessage.Resource(settings.UpdateChannel == UpdateChannel.Stable && stable is { } highest && highest < CurrentVersion
                        ? "UpdateNoDowngrade" : "UpdateUpToDate")));
            }

            var bytes = await _source.GetManifestAsync(release, cancellationToken).ConfigureAwait(false);
            var manifest = _manifestParser.ParseAndValidate(bytes, release);
            var descriptor = _deploymentMode.Current == DeploymentMode.Portable ? manifest.Portable : manifest.Installer;
            var asset = release.Assets.Single(item => string.Equals(item.Name, descriptor.AssetName, StringComparison.Ordinal));
            var candidate = new UpdateCandidate(release, manifest, asset, _deploymentMode.Current);
            var available = Publish(new UpdateStatusSnapshot(
                UpdateState.UpdateAvailable,
                string.Empty,
                _deploymentMode.Current,
                checkedAt,
                candidate,
                MessageIdentity: AppUiMessage.Resource(_deploymentMode.Current == DeploymentMode.Packaged
                    ? "UpdateFoundManaged" : "UpdateFound", manifest.Version)));

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
            return Publish(Status with { State = UpdateState.Idle, MessageIdentity = AppUiMessage.Resource("UpdateCheckCancelled") });
        }
        catch (Exception exception) when (IsHandledUpdateException(exception))
        {
            _logger.LogWarning(exception, "Update operation {OperationId} {UpdateCheckKind} check failed.", operationId, automatic ? "Automatic" : "Manual");
            var reason = AppUiMessage.Resource(exception switch
            {
                InvalidDataException or JsonException or InvalidOperationException or ArgumentException => "UpdateServiceValidationFailed",
                TaskCanceledException => "UpdateServiceTimedOut",
                _ => "UpdateServiceUnavailable",
            });
            return Publish(new UpdateStatusSnapshot(
                UpdateState.Failed,
                string.Empty,
                _deploymentMode.Current,
                DateTimeOffset.UtcNow,
                MessageIdentity: automatic ? AppUiMessage.Resource("UpdateAutomaticCheckFailed", reason) : reason));
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

    private static Task<UpdateStatusSnapshot> WaitForSharedOperation(
        SharedUpdateOperation operation,
        CancellationToken callerToken)
    {
        operation.AddWaiter();
        if (!callerToken.CanBeCanceled)
        {
            operation.ReleaseWhenCompleted();
            return operation.Task;
        }

        return WaitWithCallerCancellationAsync(operation, callerToken);
    }

    private static async Task<UpdateStatusSnapshot> WaitWithCallerCancellationAsync(
        SharedUpdateOperation operation,
        CancellationToken callerToken)
    {
        try
        {
            return await operation.Task.WaitAsync(callerToken).ConfigureAwait(false);
        }
        finally
        {
            operation.ReleaseWaiter();
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
            // CancelAsync marks the lifetime token before propagating cancellation
            // through linked callbacks. A queued operation can acquire the gate
            // during that interval, so observe the owner directly before starting it.
            _lifetime.Token.ThrowIfCancellationRequested();
            callerToken.ThrowIfCancellationRequested();
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
        // Keep the legacy snapshot text for existing consumers; the owner retains the
        // stable descriptor and UI renders it with the current language.
        if (snapshot.MessageIdentity is { } message) snapshot = snapshot with { Message = message.Render(_strings) };
        Volatile.Write(ref _status, snapshot);
        if (StatusChanged is { } handlers)
        {
            foreach (EventHandler<UpdateStatusSnapshot> handler in handlers.GetInvocationList())
            {
                try { handler(this, snapshot); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { _logger.LogWarning("Update status subscriber failed ({Category}).", exception.GetType().Name); }
            }
        }
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

    private static bool IsHandledUpdateException(Exception exception) => exception is
        HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or
        JsonException or TaskCanceledException or InvalidOperationException or ArgumentException or
        TimeoutException or NotSupportedException or
        System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException or
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
        PlatformNotSupportedException;

    /// <summary>
    /// Gives a shared update operation its own cancellation ownership. Individual callers may
    /// stop waiting without interrupting another caller, while an operation with no remaining
    /// waiters is still cancelled so a forgotten download/check does not run indefinitely.
    /// </summary>
    private sealed class SharedUpdateOperation
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _waiters;
        private int _completed;
        private int _disposed;

        public Task<UpdateStatusSnapshot> Task { get; private set; } = null!;
        private CancellationToken Token => _cancellation.Token;

        public Task<UpdateStatusSnapshot> Start(Func<CancellationToken, Task<UpdateStatusSnapshot>> operation)
        {
            Task = operation(Token);
            _ = ObserveCompletionAsync();
            return Task;
        }

        public void AddWaiter() => Interlocked.Increment(ref _waiters);

        public void ReleaseWhenCompleted()
        {
            _ = ObserveWaiterCompletionAsync();
        }

        public void ReleaseWaiter()
        {
            if (Interlocked.Decrement(ref _waiters) == 0 && Volatile.Read(ref _completed) == 0)
            {
                try { _cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            TryDispose();
        }

        private async Task ObserveWaiterCompletionAsync()
        {
            try { await Task.ConfigureAwait(false); }
            catch (Exception) { }
            finally { ReleaseWaiter(); }
        }

        private async Task ObserveCompletionAsync()
        {
            try { await Task.ConfigureAwait(false); }
            catch (Exception) { }
            finally
            {
                Volatile.Write(ref _completed, 1);
                TryDispose();
            }
        }

        private void TryDispose()
        {
            if (Volatile.Read(ref _completed) == 0 || Volatile.Read(ref _waiters) != 0 ||
                Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cancellation.Dispose();
        }
    }
}
