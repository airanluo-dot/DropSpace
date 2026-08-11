using DropSpace.Core.Abstractions;
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

    private sealed class FakeDeploymentMode : IDeploymentModeService
    {
        public DeploymentMode Current => DeploymentMode.Portable;
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
}
