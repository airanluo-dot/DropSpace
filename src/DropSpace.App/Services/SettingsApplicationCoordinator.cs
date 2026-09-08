using DropSpace.Core.Abstractions;
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
            // A settings form does not own process-driven pause or update-check state.
            var next = requested with
            {
                ClipboardPaused = current.ClipboardPaused,
                LastUpdateCheckUtc = current.LastUpdateCheckUtc,
            };
            next.Validate();

            if (!string.Equals(next.QuickPanelHotkey, current.QuickPanelHotkey, StringComparison.OrdinalIgnoreCase) &&
                !quickPanelHotkey.CanRegister(next.QuickPanelHotkey))
            {
                throw new InvalidOperationException(
                    "The requested Quick Panel hotkey is already registered by another application.");
            }

            var rollback = new SettingsTransactionRollbackCoordinator();
            try
            {
                if (uiPreflight is not null)
                {
                    rollback.Committed("ui-preflight", () => uiPreflight(current, CancellationToken.None));
                    await uiPreflight(next, cancellationToken);
                }

                rollback.Committed(
                    "startup",
                    () => startupRegistration.SetEnabledAsync(current.StartWithWindows, CancellationToken.None));
                await startupRegistration.SetEnabledAsync(next.StartWithWindows, cancellationToken);

                rollback.Committed("clipboard", () => clipboard.UpdateSettingsAsync(current, CancellationToken.None));
                await clipboard.UpdateSettingsAsync(next, cancellationToken);

                rollback.Committed("handoff", () => deviceHandoff.UpdateSettingsAsync(current, CancellationToken.None));
                await deviceHandoff.UpdateSettingsAsync(next, cancellationToken);

                rollback.Committed(
                    "cross-device-clipboard",
                    () => crossDeviceClipboard.UpdateSettingsAsync(current, CancellationToken.None));
                await crossDeviceClipboard.UpdateSettingsAsync(next, cancellationToken);

                rollback.Committed("settings-store", () => settingsService.SaveAsync(current, CancellationToken.None));
                await settingsService.SaveAsync(next, cancellationToken);
                return next;
            }
            catch (Exception updateException)
            {
                var rollbackFailures = await rollback.RollbackAsync((category, exception) =>
                    logger.LogError(
                        "Settings rollback failed in {Category}: {FailureType}.",
                        category,
                        exception.GetType().Name));
                if (rollbackFailures.Count > 0)
                {
                    var reconciliationFailures = await ReconcileAsync(current, uiPreflight);
                    if (reconciliationFailures.Count > 0)
                    {
                        logger.LogCritical(
                            "Settings update rollback and reconciliation both had failures. Rollback={RollbackFailures}, Reconciliation={ReconciliationFailures}.",
                            rollbackFailures.Count,
                            reconciliationFailures.Count);
                        throw new AggregateException(
                            "The settings update failed and the previous runtime state could not be fully reconciled. Restart DropSpace before changing settings again.",
                            new[] { updateException }
                                .Concat(rollbackFailures.Select(failure => failure.Exception))
                                .Concat(reconciliationFailures));
                    }
                }

                throw;
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

    private async Task<IReadOnlyList<Exception>> ReconcileAsync(
        AppSettings previous,
        Func<AppSettings, CancellationToken, Task>? uiPreflight)
    {
        var failures = new List<Exception>();

        async Task AttemptAsync(string category, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                logger.LogError(exception, "Settings reconciliation failed in {Category}.", category);
            }
        }

        if (uiPreflight is not null)
        {
            await AttemptAsync("ui-preflight", () => uiPreflight(previous, CancellationToken.None));
        }

        await AttemptAsync(
            "startup",
            () => startupRegistration.SetEnabledAsync(previous.StartWithWindows, CancellationToken.None));
        await AttemptAsync("clipboard", () => clipboard.UpdateSettingsAsync(previous, CancellationToken.None));
        await AttemptAsync("handoff", () => deviceHandoff.UpdateSettingsAsync(previous, CancellationToken.None));
        await AttemptAsync(
            "cross-device-clipboard",
            () => crossDeviceClipboard.UpdateSettingsAsync(previous, CancellationToken.None));
        await AttemptAsync("settings-store", () => settingsService.SaveAsync(previous, CancellationToken.None));
        return failures;
    }

    public void Dispose() => _gate.Dispose();
}
