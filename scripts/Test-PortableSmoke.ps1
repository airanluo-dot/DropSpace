param(
    [string]$ExecutablePath = "artifacts/release/DropSpace.exe",

    [ValidateSet("en-US", "zh-CN")]
    [string]$Language = "en-US",

    [int]$StartupTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "WindowsCompatibility.ps1")
$windowsCompatibility = Get-DropSpaceWindowsCompatibility
$resolvedExecutable = if ([System.IO.Path]::IsPathRooted($ExecutablePath))
{
    $ExecutablePath
}
else
{
    Join-Path $repositoryRoot $ExecutablePath
}

if (-not (Test-Path $resolvedExecutable -PathType Leaf))
{
    throw "Portable smoke test executable does not exist: $resolvedExecutable"
}

$first = $null
$second = $null
$startup = $null
$startupSecond = $null
$markerPath = $null
$startupMarkerPath = $null

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class DropSpaceWindowVisibility
{
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, char[] text, int capacity);

    public static int[] GetVisibleMainWindows(int processId)
    {
        var windows = new List<int>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var windowProcessId);
            if (windowProcessId == processId && IsWindowVisible(window))
            {
                var title = new char[256];
                var length = GetWindowText(window, title, title.Length);
                if (new string(title, 0, length) == "DropSpace")
                {
                    windows.Add(window.ToInt32());
                }
            }

            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
}
"@

