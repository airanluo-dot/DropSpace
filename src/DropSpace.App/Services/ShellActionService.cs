using System.Diagnostics;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;
using Windows.System;

namespace DropSpace.App.Services;

public sealed class ShellActionService(
    ClipboardCaptureService clipboard,
    ILogger<ShellActionService> logger)
{
    public async Task<bool> OpenHttpsAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && await Launcher.LaunchUriAsync(uri);
    }

    public Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(path);
            using var process = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not open a DropSpace-owned folder ({Category}).", exception.GetType().Name);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> OpenAsync(DropItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        if (item.File is not null)
        {
            var path = item.File.OriginalPath;
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                };
                using var process = Process.Start(info);
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning("Shell open failed for item {ItemId} ({Category}).", item.Id, exception.GetType().Name);
                return false;
            }
        }

        if (item.Url is not null && item.Url.Scheme is "http" or "https")
        {
            return await Launcher.LaunchUriAsync(new Uri(item.Url.NormalizedUrl));
        }

        return false;
    }

    public async Task<bool> ShowInFolderAsync(DropItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (item.File is null)
        {
            return false;
        }

        try
        {
            if (item.File.EntryKind == FileEntryKind.Folder)
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = item.File.OriginalPath,
                    UseShellExecute = true,
                });
            }
            else
            {
                var info = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = false,
                };
                info.ArgumentList.Add("/select,");
                info.ArgumentList.Add(item.File.OriginalPath);
                using var process = Process.Start(info);
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Show-in-folder failed for item {ItemId} ({Category}).", item.Id, exception.GetType().Name);
            return false;
        }
    }

    public Task CopyAsync(DropItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Text?.InlineText is string text)
        {
            return clipboard.CopyTextAsync(text, cancellationToken);
        }

        if (item.File is not null)
        {
            return clipboard.CopyFilesAsync([item.File.OriginalPath], cancellationToken);
        }

        if (item.Payload is not null && item.Kind == ItemKind.Image)
        {
            return clipboard.CopyImageAsync(item.Payload.RelativePath, cancellationToken);
        }

        throw new InvalidOperationException("The item has no copyable payload.");
    }
}
