Set-StrictMode -Version Latest

# Keep deployment scripts on the same minimum as the app's target framework and
# package manifests. This file intentionally uses Windows PowerShell-compatible
# syntax because release scripts run through powershell.exe on hosted Windows.
$script:DropSpaceMinimumWindowsBuild = 17763
$script:DropSpaceMinimumWindowsVersion = "10.0.17763.0"
$script:DropSpaceWindows11Build = 22000

function Get-DropSpaceWindowsCompatibility
{
    [pscustomobject]@{
        MinimumBuild = $script:DropSpaceMinimumWindowsBuild
        MinimumVersion = $script:DropSpaceMinimumWindowsVersion
        Windows11Build = $script:DropSpaceWindows11Build
    }
}
