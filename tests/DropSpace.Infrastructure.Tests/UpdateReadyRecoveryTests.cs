using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Compatibility;
using DropSpace.Core.Models;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Storage;
using DropSpace.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class UpdateReadyRecoveryTests
{
    [TestMethod]
    public async Task RecoveredPayloadSurvivesChecksWithoutReuseAcrossPayloadOrChannelChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-ready-update-" + Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(root);
        var store = new UpdateStateStore(paths);
        var source = new ControlledSource();
        var effects = new NeverSideEffects();
        var parser = new UpdateManifestParser();
        var verifier = new UpdateFileVerifier(paths);
        var service = new UpdateService(ReleaseVersion.Parse("0.1.0"), source, parser, effects,
            verifier, effects, effects, new InstallerMode(), store,
            IdentityAppStringLocalizer.Instance, NullLogger<UpdateService>.Instance);
        try
        {
            var original = CreatePacket("0.1.1", [1, 2, 3], parser);
            var seeded = await PersistAsync(original);
            var recovered = await service.RecoverPendingAsync();
            Assert.AreEqual(UpdateState.ReadyToInstall, recovered.State);
            Assert.IsNotNull(recovered.Download);
            Assert.AreEqual(seeded.FilePath, recovered.Download.FilePath);
            Assert.IsTrue(await verifier.VerifyIntegrityAsync(recovered.Download));
            Assert.IsFalse(recovered.TrustedAutoInstallAvailable);
            var automatic = new AppSettings
            {
                AutoCheckForUpdates = true,
                AutoDownloadUpdates = true,
                AutoInstallUpdates = true,
                UpdateChannel = UpdateChannel.Stable,
            };

            source.ReadReleases = _ => Task.FromException<IReadOnlyList<UpdateRelease>>(new IOException("controlled offline source"));
            var offline = await service.CheckAtStartupAsync(automatic);
            AssertReady(offline, recovered.Download);
            Assert.AreEqual("UpdateAutomaticCheckFailed", offline.MessageIdentity?.ResourceKey);
            Assert.IsNotNull(offline.LastCheckedAtUtc);
            var reason = offline.MessageIdentity!.Arguments.Single() as AppUiMessage;
            Assert.AreEqual("UpdateServiceUnavailable", reason?.ResourceKey);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.ReadReleases = async token =>
            {
                entered.TrySetResult();
                await blocked.Task.WaitAsync(token);
                return [];
            };
            using (var cancellation = new CancellationTokenSource())
            {
                var waiter = service.CheckManuallyAsync(automatic, cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Caller cancellation can finish before the actual shared check.
                // Await that existing owner rather than adding a new waiter or a delay.
                var owned = (Task<UpdateStatusSnapshot>)typeof(UpdateService)
                    .GetField("_activeCheck", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
                await cancellation.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
                var cancelled = await owned.WaitAsync(TimeSpan.FromSeconds(5));
                AssertReady(cancelled, recovered.Download);
                Assert.AreEqual("UpdateCheckCancelled", cancelled.MessageIdentity?.ResourceKey);
                Assert.AreEqual(offline.LastCheckedAtUtc, cancelled.LastCheckedAtUtc);
                Assert.AreEqual(0, source.ActiveCalls);
                Assert.AreSame(cancelled, service.Status);
            }

            source.Serve(original);
            var same = await service.CheckManuallyAsync(automatic);
            AssertReady(same, recovered.Download);
            Assert.AreEqual(1, source.ManifestCalls, "A fresh complete manifest must be parsed before matching the cached payload.");
            Assert.IsFalse(same.TrustedAutoInstallAvailable, "Metadata equality must not upgrade recovered publisher trust.");
            Assert.AreEqual(0, effects.DownloadCalls);
            Assert.AreEqual(0, effects.TrustCalls);
            Assert.AreEqual(0, effects.LaunchCalls);

            source.ReadReleases = _ => Task.FromResult<IReadOnlyList<UpdateRelease>>([]);
            AssertReady(await service.CheckManuallyAsync(automatic), recovered.Download);

            var manualDownload = automatic with { AutoDownloadUpdates = false, AutoInstallUpdates = false };
            var newer = CreatePacket("0.1.2", [4, 5, 6], parser);
            source.Serve(newer);
            var differentVersion = await service.CheckManuallyAsync(manualDownload);
            Assert.AreEqual(UpdateState.UpdateAvailable, differentVersion.State);
            Assert.AreEqual(newer.Candidate.Manifest.Version, differentVersion.Candidate?.Manifest.Version);
            Assert.IsNull(differentVersion.Download);

            var recoveredAgain = await service.RecoverPendingAsync();
            Assert.AreEqual(UpdateState.ReadyToInstall, recoveredAgain.State);
            var replaced = CreatePacket("0.1.1", [7, 8, 9], parser);
            source.Serve(replaced);
            var differentPayload = await service.CheckManuallyAsync(manualDownload);
            Assert.AreEqual(UpdateState.UpdateAvailable, differentPayload.State);
            Assert.AreEqual(replaced.Candidate.Manifest.Installer.Sha256, differentPayload.Candidate?.Manifest.Installer.Sha256);
            Assert.IsNull(differentPayload.Download, "Equal versions with a changed payload must not reuse the old executable.");
            CollectionAssert.AreEqual(original.Payload, await File.ReadAllBytesAsync(seeded.FilePath));

            var beforeMissing = await service.RecoverPendingAsync();
            Assert.AreEqual(UpdateState.ReadyToInstall, beforeMissing.State);
            File.Delete(seeded.FilePath);
            source.Serve(original);
            var missingPayload = await service.CheckManuallyAsync(manualDownload);
            Assert.AreEqual(UpdateState.UpdateAvailable, missingPayload.State);
            Assert.IsNull(missingPayload.Download,
                "Matching metadata must not keep a deleted executable ready without local integrity revalidation.");
            Assert.IsFalse(File.Exists(seeded.FilePath));

            var beta = CreatePacket("0.2.0-beta.1", [10, 11, 12], parser);
            await PersistAsync(beta);
            var recoveredBeta = await service.RecoverPendingAsync();
            Assert.AreEqual(UpdateState.ReadyToInstall, recoveredBeta.State);
            Assert.AreEqual(UpdateChannel.Beta, recoveredBeta.Candidate?.Manifest.Channel);
            source.ReadReleases = _ => Task.FromResult<IReadOnlyList<UpdateRelease>>([]);
            var stableOnly = await service.CheckManuallyAsync(manualDownload);
            Assert.AreEqual(UpdateState.UpToDate, stableOnly.State);
            Assert.IsNull(stableOnly.Download, "Switching to stable must not retain a recovered beta installer as ready.");
            Assert.IsNull(stableOnly.Candidate);
            Assert.AreEqual(0, source.ActiveCalls);
            Assert.AreEqual(0, effects.DownloadCalls);
            Assert.AreEqual(0, effects.TrustCalls);
            Assert.AreEqual(0, effects.LaunchCalls);
        }
        finally
        {
            await service.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        async Task<DownloadedUpdate> PersistAsync(Packet packet)
        {
            var directory = Path.Combine(paths.Updates, packet.Candidate.Manifest.Version.ToString());
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, packet.Candidate.SelectedAsset.Name);
            await File.WriteAllBytesAsync(file, packet.Payload);
            var download = new DownloadedUpdate(packet.Candidate, file, packet.Payload.LongLength,
                packet.Candidate.Manifest.Installer.Sha256, Path.Combine(directory, "update-install.log"));
            await store.SaveAsync(download, "ReadyToInstall");
            return download;
        }
    }

    private static void AssertReady(UpdateStatusSnapshot status, DownloadedUpdate original)
    {
        Assert.AreEqual(UpdateState.ReadyToInstall, status.State);
        Assert.AreSame(original, status.Download);
        Assert.AreSame(original.Candidate, status.Candidate);
    }

    private static Packet CreatePacket(string versionText, byte[] payload, UpdateManifestParser parser)
    {
        var version = ReleaseVersion.Parse(versionText);
        var tag = "v" + version;
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var publishedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            channel = version.IsPrerelease ? "beta" : "stable",
            version = version.ToString(),
            versionCode = version.ToVersionCode(),
            publishedAt,
            minimumWindowsBuild = WindowsCompatibilityPolicy.MinimumSupportedWindowsBuild,
            mandatory = false,
            summary = "Controlled recovery metadata",
            installer = new { assetName = "DropSpaceSetup.exe", size = payload.LongLength, sha256 = hash },
            portable = new { assetName = "DropSpace.exe", size = payload.LongLength, sha256 = hash },
        });
        var installer = new UpdateReleaseAsset("DropSpaceSetup.exe", payload.LongLength, Official("DropSpaceSetup.exe"));
        var release = new UpdateRelease(tag, false, version.IsPrerelease, publishedAt,
            new Uri($"https://github.com/airanluo-dot/DropSpace/releases/tag/{tag}"),
            [installer, new("DropSpace.exe", payload.LongLength, Official("DropSpace.exe")),
                new("update-manifest.json", bytes.LongLength, Official("update-manifest.json"))]);
        var manifest = parser.ParseAndValidate(bytes, release);
        return new Packet(release, bytes, new UpdateCandidate(release, manifest, installer, DeploymentMode.Installer), payload);

        Uri Official(string file) => new($"https://github.com/airanluo-dot/DropSpace/releases/download/{tag}/{file}");
    }

    private sealed record Packet(UpdateRelease Release, ReadOnlyMemory<byte> Manifest, UpdateCandidate Candidate, byte[] Payload);

    private sealed class ControlledSource : IUpdateSource
    {
        private int _active;
        internal Func<CancellationToken, Task<IReadOnlyList<UpdateRelease>>> ReadReleases { get; set; } =
            _ => Task.FromResult<IReadOnlyList<UpdateRelease>>([]);
        internal int ActiveCalls => Volatile.Read(ref _active);
        internal int ManifestCalls { get; private set; }
        private ReadOnlyMemory<byte> _manifest;

        internal void Serve(Packet packet)
        {
            _manifest = packet.Manifest;
            ReadReleases = _ => Task.FromResult<IReadOnlyList<UpdateRelease>>([packet.Release]);
        }

        public async Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            try { return await ReadReleases(cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }

        public Task<ReadOnlyMemory<byte>> GetManifestAsync(UpdateRelease release, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManifestCalls++;
            return Task.FromResult(_manifest);
        }
    }

    private sealed class InstallerMode : IDeploymentModeService
    {
        public DeploymentMode Current => DeploymentMode.Installer;
    }

    private sealed class NeverSideEffects : IUpdateDownloader, ITrustedUpdateVerifier, IUpdateInstallerLauncher
    {
        internal int DownloadCalls { get; private set; }
        internal int TrustCalls { get; private set; }
        internal int LaunchCalls { get; private set; }
        public Task<DownloadedUpdate> DownloadAsync(UpdateCandidate candidate, IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            throw new AssertFailedException("This metadata/recovery scenario must not download an executable.");
        }
        public Task<TrustedUpdateVerification> VerifyPublisherAsync(string filePath, CancellationToken cancellationToken = default)
        {
            TrustCalls++;
            throw new AssertFailedException("Metadata equality must not reverify or upgrade publisher trust.");
        }
        public Task<bool> LaunchAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
        {
            LaunchCalls++;
            throw new AssertFailedException("This scenario must not launch an installer.");
        }
    }
}
