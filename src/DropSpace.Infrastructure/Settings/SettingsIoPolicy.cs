using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Settings;

public static class SettingsIoPolicy
{
    public const long MaximumSettingsBytes = 1_048_576;
    public const int MaximumReadAttempts = 3;
    public const int ReadBufferBytes = 16 * 1024;
    public const int RetryDelayMilliseconds = 40;
    public const int MaximumQuarantineFiles = 20;

    public static async Task<byte[]> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Exception? lastException = null;

        for (var attempt = 0; attempt < MaximumReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path))
                {
                    return [];
                }

                var info = new FileInfo(path);
                if (info.Length > MaximumSettingsBytes)
                {
                    throw new JsonException("settings.json exceeds the supported size limit.");
                }

                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    ReadBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumSettingsBytes)
                {
                    throw new JsonException("settings.json exceeds the supported size limit.");
                }

                using var buffer = new MemoryStream(checked((int)stream.Length));
                await stream.CopyToAsync(buffer, ReadBufferBytes, cancellationToken).ConfigureAwait(false);
                if (buffer.Length > MaximumSettingsBytes)
                {
                    throw new JsonException("settings.json exceeds the supported size limit.");
                }

                return buffer.ToArray();
            }
            catch (IOException exception) when (attempt + 1 < MaximumReadAttempts)
            {
                lastException = exception;
            }
            catch (UnauthorizedAccessException exception) when (attempt + 1 < MaximumReadAttempts)
            {
                lastException = exception;
            }

            await Task.Delay(
                    TimeSpan.FromMilliseconds(RetryDelayMilliseconds * (attempt + 1)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw lastException ?? new IOException("The settings file could not be read.");
    }

    public static void TryDeleteTemporary(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "A settings temporary file could not be removed.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogDebug(exception, "A settings temporary file could not be removed.");
        }
    }

    public static void TrimQuarantine(string directory, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var stale = Directory.EnumerateFiles(directory, "settings-*.json", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaximumQuarantineFiles)
                .ToArray();
            foreach (var file in stale)
            {
                TryDeleteTemporary(file.FullName, logger);
            }
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "Settings quarantine cleanup was deferred.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogDebug(exception, "Settings quarantine cleanup was deferred.");
        }
    }
}
