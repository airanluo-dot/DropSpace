using System.Text.Json;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Updates;
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
    private readonly UpdateChannel _freshUpdateChannel;

    public JsonSettingsService(AppStoragePaths paths)
        : this(paths, NullLogger<JsonSettingsService>.Instance, UpdateChannel.Stable)
    {
    }

    public JsonSettingsService(AppStoragePaths paths, ILogger<JsonSettingsService> logger)
        : this(paths, logger, UpdateChannel.Stable)
    {
    }

    public JsonSettingsService(
        AppStoragePaths paths,
        ILogger<JsonSettingsService> logger,
        UpdateChannel freshUpdateChannel)
    {
        _paths = paths;
        _logger = logger;
        _freshUpdateChannel = freshUpdateChannel;
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
                return CreateDefaults();
            }

            AppSettings? settings = null;
            try
            {
                var hadUpdateChannel = false;
                await using (var stream = new FileStream(
                                 _paths.Settings,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 16_384,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (stream.Length > 1_048_576)
                    {
                        throw new JsonException("settings.json exceeds the supported size limit.");
                    }

                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    hadUpdateChannel = document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.EnumerateObject().Any(property =>
                            string.Equals(property.Name, nameof(AppSettings.UpdateChannel), StringComparison.OrdinalIgnoreCase));
                    settings = document.RootElement.Deserialize<AppSettings>(SerializerOptions);
                }

                settings ??= CreateDefaults();
                var migratedVersion = false;
                if (settings.Version is >= 1 and < AppSettings.CurrentVersion)
                {
                    settings = settings with
                    {
                        Version = AppSettings.CurrentVersion,
                        // A legacy settings file without an update channel belongs to the Preview-era
                        // installed population. Fresh builds use the channel selected by their release kind.
                        UpdateChannel = hadUpdateChannel ? settings.UpdateChannel : UpdateChannel.Preview,
                    };
                    migratedVersion = true;
                }

                var validated = settings.Validate();
                if (migratedVersion)
                {
                    try
                    {
                        await SaveCoreAsync(validated, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(
                            exception,
                            "Settings schema migration reached version {Version} in memory but could not replace the persisted file.",
                            validated.Version);
                    }
                }

                return validated;
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
                settings = CreateDefaults();
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
            return CreateDefaults();
        }

        await using var stream = new FileStream(
            _paths.Settings,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? CreateDefaults();
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

    private AppSettings TryPreserveNonUiPreferences(AppSettings? candidate, out bool preserved)
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
        return CreateDefaults();
    }

    private AppSettings CreateDefaults() => new() { UpdateChannel = _freshUpdateChannel };

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
