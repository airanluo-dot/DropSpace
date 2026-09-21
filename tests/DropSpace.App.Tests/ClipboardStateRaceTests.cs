using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ClipboardStateRaceTests
{
    [TestMethod]
    public async Task CancellationAfterResumeCommitKeepsRuntimeAndDiskConsistent()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-clipboard-races", Guid.NewGuid().ToString("N")));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var settings = new InstrumentedSettings(new JsonSettingsService(paths))
            {
                AfterUpdate = current => { if (!current.ClipboardPaused) cancellation.Cancel(); },
            };
            await using var service = CreateService(settings);
            await service.PauseAsync();
            Assert.IsTrue(service.IsPaused);
            await service.ResumeAsync(cancellation.Token);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.IsFalse(service.IsPaused);
            Assert.IsFalse((await settings.LoadAsync()).ClipboardPaused);
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true); }
    }

    [TestMethod]
    public async Task ShutdownWaitsForPendingInitializationBeforeDisposingItsGate()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-clipboard-races", Guid.NewGuid().ToString("N")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new InstrumentedSettings(new JsonSettingsService(paths))
        {
            BeforeLoad = async () => { entered.SetResult(); await release.Task; },
        };
        var service = CreateService(settings);
        try
        {
            var initialize = service.InitializeAsync();
            await entered.Task;
            var shutdown = service.DisposeAsync().AsTask();
            Assert.IsFalse(shutdown.IsCompleted, "Shutdown must retain resources used by pending initialization.");
            release.SetResult();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => initialize);
            await shutdown;
            await service.DisposeAsync();
        }
        finally
        {
            release.TrySetResult();
            await service.DisposeAsync();
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true);
        }
    }

    // These state-only paths never dispatch clipboard work or access item storage.
    private static ClipboardCaptureService CreateService(ISettingsService settings) => new(
        null!, settings, null!, null!, null!,
        new ClipboardNotificationService(NullLogger<ClipboardNotificationService>.Instance),
        null!, IdentityAppStringLocalizer.Instance, NullLogger<ClipboardCaptureService>.Instance);

    private sealed class InstrumentedSettings(ISettingsService inner) : ISettingsService
    {
        public Func<Task>? BeforeLoad { get; init; }
        public Action<AppSettings>? AfterUpdate { get; init; }
        public SettingsRecoveryReport LastLoadRecovery => inner.LastLoadRecovery;
        public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (BeforeLoad is not null) await BeforeLoad();
            return await inner.LoadAsync(cancellationToken);
        }
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => inner.SaveAsync(settings, cancellationToken);
        public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
        {
            var result = await inner.UpdateAsync(update, cancellationToken);
            AfterUpdate?.Invoke(result);
            return result;
        }
        public Task<AppSettings> ResetUiSettingsAsync(CancellationToken cancellationToken = default) => inner.ResetUiSettingsAsync(cancellationToken);
    }
}
