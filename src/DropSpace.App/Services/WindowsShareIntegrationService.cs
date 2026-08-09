using System.Runtime.InteropServices;
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
    private readonly ILogger<WindowsShareIntegrationService> _logger;

    public WindowsShareIntegrationService(ILogger<WindowsShareIntegrationService> logger)
    {
        _logger = logger;
        HasPackageIdentity = DetectPackageIdentity();
    }

    public bool HasPackageIdentity { get; }

    public string StatusText => HasPackageIdentity
        ? "DropSpace 已拥有 Package Identity，并已声明为 Windows 分享目标。是否直接显示在 Drop Tray 建议区由 Windows 版本和系统相关性排序决定；可通过“更多”打开完整分享界面。"
        : "当前部署没有可信 Package Identity，Windows 分享集成不可用；顶部拖放和已显示灵动岛直接拖放仍然可用。正式签名安装包会自动启用分享目标。";

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
