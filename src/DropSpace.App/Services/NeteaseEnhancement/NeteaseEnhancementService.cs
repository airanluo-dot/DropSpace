using DropSpace.Core.Media;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.NeteaseEnhancement;

/// <summary>Installation orchestration is separate from the player-agnostic media service.</summary>
public sealed class NeteaseEnhancementService(
    NeteaseInstallationProbe installations, InfLinkDeploymentService deployment,
    NeteaseRuntimeInstaller runtime, NeteaseSmtcVerifier verifier,
    ILogger<NeteaseEnhancementService> logger) : INeteaseEnhancementService
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _operation = Task.CompletedTask;
    private bool _inspected, _disposed;
    private NeteaseEnhancementState _current = new(NeteaseEnhancementStage.NotInstalled);
    public NeteaseEnhancementState Current => Volatile.Read(ref _current);
    public event EventHandler<NeteaseEnhancementState>? Changed;

    public Task InspectAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_inspected || _disposed || !_operation.IsCompleted) return _operation;
            _inspected = true;
            return _operation = Task.Run(() => InspectCoreAsync(cancellationToken), CancellationToken.None);
        }
    }
    public Task EnhanceAsync(bool reinstall = false, CancellationToken cancellationToken = default) => StartAsync(false, reinstall, cancellationToken);
    public Task RemoveAsync(CancellationToken cancellationToken = default) => StartAsync(true, false, cancellationToken);
    private Task StartAsync(bool remove, bool reinstall, CancellationToken token)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_operation.IsCompleted) return _operation;
            return _operation = Task.Run(() => RunAsync(remove, reinstall, token), CancellationToken.None);
        }
    }

    private async Task InspectCoreAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        InfLinkDeploymentReceipt? receipt = null;
        try
        {
            Publish(new(NeteaseEnhancementStage.Detecting));
            var installation = await installations.FindAsync(lifetime.Token).ConfigureAwait(false);
            if (installation is null) { Publish(new(NeteaseEnhancementStage.NotInstalled)); return; }
            receipt = await deployment.GetManagedReceiptAsync(installation, lifetime.Token).ConfigureAwait(false);
            // Merely opening Music never downloads, modifies components or starts playback.
            var capabilities = await verifier.VerifyAsync(TimeSpan.FromSeconds(4), false, lifetime.Token).ConfigureAwait(false);
            Publish(new((capabilities.Complete || CanRetainVerifiedState(receipt is { Committed: true }, capabilities)) ? NeteaseEnhancementStage.Enhanced : NeteaseEnhancementStage.NotInstalled,
                receipt is not null, receipt?.PluginVersion, receipt is { Committed: false } ? "Rollback" : null));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("NetEase enhancement operation failed ({Category}, {Code}).", exception.GetType().Name,
                exception is EnhancementDeploymentException failure ? failure.Code : "Operation");
            lock (_sync) _inspected = false;
            PublishFailure(exception, receipt is not null, receipt?.PluginVersion);
        }
    }

    internal static bool CanRetainVerifiedState(bool committed, NeteaseMediaCapabilities current) =>
        // A committed, hash-checked receipt records a completed active verification.
        // Passive inspection must not start music merely to re-prove a paused clock.
        // Still require a real current session with metadata, timeline and controls.
        committed && (current.Play || current.Pause) &&
        (current with { Play = true, Pause = true, LiveProgress = true }).Complete;

    private async Task RunAsync(bool remove, bool reinstall, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(10));
        token = lifetime.Token;
        NeteaseInstallation? installation = null;
        InfLinkDeploymentReceipt? previous = null, transaction = null;
        PreparedInfLinkDeployment? prepared = null;
        bool stopped = false;
        try
        {
            verifier.Diagnostic = category => logger.LogInformation("NetEase SMTC verification {Category}.", category);
            Publish(new(NeteaseEnhancementStage.Detecting, Current.IsManaged, Current.Version));
            installation = await installations.FindAsync(token).ConfigureAwait(false) ?? throw new EnhancementDeploymentException("NotFound");
            previous = await deployment.GetManagedReceiptAsync(installation, token).ConfigureAwait(false);
            if (previous is { Committed: false })
            {
                Publish(new(NeteaseEnhancementStage.Restarting, true, previous.PluginVersion));
                await verifier.InvalidateBeforeRestartAsync(token).ConfigureAwait(false);
                stopped = true; await deployment.StopAsync(installation, token).ConfigureAwait(false);
                await deployment.RollbackAsync(previous, token).ConfigureAwait(false);
                previous = await deployment.GetManagedReceiptAsync(installation, token).ConfigureAwait(false);
                await deployment.RestartAsync(installation, token).ConfigureAwait(false); stopped = false;
                if (remove && previous is null)
                {
                    Publish(new(NeteaseEnhancementStage.Removed)); return;
                }
            }
            if (remove)
            {
                if (previous is null) throw new EnhancementDeploymentException("NotManaged");
                Publish(new(NeteaseEnhancementStage.Removing, true, previous.PluginVersion));
                await verifier.InvalidateBeforeRestartAsync(token).ConfigureAwait(false);
                stopped = true; await deployment.StopAsync(installation, token).ConfigureAwait(false);
                await deployment.RemoveAsync(installation, token).ConfigureAwait(false);
                previous = null;
                await deployment.RestartAsync(installation, token).ConfigureAwait(false); stopped = false;
                Publish(new(NeteaseEnhancementStage.Removed)); return;
            }
            var native = await verifier.VerifyAsync(TimeSpan.FromSeconds(60), true, token).ConfigureAwait(false);
            if (native.Complete && previous is null && !reinstall)
            {
                Publish(new(NeteaseEnhancementStage.Enhanced)); return;
            }
            Publish(new(NeteaseEnhancementStage.Preparing, previous is not null, previous?.PluginVersion));
            await runtime.EnsureAsync(installation.Architecture, token).ConfigureAwait(false);
            prepared = await deployment.PrepareAsync(installation, token).ConfigureAwait(false);
            if (!reinstall && native.Complete && previous?.PluginVersion == prepared.PluginVersion)
            {
                Publish(new(NeteaseEnhancementStage.Enhanced, true, prepared.PluginVersion)); return;
            }
            Publish(new(NeteaseEnhancementStage.Installing, previous is not null, previous?.PluginVersion));
            await verifier.InvalidateBeforeRestartAsync(token).ConfigureAwait(false);
            stopped = true; await deployment.StopAsync(installation, token).ConfigureAwait(false);
            transaction = await deployment.InstallAsync(prepared, token).ConfigureAwait(false);
            Publish(new(NeteaseEnhancementStage.Restarting, true, prepared.PluginVersion));
            await deployment.RestartAsync(installation, token).ConfigureAwait(false); stopped = false;
            Publish(new(NeteaseEnhancementStage.Verifying, true, prepared.PluginVersion));
            var evidence = await verifier.VerifyAsync(TimeSpan.FromSeconds(60), true, token).ConfigureAwait(false);
            logger.LogInformation("NetEase SMTC verification evidence {Capabilities}.", evidence);
            if (!evidence.Complete) throw new EnhancementDeploymentException("Verification");
            await deployment.CommitAsync(transaction, token).ConfigureAwait(false);
            transaction = null;
            Publish(new(NeteaseEnhancementStage.Enhanced, true, prepared.PluginVersion));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("NetEase enhancement operation failed ({Category}, {Code}).", exception.GetType().Name,
                exception is EnhancementDeploymentException operationFailure ? operationFailure.Code : "Operation");
            logger.LogWarning("NetEase enhancement failure location {Operation}, HRESULT {HResult}.",
                exception.TargetSite?.Name, exception.HResult);
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                if (installation is not null && transaction is null)
                {
                    var persisted = await deployment.GetManagedReceiptAsync(installation, recovery.Token).ConfigureAwait(false);
                    if (persisted is { Committed: false }) transaction = persisted;
                }
                if (installation is not null && transaction is not null)
                {
                    await verifier.InvalidateBeforeRestartAsync(recovery.Token).ConfigureAwait(false);
                    await deployment.StopAsync(installation, recovery.Token).ConfigureAwait(false); stopped = true;
                    await deployment.RollbackAsync(transaction, recovery.Token).ConfigureAwait(false);
                }
                if (installation is not null && stopped)
                    await deployment.RestartAsync(installation, recovery.Token).ConfigureAwait(false);
                PublishFailure(exception, previous is not null, previous?.PluginVersion);
            }
            catch (Exception rollback) when (rollback is not OutOfMemoryException)
            {
                logger.LogWarning("NetEase enhancement recovery failed ({Category}, {Code}).", rollback.GetType().Name,
                    rollback is EnhancementDeploymentException failure ? failure.Code : "Recovery");
                logger.LogWarning("NetEase enhancement recovery location {Operation}, HRESULT {HResult}.",
                    rollback.TargetSite?.Name, rollback.HResult);
                Publish(new(NeteaseEnhancementStage.Failed, transaction is not null || previous is not null,
                    previous?.PluginVersion, "Rollback"));
                if (installation is not null && stopped)
                {
                    try { await deployment.RestartAsync(installation, recovery.Token).ConfigureAwait(false); }
                    catch (Exception restart) when (restart is not OutOfMemoryException)
                    { logger.LogWarning("NetEase recovery restart failed ({Category}).", restart.GetType().Name); }
                }
            }
        }
        finally
        {
            if (prepared is not null)
            {
                try { await deployment.DiscardPreparedAsync(prepared, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EnhancementDeploymentException)
                { logger.LogWarning("NetEase enhancement staging cleanup failed ({Category}).", exception.GetType().Name); }
            }
        }
    }

    private void PublishFailure(Exception exception, bool managed, string? version)
    {
        var code = exception switch
        {
            EnhancementDeploymentException ex => NormalizeError(ex.Code),
            OperationCanceledException => "Canceled",
            UnauthorizedAccessException => "Permission",
            HttpRequestException => "Network",
            _ => "Unexpected",
        };
        logger.LogWarning("NetEase enhancement failed ({Category}, {Code}).", exception.GetType().Name, code);
        Publish(new(NeteaseEnhancementStage.Failed, managed, version, code));
    }
    internal static string NormalizeError(string code) => code switch
    {
        "NotFound" => "NotFound", "Verification" => "Verification",
        "UnsupportedPrerequisite" or "RuntimeInstallFailed" => "Runtime",
        "DownloadFailed" => "Network",
        "HashMismatch" or "MissingDigest" or "InvalidDigest" or "InvalidAssetSize" or "AssetTooLarge" or "UntrustedDownload" or "InvalidRelease" or "RuntimeIntegrity" or "InvalidPlugin" or "InvalidRedirect" or "TooManyRedirects" => "Integrity",
        "ExistingUnmanagedFile" or "ManagedFileChanged" or "NotManaged" or "InvalidReceipt" or "PendingTransaction" or "UnsafePath" or "ProfileChanged" or "ConflictingPlugin" or "ReparsePoint" => "Conflict",
        _ => "Unexpected",
    };
    private void Publish(NeteaseEnhancementState state)
    {
        logger.LogInformation("NetEase enhancement stage {Stage}, managed={Managed}.", state.Stage, state.IsManaged);
        Volatile.Write(ref _current, state); Changed?.Invoke(this, state);
    }
    public async ValueTask DisposeAsync()
    {
        Task operation;
        lock (_sync) { if (_disposed) return; _disposed = true; _stop.Cancel(); operation = _operation; }
        await operation.ConfigureAwait(false);
        _stop.Dispose();
    }
}
