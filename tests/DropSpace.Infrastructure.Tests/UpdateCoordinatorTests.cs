using DropSpace.Core.Abstractions;
using DropSpace.Core.Compatibility;
using DropSpace.Core.Models;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Storage;
using DropSpace.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class UpdateCoordinatorTests
{
    private readonly List<string> _roots = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var root in _roots.Where(Directory.Exists)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task StartupCheck_RunsAtMostOnceForProcessLifetime()
    {
        var source = new FakeSource();
        var service = Create(source);
        var settings = new AppSettings { AutoDownloadUpdates = false };

        await service.CheckAtStartupAsync(settings);
        await service.CheckAtStartupAsync(settings);

        Assert.AreEqual(1, source.CallCount);
    }

    [TestMethod]
    public async Task ManualChecks_CanRepeatAfterCompletion()
    {
        var source = new FakeSource();
        var service = Create(source);
        var settings = new AppSettings { AutoDownloadUpdates = false };

        await service.CheckManuallyAsync(settings);
        await service.CheckManuallyAsync(settings);

        Assert.AreEqual(2, source.CallCount);
    }

    [TestMethod]
    public async Task ConcurrentStartupAndManualChecks_ShareOneFlight()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(gate.Task);
        var service = Create(source);
        var settings = new AppSettings { AutoDownloadUpdates = false };

        var automatic = service.CheckAtStartupAsync(settings);
        var manual = service.CheckManuallyAsync(settings);
        Assert.AreSame(automatic, manual);
        Assert.AreEqual(1, source.CallCount);
        gate.SetResult();
        await Task.WhenAll(automatic, manual);
    }

    [TestMethod]
    public async Task DisabledAutomaticCheck_DoesNotTouchNetworkButManualStillWorks()
    {
        var source = new FakeSource();
        var service = Create(source);
        var settings = new AppSettings { AutoCheckForUpdates = false, AutoDownloadUpdates = false };

        await service.CheckAtStartupAsync(settings);
        Assert.AreEqual(0, source.CallCount);
        await service.CheckManuallyAsync(settings);
        Assert.AreEqual(1, source.CallCount);
    }

    [TestMethod]
    public async Task InstallerShellLaunchFailure_RestoresReadyState()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-update-coordinator", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var paths = new AppStoragePaths(root);
        var store = new UpdateStateStore(paths);
        var update = CreateInstallerUpdate(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(update.FilePath)!);
        await File.WriteAllBytesAsync(update.FilePath, [1]);
        await store.SaveAsync(update, "ReadyToInstall");

        var service = new UpdateService(
            ReleaseVersion.Parse("0.1.0"),
            new FakeSource(),
            new UpdateManifestParser(),
            new NeverDownloader(),
            new AlwaysVerifier(),
            new UntrustedVerifier(),
            new Win32FailingLauncher(),
            new FakeDeploymentMode(DeploymentMode.Installer),
            store,
            IdentityAppStringLocalizer.Instance,
            NullLogger<UpdateService>.Instance);

        Assert.AreEqual(UpdateState.ReadyToInstall, (await service.RecoverPendingAsync()).State);
        var result = await service.InstallAsync(unattended: false);

        Assert.AreEqual(UpdateState.ReadyToInstall, result.State);
        Assert.AreEqual("ReadyToInstall", (await store.LoadHighestAsync(
            ReleaseVersion.Parse("0.1.0"), DeploymentMode.Installer))?.State);
    }

    [TestMethod]
    public async Task ManualInstallerInstall_AllowsUnsignedPreviewAfterIntegrityVerification()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-update-coordinator", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var paths = new AppStoragePaths(root);
        var store = new UpdateStateStore(paths);
        var update = CreateInstallerUpdate(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(update.FilePath)!);
        await File.WriteAllBytesAsync(update.FilePath, [1]);
        await store.SaveAsync(update, "ReadyToInstall");
        var launcher = new SuccessfulLauncher();
        var service = new UpdateService(
            ReleaseVersion.Parse("0.1.0"),
            new FakeSource(),
            new UpdateManifestParser(),
            new NeverDownloader(),
            new AlwaysVerifier(),
            new UntrustedVerifier(),
            launcher,
            new FakeDeploymentMode(DeploymentMode.Installer),
            store,
            IdentityAppStringLocalizer.Instance,
            NullLogger<UpdateService>.Instance);

        await service.RecoverPendingAsync();
        var result = await service.InstallAsync(unattended: false);

        Assert.IsTrue(launcher.Started);
        Assert.AreEqual(UpdateState.Installing, result.State);
    }

    private UpdateService Create(IUpdateSource source)
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-update-coordinator", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var paths = new AppStoragePaths(root);
        var store = new UpdateStateStore(paths);
        var mode = new FakeDeploymentMode();
        return new UpdateService(
            ReleaseVersion.Parse("0.1.0"),
            source,
            new UpdateManifestParser(),
            new NeverDownloader(),
            new AlwaysVerifier(),
            new UntrustedVerifier(),
            new NeverLauncher(),
            mode,
            store,
            IdentityAppStringLocalizer.Instance,
            NullLogger<UpdateService>.Instance);
    }

    private sealed class FakeSource(Task? gate = null) : IUpdateSource
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        public async Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (gate is not null) await gate.WaitAsync(cancellationToken);
            return [];
        }

        public Task<ReadOnlyMemory<byte>> GetManifestAsync(UpdateRelease release, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("No manifest should be requested when the fake release list is empty.");
    }

    private static DownloadedUpdate CreateInstallerUpdate(AppStoragePaths paths)
    {
        var version = ReleaseVersion.Parse("0.1.1");
        var installer = new UpdateManifestAsset("DropSpaceSetup.exe", 1, new string('a', 64));
        var portable = new UpdateManifestAsset("DropSpace.exe", 1, new string('b', 64));
        var asset = new UpdateReleaseAsset(
            installer.AssetName,
            installer.Size,
            new Uri("https://github.com/airanluo-dot/DropSpace/releases/download/v0.1.1/DropSpaceSetup.exe"));
        var release = new UpdateRelease(
            "v0.1.1",
            false,
            false,
            DateTimeOffset.Parse("2026-08-11T00:00:00Z"),
            new Uri("https://github.com/airanluo-dot/DropSpace/releases/tag/v0.1.1"),
            [asset]);
        var manifest = new UpdateManifest(
            1,
            UpdateChannel.Stable,
            version,
            version.ToVersionCode(),
            DateTimeOffset.Parse("2026-08-11T00:00:00Z"),
            WindowsCompatibilityPolicy.MinimumSupportedWindowsBuild,
            false,
            "Test update",
            installer,
            portable);
        var directory = Path.Combine(paths.Updates, version.ToString());
        return new DownloadedUpdate(
            new UpdateCandidate(release, manifest, asset, DeploymentMode.Installer),
            Path.Combine(directory, installer.AssetName),
            installer.Size,
            installer.Sha256,
            Path.Combine(directory, "update-install.log"));
    }

    private sealed class FakeDeploymentMode(DeploymentMode mode = DeploymentMode.Portable) : IDeploymentModeService
    {
        public DeploymentMode Current => mode;
    }

    private sealed class NeverDownloader : IUpdateDownloader
    {
        public Task<DownloadedUpdate> DownloadAsync(UpdateCandidate candidate, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Download was not expected.");
    }

    private sealed class AlwaysVerifier : IUpdateVerifier
    {
        public Task<bool> VerifyIntegrityAsync(DownloadedUpdate update, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class UntrustedVerifier : ITrustedUpdateVerifier
    {
        public Task<TrustedUpdateVerification> VerifyPublisherAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrustedUpdateVerification(false, "unsigned test"));
    }

    private sealed class NeverLauncher : IUpdateInstallerLauncher
    {
        public Task<bool> LaunchAsync(DownloadedUpdate update, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Installer launch was not expected.");
    }

    private sealed class Win32FailingLauncher : IUpdateInstallerLauncher
    {
        public Task<bool> LaunchAsync(DownloadedUpdate update, CancellationToken cancellationToken = default) =>
            throw new System.ComponentModel.Win32Exception(5, "Simulated ShellExecute denial.");
    }

    private sealed class SuccessfulLauncher : IUpdateInstallerLauncher
    {
        public bool Started { get; private set; }

        public Task<bool> LaunchAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
        {
            Started = true;
            return Task.FromResult(true);
        }
    }
}
