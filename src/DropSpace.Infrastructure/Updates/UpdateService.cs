using System.Text.Json;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Updates;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Updates;

public sealed class UpdateService : IUpdateService
{
    private readonly object _checkSync = new();
    private readonly IUpdateSource _source;
    private readonly UpdateManifestParser _manifestParser;
    private readonly IUpdateDownloader _downloader;
    private readonly IUpdateVerifier _verifier;
    private readonly ITrustedUpdateVerifier _trustedVerifier;
    private readonly IUpdateInstallerLauncher _installerLauncher;
    private readonly IDeploymentModeService _deploymentMode;
    private readonly UpdateStateStore _stateStore;
    private readonly ILogger<UpdateService> _logger;
    private Task<UpdateStatusSnapshot>? _activeCheck;
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
        _logger = logger;
        _status = UpdateStatusSnapshot.Initial(deploymentMode.Current);
    }

    public ReleaseVersion CurrentVersion { get; }

    public UpdateStatusSnapshot Status => Volatile.Read(ref _status);

    public event EventHandler<UpdateStatusSnapshot>? StatusChanged;

    public async Task<UpdateStatusSnapshot> RecoverPendingAsync(CancellationToken cancellationToken = default)
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
                Message = "上次下载的更新不完整，已保持当前版本。",
                PreviousInstallIncomplete = true,
            });
        }

        var incomplete = string.Equals(state, "Installing", StringComparison.OrdinalIgnoreCase);
        return Publish(new UpdateStatusSnapshot(
            UpdateState.ReadyToInstall,
            incomplete ? "上次更新未完成，可重新手动安装。" : "已下载更新，可立即安装。",
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

    public async Task<UpdateStatusSnapshot> DownloadAsync(CancellationToken cancellationToken = default)
    {
        var candidate = Status.Candidate ?? throw new InvalidOperationException("No validated update is available.");
        if (_deploymentMode.Current == DeploymentMode.Packaged)
        {
            return Publish(Status with { Message = "此安装由 Windows 管理更新。" });
        }

        Publish(Status with { State = UpdateState.Downloading, Message = "正在下载更新…", Progress = null });
        try
        {
            var progress = new InlineProgress<UpdateDownloadProgress>(value =>
                Publish(Status with { State = UpdateState.Downloading, Message = "正在下载更新…", Progress = value }));
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
                    ? "便携版更新已验证；请打开下载位置手动替换。"
                    : "更新已下载并通过完整性验证。",
                Download = download,
                Progress = new UpdateDownloadProgress(download.Size, download.Size),
                TrustedAutoInstallAvailable = trust.IsTrusted,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Publish(Status with { State = UpdateState.UpdateAvailable, Message = "更新下载已取消。", Progress = null });
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Update download or integrity verification failed.");
            return Publish(Status with
            {
                State = UpdateState.Failed,
                Message = exception is InvalidDataException
                    ? "下载的更新未通过完整性验证，已拒绝安装。"
                    : "更新下载失败；当前版本可继续正常使用。",
                Progress = null,
            });
        }
    }

    public async Task<UpdateStatusSnapshot> InstallAsync(
        bool unattended,
        CancellationToken cancellationToken = default)
    {
        var download = Status.Download ?? throw new InvalidOperationException("No verified update is ready to install.");
        if (_deploymentMode.Current != DeploymentMode.Installer)
        {
            return Publish(Status with
            {
                Message = _deploymentMode.Current == DeploymentMode.Packaged
                    ? "此安装由 Windows 管理更新。"
                    : "便携版不会转换为安装版；请手动替换便携文件。",
            });
        }

        if (!await _verifier.VerifyIntegrityAsync(download, cancellationToken).ConfigureAwait(false))
        {
            TryDelete(download.FilePath);
            return Publish(Status with { State = UpdateState.Failed, Message = "更新文件在安装前完整性验证失败，已拒绝执行。" });
        }

        var trust = await _trustedVerifier.VerifyPublisherAsync(download.FilePath, cancellationToken).ConfigureAwait(false);
        if (unattended && !trust.IsTrusted)
        {
            return Publish(Status with
            {
                Message = "当前构建尚未启用可信代码签名；无人值守自动安装不可用。",
                TrustedAutoInstallAvailable = false,
            });
        }

        Publish(Status with { State = UpdateState.Installing, Message = "正在启动安全升级…" });
        await _stateStore.SaveAsync(download, "Installing", cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await _installerLauncher.LaunchAsync(download, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The installer process could not be started.");
            }

            return Status;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "The verified update installer could not be launched.");
            await _stateStore.SaveAsync(download, "ReadyToInstall", CancellationToken.None).ConfigureAwait(false);
            return Publish(Status with { State = UpdateState.ReadyToInstall, Message = "安装器未能启动；DropSpace 将继续保持当前版本。" });
        }
    }

    public async Task MarkUpdatedLaunchAsync(ReleaseVersion updatedVersion, CancellationToken cancellationToken = default)
    {
        await _stateStore.MarkUpdatedLaunchAsync(updatedVersion, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("DropSpace completed an update launch at version {UpdatedVersion}.", updatedVersion);
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
                return _activeCheck;
            }

            _activeCheck = CheckCoreAsync(settings, automatic, cancellationToken);
            return _activeCheck;
        }
    }

    private async Task<UpdateStatusSnapshot> CheckCoreAsync(
        AppSettings settings,
        bool automatic,
        CancellationToken cancellationToken)
    {
        Publish(new UpdateStatusSnapshot(UpdateState.Checking, "正在检查更新…", _deploymentMode.Current));
        try
        {
            var releases = await _source.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            var release = UpdateReleaseSelector.SelectHighest(CurrentVersion, settings.UpdateChannel, releases);
            var checkedAt = DateTimeOffset.UtcNow;
            if (release is null)
            {
                var stable = UpdateReleaseSelector.HighestStable(releases);
                var message = settings.UpdateChannel == UpdateChannel.Stable && stable is { } highest && highest < CurrentVersion
                    ? "当前版本高于最新稳定版；DropSpace 不会自动降级。"
                    : "当前已是最新版本。";
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
                    ? $"发现 {manifest.Version}；此安装由 Windows 管理更新。"
                    : $"发现 DropSpace {manifest.Version}。",
                _deploymentMode.Current,
                checkedAt,
                candidate));

            if (_deploymentMode.Current != DeploymentMode.Packaged && settings.AutoDownloadUpdates)
            {
                var downloaded = await DownloadAsync(cancellationToken).ConfigureAwait(false);
                if (downloaded.State == UpdateState.ReadyToInstall && settings.AutoInstallUpdates && downloaded.TrustedAutoInstallAvailable)
                {
                    return await InstallAsync(unattended: true, cancellationToken).ConfigureAwait(false);
                }

                return downloaded;
            }

            return available;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Publish(Status with { State = UpdateState.Idle, Message = "更新检查已取消。" });
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "{UpdateCheckKind} update check failed.", automatic ? "Automatic" : "Manual");
            return Publish(new UpdateStatusSnapshot(
                UpdateState.Failed,
                automatic ? "上次自动检查失败。" : "暂时无法检查更新，请稍后重试。",
                _deploymentMode.Current,
                DateTimeOffset.UtcNow));
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
