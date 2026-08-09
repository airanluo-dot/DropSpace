param(
    [string]$ExecutablePath = "artifacts/release/DropSpace.exe",

    [int]$StartupTimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
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
$markerPath = $null
try
{
    $first = Start-Process -FilePath $resolvedExecutable -ArgumentList "--smoke-test", "--smoke-hold" -PassThru
    $markerPath = Join-Path ([System.IO.Path]::GetTempPath()) "DropSpace-smoke-$($first.Id).json"
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while (-not (Test-Path $markerPath -PathType Leaf))
    {
        if ($first.HasExited)
        {
            throw "DropSpace.exe exited before reporting startup readiness (exit $($first.ExitCode))."
        }

        if ([DateTime]::UtcNow -ge $deadline)
        {
            throw "DropSpace.exe did not report startup readiness within $StartupTimeoutSeconds seconds."
        }

        Start-Sleep -Milliseconds 200
        $first.Refresh()
    }

    $marker = Get-Content -Path $markerPath -Raw | ConvertFrom-Json
    if ($marker.ready -ne $true -or
        $marker.storageWritable -ne $true -or
        [int]$marker.schemaVersion -lt 1 -or
        [int]$marker.overlayCycles -ne 100 -or
        [int]$marker.overlayWindowCount -lt 1 -or
        [int]$marker.dragActivationHostCount -lt 1 -or
        $marker.clipboardListenerRegistered -ne $true -or
        [int]$marker.clipboardObservedUpdateDelta -lt 5 -or
        [int]$marker.clipboardSuccessfulCaptureDelta -lt 3 -or
        $marker.clipboardFirstTextPersisted -ne $true -or
        $marker.clipboardSecondTextPersisted -ne $true -or
        $marker.clipboardPauseVerified -ne $true -or
        $marker.clipboardResumeVerified -ne $true -or
        $marker.clipboardSelfWriteSuppressionVerified -ne $true -or
        $marker.noContinuousFrameLoop -ne $true)
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

    Write-Host "Portable smoke test passed: startup, Windows App SDK, SQLite, AppData, Win32 clipboard integration, single instance, clean exit."
    Write-Host "Clipboard integration: observed=$($marker.clipboardObservedUpdateDelta), captured=$($marker.clipboardSuccessfulCaptureDelta), failedReads=$($marker.clipboardFailedReadDelta), pause/resume/self-write=passed"
    Write-Host "Overlay 100-cycle resource deltas: handles=$($marker.overlayHandleDelta), GDI=$($marker.overlayGdiObjectDelta), USER=$($marker.overlayUserObjectDelta), privateBytes=$($marker.overlayPrivateBytesDelta)"
}
finally
{
    foreach ($process in @($second, $first))
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
}
