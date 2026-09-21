using System.Text.Json.Nodes;
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
    public async Task ShutdownCancelsSharedWorkDrainsWaitersAndRejectsNewWork()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(gate.Task);
        var service = Create(source);
        var check = service.CheckManuallyAsync(new AppSettings { AutoDownloadUpdates = false });
        await source.Started.Task;
        var recovery = service.RecoverPendingAsync();
        var firstStop = service.DisposeAsync().AsTask();
        var secondStop = service.DisposeAsync().AsTask();
        Assert.AreSame(firstStop, secondStop);
        await firstStop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(check.IsCompleted);
        Assert.AreEqual(UpdateState.Idle, (await check).State);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await recovery);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await service.RecoverPendingAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await service.CheckManuallyAsync(new AppSettings()));
        Assert.AreEqual(1, source.CallCount);
    }

    [TestMethod]
    public async Task CallerCancellationDoesNotCancelSharedCheck()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(gate.Task);
        var service = Create(source);
        var settings = new AppSettings { AutoDownloadUpdates = false };
        using var caller = new CancellationTokenSource();
        var first = service.CheckManuallyAsync(settings, caller.Token);
        var second = service.CheckManuallyAsync(settings);
        caller.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
        Assert.IsFalse(second.IsCompleted);
        gate.SetResult();
        await second;
        Assert.AreEqual(1, source.CallCount);
    }

    [TestMethod]
    public async Task SoleCallerCancellationCancelsUnderlyingSharedCheck()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(gate.Task);
        var service = Create(source);
        var settings = new AppSettings { AutoDownloadUpdates = false };
        using var caller = new CancellationTokenSource();

        var check = service.CheckManuallyAsync(settings, caller.Token);
        await source.Started.Task;
        caller.Cancel();

        await source.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await check);
        Assert.AreEqual(1, source.CallCount);
    }

    [TestMethod]
    public async Task ThrowingStatusSubscriberDoesNotBreakUpdateOperation()
    {
        var source = new FakeSource();
        var service = Create(source);
        service.StatusChanged += (_, _) => throw new InvalidOperationException("subscriber failure");

        var result = await service.CheckManuallyAsync(new AppSettings { AutoDownloadUpdates = false });

        Assert.AreEqual(UpdateState.UpToDate, result.State);
        Assert.AreEqual(1, source.CallCount);
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
        await source.Started.Task;
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

    [TestMethod]
    public async Task InstallerIntegrityFailureIsReportedInsteadOfFaultingTheOperation()
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
            new ThrowingVerifier(),
            new UntrustedVerifier(),
            new NeverLauncher(),
            new FakeDeploymentMode(DeploymentMode.Installer),
            store,
            IdentityAppStringLocalizer.Instance,
            NullLogger<UpdateService>.Instance);

        await service.RecoverPendingAsync();
        var result = await service.InstallAsync(unattended: false);

        Assert.AreEqual(UpdateState.Failed, result.State);
    }

    [TestMethod]
    public async Task RecoveryScansEveryUpdateStateBeforeSelectingHighestVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-update-coordinator", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var paths = new AppStoragePaths(root);
        var store = new UpdateStateStore(paths);

        for (var index = 0; index < 21; index++)
        {
            var candidate = CreateInstallerUpdate(paths, "0.1.1", string.Concat("state-", index.ToString("D2")));
            Directory.CreateDirectory(Path.GetDirectoryName(candidate.FilePath)!);
            await store.SaveAsync(candidate, "ReadyToInstall");
        }

        var highest = CreateInstallerUpdate(paths, "0.2.0", "zz-highest");
        Directory.CreateDirectory(Path.GetDirectoryName(highest.FilePath)!);
        await store.SaveAsync(highest, "ReadyToInstall");

        var recovered = await store.LoadHighestAsync(ReleaseVersion.Parse("0.1.0"), DeploymentMode.Installer);

        Assert.IsNotNull(recovered);
        Assert.AreEqual(ReleaseVersion.Parse("0.2.0"), recovered.Value.Update.Candidate.Manifest.Version);
    }

    [TestMethod]
    [DataRow("nullHash")]
    [DataRow("wrongTag")]
    [DataRow("wrongChannel")]
    [DataRow("wrongPrerelease")]
    [DataRow("unsupportedWindows")]
    public async Task RecoverySkipsMalformedHigherStateAndFindsValidUpdate(string corruption)
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-update-coordinator", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var paths = new AppStoragePaths(root);
        var store = new UpdateStateStore(paths);
        var valid = CreateInstallerUpdate(paths);
        var invalid = CreateInstallerUpdate(paths, "0.2.0");
        foreach (var update in new[] { valid, invalid })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(update.FilePath)!);
            await store.SaveAsync(update, "ReadyToInstall");
        }
        var statePath = Path.Combine(Path.GetDirectoryName(invalid.FilePath)!, "update-state.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
        switch (corruption)
        {
            case "nullHash": json["installer"]!["sha256"] = null; break;
            case "wrongTag": json["tagName"] = "v0.3.0"; break;
            case "wrongChannel": json["channel"] = "Beta"; break;
            case "wrongPrerelease": json["isPrerelease"] = true; break;
            case "unsupportedWindows": json["minimumWindowsBuild"] = 1; break;
        }
        await File.WriteAllTextAsync(statePath, json.ToJsonString());

        var recovered = await store.LoadHighestAsync(ReleaseVersion.Parse("0.1.0"), DeploymentMode.Installer);

        Assert.IsNotNull(recovered);
        Assert.AreEqual(valid.Candidate.Manifest.Version, recovered.Value.Update.Candidate.Manifest.Version);
    }

    [TestMethod]
    public async Task CancellationBeforeInstallerLaunchRestoresDurableAndVisibleReadyState()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-update-coordinator", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var paths = new AppStoragePaths(root);
        var store = new UpdateStateStore(paths);
        var update = CreateInstallerUpdate(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(update.FilePath)!);
        await store.SaveAsync(update, "ReadyToInstall");
        var launcher = new CancellableLauncher();
        var service = new UpdateService(ReleaseVersion.Parse("0.1.0"), new FakeSource(),
            new UpdateManifestParser(), new NeverDownloader(), new AlwaysVerifier(), new UntrustedVerifier(),
            launcher, new FakeDeploymentMode(DeploymentMode.Installer), store,
            IdentityAppStringLocalizer.Instance, NullLogger<UpdateService>.Instance);
        await service.RecoverPendingAsync();
        using var caller = new CancellationTokenSource();
        var install = service.InstallAsync(false, caller.Token);
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await install);
        await service.DisposeAsync(); // drains rollback, not just the cancelled waiter

        Assert.AreEqual(UpdateState.ReadyToInstall, service.Status.State);
        Assert.AreEqual("ReadyToInstall", (await store.LoadHighestAsync(
            ReleaseVersion.Parse("0.1.0"), DeploymentMode.Installer))?.State);
    }

    private sealed class CancellableLauncher : IUpdateInstallerLauncher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> LaunchAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }
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
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _calls);

        public async Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            if (gate is not null)
            {
                try { await gate.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved.TrySetResult();
                    throw;
                }
            }
            return [];
        }

        public Task<ReadOnlyMemory<byte>> GetManifestAsync(UpdateRelease release, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("No manifest should be requested when the fake release list is empty.");
    }

    private static DownloadedUpdate CreateInstallerUpdate(
        AppStoragePaths paths,
        string versionText = "0.1.1",
        string? directoryName = null)
    {
        var version = ReleaseVersion.Parse(versionText);
        var installer = new UpdateManifestAsset("DropSpaceSetup.exe", 1, new string('a', 64));
        var portable = new UpdateManifestAsset("DropSpace.exe", 1, new string('b', 64));
        var asset = new UpdateReleaseAsset(
            installer.AssetName,
            installer.Size,
            new Uri($"https://github.com/airanluo-dot/DropSpace/releases/download/v{version}/DropSpaceSetup.exe"));
        var release = new UpdateRelease(
            $"v{version}",
            false,
            false,
            DateTimeOffset.Parse("2026-08-11T00:00:00Z"),
            new Uri($"https://github.com/airanluo-dot/DropSpace/releases/tag/v{version}"),
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
        var directory = Path.Combine(paths.Updates, directoryName ?? version.ToString());
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

    private sealed class ThrowingVerifier : IUpdateVerifier
    {
        private int _calls;

        public Task<bool> VerifyIntegrityAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1) return Task.FromResult(true);
            throw new IOException("Simulated verifier I/O failure.");
        }
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
