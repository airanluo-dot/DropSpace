using DropSpace.Core.Abstractions;
using DropSpace.Core.Diagnostics;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>
/// Owns the serialized settings transaction across persisted settings and every
/// runtime subsystem affected by those settings. UI code supplies only its visual
/// preflight callback and receives a reconciled settings snapshot back.
/// </summary>
public sealed class SettingsApplicationCoordinator(
    ISettingsService settingsService,
    IStartupRegistrationService startupRegistration,
    GlobalQuickPanelHotkeyService quickPanelHotkey,
    ClipboardCaptureService clipboard,
    DeviceHandoffService deviceHandoff,
    CrossDeviceClipboardService crossDeviceClipboard,
    ILogger<SettingsApplicationCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        settingsService.LoadAsync(cancellationToken);

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await settingsService.SaveAsync(settings, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task EnsureStartupStateAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return startupRegistration.SetEnabledAsync(settings.StartWithWindows, cancellationToken);
    }

    public async Task<AppSettings> SetClipboardPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (paused)
            {
                await clipboard.PauseAsync(cancellationToken);
            }
            else
            {
                await clipboard.ResumeAsync(cancellationToken);
            }

            return await settingsService.LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(
        AppSettings current,
        AppSettings requested,
        Func<AppSettings, CancellationToken, Task>? uiPreflight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(requested);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var operationId = OperationCorrelation.New();
            logger.LogInformation("Settings operation {OperationId} started.", operationId);
            // The form may have been captured while another edit was still saving.
            // Merge its changed fields only, under the transaction gate.
            var persisted = await settingsService.LoadAsync(cancellationToken);
            var next = SettingsChangePolicy.Merge(current, requested, persisted);
            current = persisted;
            next = next.Validate();

            if (!string.Equals(next.QuickPanelHotkey, current.QuickPanelHotkey, StringComparison.OrdinalIgnoreCase) &&
                !quickPanelHotkey.CanRegister(next.QuickPanelHotkey))
            {
                throw new InvalidOperationException(
                    "The requested Quick Panel hotkey is already registered by another application.");
            }

            var rollback = new SettingsTransactionRollbackCoordinator();
            var stage = "SettingsStageAppearance";
            try
            {
                if (uiPreflight is not null)
                {
                    rollback.Committed("ui-preflight", () => uiPreflight(current, CancellationToken.None));
                    await uiPreflight(next, cancellationToken);
                }

                if (next.StartWithWindows != current.StartWithWindows)
                {
                    stage = "SettingsStageStartup";
                    rollback.Committed("startup", () => startupRegistration.SetEnabledAsync(current.StartWithWindows, CancellationToken.None));
                    await startupRegistration.SetEnabledAsync(next.StartWithWindows, cancellationToken);
                }

                stage = "SettingsStageClipboard";
                rollback.Committed("clipboard", () => clipboard.UpdateSettingsAsync(current, CancellationToken.None));
                await clipboard.UpdateSettingsAsync(next, cancellationToken);

                if (next.EnableDeviceHandoff != current.EnableDeviceHandoff)
                {
                    stage = "SettingsStageHandoff";
                    rollback.Committed("handoff", () => deviceHandoff.UpdateSettingsAsync(current, CancellationToken.None));
                    await deviceHandoff.UpdateSettingsAsync(next, cancellationToken);
                }

                // Capture limits and Pause are live inputs to automatic propagation. Keep
                // its snapshot current, but do not initialize a failed network feature
                // again as a side effect of changing an unrelated local preference.
                if (next.EnableCrossDeviceClipboard != current.EnableCrossDeviceClipboard || crossDeviceClipboard.IsEnabled)
                {
                    stage = "SettingsStageCrossClipboard";
                    rollback.Committed("cross-device-clipboard", () => crossDeviceClipboard.UpdateSettingsAsync(current, CancellationToken.None));
                    await crossDeviceClipboard.UpdateSettingsAsync(next, cancellationToken);
                }

                stage = "SettingsStageStore";
                rollback.Committed("settings-store", () => settingsService.UpdateAsync(latest => current with
                {
                    ClipboardPaused = latest.ClipboardPaused,
                    LastUpdateCheckUtc = latest.LastUpdateCheckUtc,
                }, CancellationToken.None));
                next = await settingsService.UpdateAsync(latest => next with
                {
                    ClipboardPaused = latest.ClipboardPaused,
                    LastUpdateCheckUtc = latest.LastUpdateCheckUtc,
                }, cancellationToken);
                logger.LogInformation("Settings operation {OperationId} committed.", operationId);
                return next;
            }
            catch (Exception updateException)
            {
                logger.LogWarning(updateException, "Settings operation {OperationId} failed; rollback started.", operationId);
                var rollbackFailures = await rollback.RollbackAsync((category, exception) =>
                    logger.LogError(
                        "Settings operation {OperationId} rollback failed in {Category}: {FailureType}.",
                        operationId,
                        category,
                        exception.GetType().Name));
                if (rollbackFailures.Count > 0)
                {
                    var reconciliationFailures = await ReconcileAsync(rollbackFailures);
                    if (reconciliationFailures.Count > 0)
                    {
                        logger.LogCritical(
                            "Settings update rollback and reconciliation both had failures. Rollback={RollbackFailures}, Reconciliation={ReconciliationFailures}.",
                            rollbackFailures.Count,
                            reconciliationFailures.Count);
                        throw new SettingsUpdateException("SettingsStageRecovery", operationId, new AggregateException(
                            "The settings update failed and the previous runtime state could not be fully reconciled. Restart DropSpace before changing settings again.",
                            new[] { updateException }
                                .Concat(rollbackFailures.Select(failure => failure.Exception))
                                .Concat(reconciliationFailures)));
                    }
                }

                if (updateException is OperationCanceledException) throw;
                throw new SettingsUpdateException(stage, operationId, updateException);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> RecoverPersistedStateAsync(AppSettings fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        try
        {
            return await settingsService.LoadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(exception, "Persisted settings could not be reloaded after a failed settings transaction.");
            return fallback;
        }
    }

    public async Task<AppSettings> UpdateLastCheckAsync(
        AppSettings current,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var updated = await settingsService.UpdateAsync(settings =>
                settings.LastUpdateCheckUtc is { } previous && previous >= checkedAt
                    ? settings
                    : settings with { LastUpdateCheckUtc = checkedAt.ToUniversalTime() }, cancellationToken);
            return current with { LastUpdateCheckUtc = updated.LastUpdateCheckUtc };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<Exception>> ReconcileAsync(IReadOnlyList<SettingsRollbackFailure> rollbackFailures)
    {
        var failures = new List<Exception>();
        // Retry only failed compensation steps, in their original reverse order.
        // Recovery must not start an unrelated network service or write its state.
        foreach (var failure in rollbackFailures)
        {
            try { await failure.Retry(); }
            catch (Exception exception)
            {
                failures.Add(exception);
                logger.LogError(exception, "Settings reconciliation failed in {Category}.", failure.Category);
            }
        }
        return failures;
    }

    public void Dispose() => _gate.Dispose();
}
