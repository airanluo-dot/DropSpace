using System.Text.Json;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppStoragePaths _paths;
    private readonly ILogger<JsonSettingsService> _logger;

    public JsonSettingsService(AppStoragePaths paths)
        : this(paths, NullLogger<JsonSettingsService>.Instance)
    {
    }

    public JsonSettingsService(AppStoragePaths paths, ILogger<JsonSettingsService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public SettingsRecoveryReport LastLoadRecovery { get; private set; } = SettingsRecoveryReport.None;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            LastLoadRecovery = SettingsRecoveryReport.None;
            if (!File.Exists(_paths.Settings))
            {
                return new AppSettings();
            }

            AppSettings? settings = null;
            try
            {
                await using var stream = new FileStream(
                    _paths.Settings,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    16_384,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                settings ??= new AppSettings();
                if (settings.Version == 1)
                {
                    settings = settings with { Version = AppSettings.CurrentVersion };
                }

                return settings.Validate();
            }
            catch (Exception exception) when (IsRecoverableSettingsFailure(exception))
            {
                var recovered = TryPreserveNonUiPreferences(settings, out var preservedNonUi);
                var quarantinePath = QuarantineSettingsFile();
                await SaveCoreAsync(recovered, cancellationToken).ConfigureAwait(false);
                LastLoadRecovery = new SettingsRecoveryReport(
                    true,
                    preservedNonUi,
                    Path.GetFileName(quarantinePath),
                    exception.GetType().Name);
                _logger.LogError(
                    exception,
                    "Invalid UI settings were quarantined as {QuarantineFileName}; non-UI preferences preserved={PreservedNonUi}. Database and payloads were not changed.",
                    Path.GetFileName(quarantinePath),
                    preservedNonUi);
                return recovered;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> ResetUiSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            AppSettings settings;
            try
            {
                settings = await LoadRawAsync(cancellationToken).ConfigureAwait(false);
                settings = settings with { Version = AppSettings.CurrentVersion };
                try
                {
                    settings.Validate();
                }
                catch (Exception exception) when (IsRecoverableSettingsFailure(exception))
                {
                    var quarantinePath = QuarantineSettingsFile();
                    _logger.LogWarning(
                        exception,
                        "The UI reset command quarantined invalid settings as {QuarantineFileName} before preserving valid non-UI preferences.",
                        Path.GetFileName(quarantinePath));
                    settings = TryPreserveNonUiPreferences(settings, out _);
                }
            }
            catch (Exception exception) when (IsRecoverableSettingsFailure(exception))
            {
                var quarantinePath = QuarantineSettingsFile();
                _logger.LogWarning(
                    exception,
                    "The UI reset command quarantined unreadable settings as {QuarantineFileName}.",
                    Path.GetFileName(quarantinePath));
                settings = new AppSettings();
            }

            var reset = settings.WithSafeUiPreferences().Validate();
            await SaveCoreAsync(reset, cancellationToken).ConfigureAwait(false);
            return reset;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppSettings> LoadRawAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.Settings))
        {
            return new AppSettings();
        }

        await using var stream = new FileStream(
            _paths.Settings,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? new AppSettings();
        return settings;
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        _paths.EnsureCreated();
        var temporaryPath = string.Concat(_paths.Settings, ".tmp");
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16_384,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _paths.Settings, true);
    }

    private static AppSettings TryPreserveNonUiPreferences(AppSettings? candidate, out bool preserved)
    {
        if (candidate is not null)
        {
            try
            {
                var recovered = candidate with { Version = AppSettings.CurrentVersion };
                recovered = recovered.WithSafeUiPreferences().Validate();
                preserved = true;
                return recovered;
            }
            catch (Exception exception) when (IsRecoverableSettingsFailure(exception))
            {
                // A non-UI field was also invalid. Full settings defaults are safer than carrying
                // malformed retention or payload limits into the running application.
            }
        }

        preserved = false;
        return new AppSettings();
    }

    private string QuarantineSettingsFile()
    {
        _paths.EnsureCreated();
        var name = $"settings-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
        var destination = Path.Combine(_paths.Quarantine, name);
        if (File.Exists(_paths.Settings))
        {
            File.Move(_paths.Settings, destination, false);
        }

        return destination;
    }

    private static bool IsRecoverableSettingsFailure(Exception exception) =>
        exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException;
}
