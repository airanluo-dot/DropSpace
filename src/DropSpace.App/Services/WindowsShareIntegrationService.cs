using System.Runtime.InteropServices;
using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Windows.System;

namespace DropSpace.App.Services;

/// <summary>
/// Reports the public Windows Share integration state without probing undocumented Drop Tray flags.
/// Package identity is required for a desktop Share Target; portable and unsigned Inno deployments
/// therefore degrade to the native DropSpace top-edge/visible-overlay targets.
/// </summary>
public sealed class WindowsShareIntegrationService
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<WindowsShareIntegrationService> _logger;

    public WindowsShareIntegrationService(
        IAppStringLocalizer strings,
        ILogger<WindowsShareIntegrationService> logger)
    {
        _strings = strings;
        _logger = logger;
        HasPackageIdentity = DetectPackageIdentity();
    }

    public bool HasPackageIdentity { get; }

    public string StatusText => HasPackageIdentity
        ? _strings.Get("WindowsSharePackagedStatus")
        : _strings.Get("WindowsSharePortableStatus");

    public async Task<bool> OpenDropTraySettingsAsync()
    {
        var opened = await Launcher.LaunchUriAsync(new Uri("ms-settings:multitasking"));
        _logger.LogInformation(
            "Windows multitasking settings launch requested for Drop Tray compatibility guidance; opened={Opened}. No undocumented Drop Tray state was read or changed.",
            opened);
        return opened;
    }

    private static bool DetectPackageIdentity()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return false;
        }

        if (result != ErrorInsufficientBuffer || length == 0)
        {
            return result == 0;
        }

        var packageFullName = new char[length];
        return GetCurrentPackageFullName(ref length, packageFullName) == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        [Out] char[]? packageFullName);
}
