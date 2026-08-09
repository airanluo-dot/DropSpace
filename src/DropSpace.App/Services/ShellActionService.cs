using System.Diagnostics;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;
using Windows.System;

namespace DropSpace.App.Services;

public sealed class ShellActionService(
    ClipboardCaptureService clipboard,
    ILogger<ShellActionService> logger)
{
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
                Process.Start(info);
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning(exception, "Shell open failed for item {ItemId}.", item.Id);
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
                Process.Start(new ProcessStartInfo
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
                Process.Start(info);
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "Show-in-folder failed for item {ItemId}.", item.Id);
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