try
{
    $first = Start-Process -FilePath $resolvedExecutable -ArgumentList "--smoke-test", "--smoke-hold", "--smoke-language", $Language -PassThru
    $markerPath = Join-Path ([System.IO.Path]::GetTempPath()) "DropSpace-smoke-$($first.Id).json"
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $lastStage = "process-launch"
    $marker = $null
    while ($true)
    {
        if (Test-Path $markerPath -PathType Leaf)
        {
            try
            {
                $marker = Get-Content -Path $markerPath -Raw | ConvertFrom-Json
                if ($null -ne $marker.stage)
                {
                    $lastStage = [string]$marker.stage
                }

                if ($marker.failed -eq $true)
                {
                    $detail = if ([string]::IsNullOrWhiteSpace([string]$marker.errorDetail)) { "" } else { "`n$($marker.errorDetail)" }
                    throw "DropSpace.exe smoke failed during '$lastStage' ($($marker.exceptionType), HRESULT $($marker.errorCode)): $($marker.error)$detail"
                }

                if ($marker.ready -eq $true)
                {
                    break
                }
            }
            catch [System.Management.Automation.PipelineStoppedException]
            {
                throw
            }
            catch
            {
                if ($_.Exception.Message -like "DropSpace.exe smoke failed*")
                {
                    throw
                }

                # The app replaces the marker atomically, but antivirus/file-system filters can
                # still expose a transient read race. Retry until the bounded deadline.
            }
        }

        if ($first.HasExited)
        {
            throw "DropSpace.exe exited before reporting startup readiness (exit $($first.ExitCode))."
        }

        if ([DateTime]::UtcNow -ge $deadline)
        {
            throw "DropSpace.exe did not report startup readiness within $StartupTimeoutSeconds seconds (last stage '$lastStage')."
        }

        Start-Sleep -Milliseconds 200
        $first.Refresh()
    }

    if ($marker.ready -ne $true -or
        $marker.storageWritable -ne $true -or
        [int]$marker.windowsBuild -lt $windowsCompatibility.MinimumBuild -or
        [int]$marker.minimumWindowsBuild -ne $windowsCompatibility.MinimumBuild -or
        [string]$marker.windowsRuntimeStatus -ne "Available" -or
        [int]$marker.schemaVersion -lt 1 -or
        [int]$marker.overlayCycles -ne 100 -or
        [int]$marker.overlayWindowCount -lt 1 -or
        [int]$marker.dragActivationHostCount -ne 0 -or
        $marker.clipboardListenerRegistered -ne $true -or
        [int]$marker.clipboardObservedUpdateDelta -lt 8 -or
        [int]$marker.clipboardSuccessfulCaptureDelta -lt 7 -or
        [int]$marker.clipboardSuppressedConsecutiveDuplicateDelta -lt 1 -or
        $marker.clipboardFirstTextPersisted -ne $true -or
        $marker.clipboardSecondTextPersisted -ne $true -or
        $marker.clipboardConsecutiveDuplicateSuppressionVerified -ne $true -or
        $marker.clipboardNonConsecutiveDuplicatePreserved -ne $true -or
        $marker.clipboardFileReferencePersisted -ne $true -or
        $marker.clipboardPauseVerified -ne $true -or
        $marker.clipboardResumeVerified -ne $true -or
        $marker.clipboardSelfWriteSuppressionVerified -ne $true -or
        $marker.startupRegistrationEnabled -ne $true -or
        $marker.noContinuousFrameLoop -ne $true -or
        [int]$marker.overlayGeometryStressCycles -ne 1000 -or
        [long]$marker.overlayRegionFailureCount -ne 0 -or
        $marker.idleTopEdgePassThrough -ne $true -or
        $marker.wakeModeSwitchVerified -ne $true -or
        $marker.compactVisualTargetDiscoverable -ne $true -or
        $marker.expandedVisualTargetDiscoverable -ne $true -or
        $marker.compactSyntheticCfHDropAccepted -ne $true -or
        $marker.expandedSyntheticCfHDropAccepted -ne $true -or
        $marker.expandedDropStayedOpen -ne $true -or
        [int]$marker.visibleDropAddedItemCount -ne 3 -or
        [int]$marker.projectionDeletionStressCycles -ne 200 -or
        [long]$marker.projectionUnhandledExceptionDelta -ne 0 -or
        [long]$marker.projectionUnobservedTaskExceptionDelta -ne 0 -or
        $marker.projectionExternalSentinelPreserved -ne $true -or
        $marker.localizedUiResourcesResolved -ne $true -or
        [string]$marker.resourceLanguage -ne $Language)
    {
        throw "DropSpace.exe produced an invalid startup marker."
    }

    $second = Start-Process -FilePath $resolvedExecutable -ArgumentList "--smoke-test" -PassThru
    if (-not $second.WaitForExit(15000))
    {
        throw "The second DropSpace instance did not redirect and exit within 15 seconds."
    }

    if ($second.ExitCode -ne 0)
    {
        throw "The second DropSpace instance exited with code $($second.ExitCode)."
    }

    if (-not $first.WaitForExit(30000))
    {
        throw "The primary DropSpace smoke process did not exit cleanly."
    }

    if ($first.ExitCode -ne 0)
    {
        throw "The primary DropSpace smoke process exited with code $($first.ExitCode)."
    }

    $startup = Start-Process -FilePath $resolvedExecutable -ArgumentList "--startup", "--smoke-test", "--smoke-hold", "--smoke-language", $Language -PassThru
    $startupMarkerPath = Join-Path ([System.IO.Path]::GetTempPath()) "DropSpace-smoke-$($startup.Id).json"
    $startupDeadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $startupLastStage = "process-launch"
    $startupMarker = $null
    while ($true)
    {
        if (Test-Path $startupMarkerPath -PathType Leaf)
        {
            try
            {
                $startupMarker = Get-Content -Path $startupMarkerPath -Raw | ConvertFrom-Json
                if ($null -ne $startupMarker.stage)
                {
                    $startupLastStage = [string]$startupMarker.stage
                }

                if ($startupMarker.failed -eq $true)
                {
                    $detail = if ([string]::IsNullOrWhiteSpace([string]$startupMarker.errorDetail)) { "" } else { "`n$($startupMarker.errorDetail)" }
                    throw "DropSpace.exe --startup smoke failed during '$startupLastStage' ($($startupMarker.exceptionType), HRESULT $($startupMarker.errorCode)): $($startupMarker.error)$detail"
                }

                if ($startupMarker.ready -eq $true)
                {
                    break
                }
            }
            catch [System.Management.Automation.PipelineStoppedException]
            {
                throw
            }
            catch
            {
                if ($_.Exception.Message -like "DropSpace.exe --startup smoke failed*")
                {
                    throw
                }
            }
        }

        $startup.Refresh()
        if ($startup.HasExited)
        {
            throw "DropSpace.exe --startup exited before reporting startup readiness (exit $($startup.ExitCode))."
        }

        if ([DateTime]::UtcNow -ge $startupDeadline)
        {
            throw "DropSpace.exe --startup did not report startup readiness within $StartupTimeoutSeconds seconds (last stage '$startupLastStage')."
        }

        Start-Sleep -Milliseconds 200
    }

    $visibleWindows = [DropSpaceWindowVisibility]::GetVisibleMainWindows($startup.Id)
    if ($visibleWindows.Count -gt 0)
    {
        throw "DropSpace.exe --startup exposed visible top-level window(s) after readiness: $($visibleWindows -join ', ')."
    }

    $startupSecond = Start-Process -FilePath $resolvedExecutable -ArgumentList "--smoke-test" -PassThru
    if (-not $startupSecond.WaitForExit(15000))
    {
        throw "The redirected activation against the startup instance did not exit within 15 seconds."
    }

    if ($startupSecond.ExitCode -ne 0)
    {
        throw "The redirected activation against the startup instance exited with code $($startupSecond.ExitCode)."
    }

    if (-not $startup.WaitForExit(30000))
    {
        throw "The primary DropSpace --startup smoke process did not exit cleanly."
    }

    if ($startup.ExitCode -ne 0)
    {
        throw "The primary DropSpace --startup smoke process exited with code $($startup.ExitCode)."
    }

    Write-Host "Portable smoke test passed: startup, Windows App SDK, SQLite, AppData, Win32 clipboard integration, default per-user startup registration, single instance, clean exit."
    Write-Host "Startup visibility regression passed: --startup initialized the process without a visible top-level window, and redirected activation remained functional."
    Write-Host "Windows compatibility probe: build=$($marker.windowsBuild), minimum=$($marker.minimumWindowsBuild), runtime=$($marker.windowsRuntimeStatus), Windows11 visuals: Mica=$($marker.modernWindowAppearanceAvailable), modernDwm=$($marker.modernDwmAttributesAvailable)"
    Write-Host "Localized resource context: $($marker.resourceLanguage); XAML resource resolution=passed"
    Write-Host "Clipboard integration: observed=$($marker.clipboardObservedUpdateDelta), captured=$($marker.clipboardSuccessfulCaptureDelta), consecutiveSuppressed=$($marker.clipboardSuppressedConsecutiveDuplicateDelta), failedReads=$($marker.clipboardFailedReadDelta), pause/resume/self-write=passed"
    Write-Host "Overlay 100-cycle resource deltas: handles=$($marker.overlayHandleDelta), GDI=$($marker.overlayGdiObjectDelta), USER=$($marker.overlayUserObjectDelta), privateBytes=$($marker.overlayPrivateBytesDelta)"
    Write-Host "Overlay geometry stress: transitions=$($marker.overlayGeometryStressCycles), regionFailures=$($marker.overlayRegionFailureCount), idleTopEdgePassThrough=$($marker.idleTopEdgePassThrough), wakeModeSwitch=$($marker.wakeModeSwitchVerified)"
    Write-Host "Visible Overlay targets: compact=$($marker.compactVisualTargetDiscoverable), expanded=$($marker.expandedVisualTargetDiscoverable)"
    Write-Host "Visible Overlay CF_HDROP pipeline: compact=$($marker.compactSyntheticCfHDropAccepted), expanded=$($marker.expandedSyntheticCfHDropAccepted), expandedStayedOpen=$($marker.expandedDropStayedOpen)"
    Write-Host "Main + Expanded deletion stress: cycles=$($marker.projectionDeletionStressCycles), unhandled=$($marker.projectionUnhandledExceptionDelta), unobserved=$($marker.projectionUnobservedTaskExceptionDelta), externalSentinel=$($marker.projectionExternalSentinelPreserved)"
}
finally
{
    foreach ($process in @($second, $startupSecond, $startup, $first))
    {
        if ($null -ne $process -and -not $process.HasExited)
        {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    if ($null -ne $markerPath -and (Test-Path $markerPath))
    {
        Remove-Item -Path $markerPath -Force
    }

    if ($null -ne $startupMarkerPath -and (Test-Path $startupMarkerPath))
    {
        Remove-Item -Path $startupMarkerPath -Force
    }
}
