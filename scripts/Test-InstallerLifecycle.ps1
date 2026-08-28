param(
    [string]$CurrentInstaller = "artifacts/installer/DropSpaceSetup.exe",

    [string]$PortableExecutable = "artifacts/release/DropSpace.exe",

    [switch]$AllowUserDataMutation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:DROPSPACE_TEST_MODE = "1"

if (-not $AllowUserDataMutation)
{
    throw "Installer lifecycle testing mutates %LOCALAPPDATA%\DropSpace. Re-run with -AllowUserDataMutation only in an isolated Windows test account/runner."
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$currentInstallerPath = if ([System.IO.Path]::IsPathRooted($CurrentInstaller))
{
    [System.IO.Path]::GetFullPath($CurrentInstaller)
}
else
{
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $CurrentInstaller))
}
$portablePath = if ([System.IO.Path]::IsPathRooted($PortableExecutable))
{
    [System.IO.Path]::GetFullPath($PortableExecutable)
}
else
{
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $PortableExecutable))
}
$releaseTag = (Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw).Trim()
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")
$releaseInfo = Get-DropSpaceReleaseInfo $releaseTag
$currentVersion = $releaseInfo.SemanticVersion
$baselineVersion = Get-DropSpaceLifecycleBaselineVersion $releaseInfo
$baselineVersionCode = (Get-DropSpaceReleaseInfo "v$baselineVersion").VersionCode
$testBase = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP))
{
    [System.IO.Path]::GetTempPath()
}
else
{
    $env:RUNNER_TEMP
}
$testRoot = Join-Path $testBase "DropSpace-installer-$([Guid]::NewGuid().ToString('N'))"
$installPath = Join-Path $testRoot "CustomInstallLocation"
$baselineOutput = Join-Path $testRoot "baseline"
$dataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "DropSpace"
$startMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\DropSpace.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) "DropSpace.lnk"
$customRegistryPath = "HKCU:\Software\DropSpace\Install"
$shellVerbPath = "HKCU:\Software\Classes\AllFileSystemObjects\shell\DropSpace.SendToSpace"
$shellVerbCommandPath = "$shellVerbPath\command"
$sendToShortcut = Join-Path $env:APPDATA "Microsoft\Windows\SendTo\DropSpace.lnk"
$startupRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runningProcess = $null
$restartProcess = $null

function Invoke-CheckedProcess
{
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$Description = "process",
        [string]$LogPath = "",
        [int]$TimeoutSeconds = 180
    )

    $effectiveArguments = @($Arguments)
    if (-not [string]::IsNullOrWhiteSpace($LogPath))
    {
        $effectiveArguments += "/LOG=$LogPath"
    }

    $process = Start-Process -FilePath $FilePath -ArgumentList $effectiveArguments -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000))
    {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($LogPath) -and (Test-Path $LogPath -PathType Leaf))
        {
            Write-Host "---- $Description log tail ----"
            Get-Content $LogPath -Tail 120 | Write-Host
        }

        throw "$Description did not exit within $TimeoutSeconds seconds."
    }

    $process.Refresh()
    if ($process.ExitCode -ne 0)
    {
        if (-not [string]::IsNullOrWhiteSpace($LogPath) -and (Test-Path $LogPath -PathType Leaf))
        {
            Write-Host "---- $Description log tail ----"
            Get-Content $LogPath -Tail 120 | Write-Host
        }

        throw "$Description failed with exit code $($process.ExitCode)."
    }
}

function Get-UninstallEntry
{
    $root = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall"
    return Get-ChildItem $root -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.DisplayName -eq "DropSpace" } |
        Select-Object -First 1
}

function Assert-X64Executable
{
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try
    {
        $reader = [System.IO.BinaryReader]::new($stream)
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset + 4
        $machine = $reader.ReadUInt16()
        if ($machine -ne 0x8664)
        {
            throw "$Path is not an x64 PE image (machine 0x$($machine.ToString('x4')))."
        }
    }
    finally
    {
        $stream.Dispose()
    }
}

function Get-UninstallerPath
{
    $path = Get-ChildItem (Join-Path $installPath "uninstall") -Filter "unins*.exe" -File |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($path))
    {
        throw "The independent Inno Setup uninstaller was not installed."
    }
    return $path
}

function Write-ApplicationStartupDiagnostics
{
    $logDirectory = Join-Path $dataRoot "logs"
    if (-not (Test-Path $logDirectory -PathType Container))
    {
        Write-Host "DropSpace startup diagnostics were not created."
        return
    }

    Write-Host "---- DropSpace startup diagnostics ----"
    foreach ($fileName in "crash.marker", "dropspace.log", "dropspace.log.1")
    {
        $path = Join-Path $logDirectory $fileName
        if (Test-Path $path -PathType Leaf)
        {
            Write-Host "[$fileName]"
            Get-Content -Path $path -Tail 40 | Write-Host
        }
    }
}

function Wait-ForMaintenanceEndpoint
{
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 45
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        $Process.Refresh()
        if ($Process.HasExited)
        {
            Write-ApplicationStartupDiagnostics
            throw "Installed baseline app exited before its maintenance endpoint became ready."
        }

        $mutex = $null
        if ([System.Threading.Mutex]::TryOpenExisting("Local\DropSpace.Running.v1", [ref]$mutex))
        {
            $mutex.Dispose()
            return
        }

        Start-Sleep -Milliseconds 200
    }

    throw "Installed baseline app did not expose its maintenance endpoint within $TimeoutSeconds seconds."
}

function Wait-PathRemoved
{
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutSeconds = 20
    )

    # Inno runs the final uninstaller/self-delete pass from a temporary process.
    # The launched unins*.exe can exit just before that helper removes the empty
    # uninstall and application directories, so observe the real filesystem
    # postcondition with a bounded wait instead of racing the helper.
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ((Test-Path $Path) -and [DateTime]::UtcNow -lt $deadline)
    {
        Start-Sleep -Milliseconds 200
    }

    if (Test-Path $Path)
    {
        $remaining = @(Get-ChildItem -Path $Path -Force -Recurse -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty FullName)
        throw "Uninstall did not remove '$Path' within $TimeoutSeconds seconds. Remaining entries: $($remaining -join '; ')"
    }
}

if (Test-Path $dataRoot)
{
    throw "The isolated runner already contains $dataRoot; refusing to risk pre-existing user data."
}
if (-not (Test-Path $currentInstallerPath -PathType Leaf) -or -not (Test-Path $portablePath -PathType Leaf))
{
    throw "Current installer or portable payload is missing."
}

New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try
{
    & (Join-Path $PSScriptRoot "Build-Installer.ps1") `
        -PortableExecutable $portablePath `
        -OutputDirectory $baselineOutput `
        -AppVersion $baselineVersion `
        -VersionCode $baselineVersionCode `
        -OutputBaseFilename "DropSpaceSetup-baseline"
    $baselineInstaller = Join-Path $baselineOutput "DropSpaceSetup-baseline.exe"

    Invoke-CheckedProcess $baselineInstaller @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
        "/DIR=$installPath", "/TASKS=desktopicon"
    ) "baseline silent install" (Join-Path $testRoot "baseline-install.log")

    $installedExe = Join-Path $installPath "DropSpace.exe"
    if (-not (Test-Path $installedExe -PathType Leaf)) { throw "Installed DropSpace.exe is missing." }
    Assert-X64Executable $installedExe
    $versionInfo = (Get-Item $installedExe).VersionInfo
    if ($versionInfo.ProductName -ne "DropSpace" -or $versionInfo.OriginalFilename -ne "DropSpace.exe")
    {
        throw "Installed executable metadata is invalid."
    }
    if (-not (Test-Path $startMenuShortcut) -or -not (Test-Path $desktopShortcut))
    {
        throw "Expected Start Menu/Desktop shortcuts were not installed."
    }
    if ($null -eq (Get-UninstallEntry) -or -not (Test-Path $customRegistryPath))
    {
        throw "Installed Apps or DropSpace install registry metadata is missing."
    }

    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $dataMarker = Join-Path $dataRoot "upgrade-preservation.marker"
    Set-Content -Path $dataMarker -Value "preserve across upgrade and normal uninstall" -Encoding utf8
    $selectedInstallPath = (Get-ItemProperty $customRegistryPath).InstallPath
    if ([System.IO.Path]::GetFullPath($selectedInstallPath) -ne [System.IO.Path]::GetFullPath($installPath))
    {
        throw "Custom installation path was not recorded."
    }

    $runningProcess = Start-Process -FilePath $installedExe -PassThru
    Wait-ForMaintenanceEndpoint $runningProcess

    try
    {
        Invoke-CheckedProcess $currentInstallerPath @(
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATE",
            "/MERGETASKS=explorercontext,sendtointegration"
        ) "in-place /UPDATE upgrade" (Join-Path $testRoot "upgrade.log")
    }
    catch
    {
        Write-ApplicationStartupDiagnostics
        throw
    }
    if (-not $runningProcess.WaitForExit(15000))
    {
        throw "In-place upgrade did not gracefully stop the running DropSpace process."
    }
    $runningProcess = $null
    if ((Get-Content (Join-Path $installPath "install.version") -Raw).Trim() -ne $currentVersion)
    {
        throw "In-place upgrade did not replace the program version marker."
    }
    if (-not (Test-Path $dataMarker)) { throw "In-place upgrade deleted user data." }
    if ([System.IO.Path]::GetFullPath((Get-ItemProperty $customRegistryPath).InstallPath) -ne [System.IO.Path]::GetFullPath($installPath))
    {
        throw "In-place upgrade reset the selected installation path."
    }
    if (-not (Test-Path $shellVerbPath) -or -not (Test-Path $sendToShortcut))
    {
        throw "In-place upgrade did not create both Windows shell integrations."
    }
    if ((Get-ItemPropertyValue -Path $shellVerbPath -Name "MUIVerb") -ne "Send to DropSpace" -or
        (Get-ItemPropertyValue -Path $shellVerbPath -Name "MultiSelectModel") -ne "Player" -or
        (Get-ItemPropertyValue -Path $shellVerbPath -Name "Icon") -notlike "*$installedExe,0")
    {
        throw "Explorer shell verb metadata is invalid."
    }
    $shellCommand = (Get-Item $shellVerbCommandPath).GetValue("")
    if ($shellCommand -notlike "*`"$installedExe`"*--shell-add*--source explorer-context-menu*")
    {
        throw "Explorer shell verb command does not target the current installation."
    }

    $restartDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $restartDeadline)
    {
        $restartProcess = Get-Process -Name DropSpace -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $restartProcess) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $restartProcess)
    {
        throw "/UPDATE did not automatically restart the installed DropSpace version."
    }

    $updatedMarker = Join-Path $dataRoot "Updates\last-update.json"
    $updatedMarkerDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $updatedMarkerDeadline)
    {
        $restartProcess.Refresh()
        if ($restartProcess.HasExited)
        {
            throw "The restarted app exited before persisting its update launch marker."
        }

        if ((Test-Path $updatedMarker -PathType Leaf) -and
            (Get-Content $updatedMarker -Raw) -match [regex]::Escape($currentVersion))
        {
            break
        }

        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path $updatedMarker -PathType Leaf) -or
        (Get-Content $updatedMarker -Raw) -notmatch [regex]::Escape($currentVersion))
    {
        throw "The restarted version did not persist the expected update launch marker within 45 seconds."
    }

    Wait-ForMaintenanceEndpoint $restartProcess
    Invoke-CheckedProcess `
        -FilePath $installedExe `
        -Arguments @("--shutdown-for-maintenance") `
        -Description "post-update graceful maintenance shutdown" `
        -TimeoutSeconds 30
    if (-not $restartProcess.WaitForExit(15000))
    {
        throw "The restarted app acknowledged maintenance shutdown but did not exit within 15 seconds."
    }
    $restartProcess = $null

    & (Join-Path $PSScriptRoot "Test-PortableSmoke.ps1") -ExecutablePath $installedExe

    $startupCommand = (Get-ItemProperty -Path $startupRegistryPath -Name "DropSpace" -ErrorAction Stop).DropSpace
    if ($startupCommand -notlike "*$installedExe*--startup*")
    {
        throw "Installed smoke did not create the expected per-user startup registration."
    }

    $uninstaller = Get-UninstallerPath
    Invoke-CheckedProcess $uninstaller @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/PURGEDATA=0"
    ) "normal uninstall" (Join-Path $testRoot "normal-uninstall.log")
    Wait-PathRemoved -Path $installPath
    if (Test-Path $installedExe) { throw "Normal uninstall left application files behind." }
    if ($null -ne (Get-UninstallEntry) -or (Test-Path $customRegistryPath))
    {
        throw "Normal uninstall left uninstall registry metadata behind."
    }
    if ((Test-Path $startMenuShortcut) -or (Test-Path $desktopShortcut))
    {
        throw "Normal uninstall left shortcuts behind."
    }
    if ((Test-Path $shellVerbPath) -or (Test-Path $sendToShortcut))
    {
        throw "Normal uninstall left Windows shell integrations behind."
    }
    if ($null -ne (Get-ItemProperty -Path $startupRegistryPath -Name "DropSpace" -ErrorAction SilentlyContinue))
    {
        throw "Normal uninstall left the DropSpace startup registration behind."
    }
    if (-not (Test-Path $dataMarker)) { throw "Normal uninstall did not preserve user data." }

    Invoke-CheckedProcess $currentInstallerPath @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=$installPath"
    ) "current reinstall" (Join-Path $testRoot "reinstall.log")
    $externalSentinel = Join-Path $testRoot "original-user-file.pdf"
    Set-Content -Path $externalSentinel -Value "must never be deleted" -Encoding utf8
    Set-Content -Path (Join-Path $dataRoot "referenced-original-path.marker") -Value $externalSentinel -Encoding utf8
    $uninstaller = Get-UninstallerPath
    Invoke-CheckedProcess $uninstaller @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/PURGEDATA=1"
    ) "complete uninstall" (Join-Path $testRoot "complete-uninstall.log")
    Wait-PathRemoved -Path $installPath
    if (Test-Path $dataRoot) { throw "Complete uninstall did not remove DropSpace-owned local data." }
    if (-not (Test-Path $externalSentinel))
    {
        throw "Complete uninstall deleted an external sentinel representing a user original file."
    }
    if ($null -ne (Get-UninstallEntry) -or (Test-Path $customRegistryPath) -or
        (Test-Path $startMenuShortcut) -or (Test-Path $desktopShortcut) -or
        (Test-Path $shellVerbPath) -or (Test-Path $sendToShortcut))
    {
        throw "Complete uninstall left DropSpace-owned registry or shortcut state behind."
    }
    if ($null -ne (Get-ItemProperty -Path $startupRegistryPath -Name "DropSpace" -ErrorAction SilentlyContinue))
    {
        throw "Complete uninstall left the DropSpace startup registration behind."
    }

    Write-Host "Installer lifecycle passed: silent per-user install, x64 metadata, Installed Apps, custom path, graceful /UPDATE shutdown, automatic restart marker, installed smoke, preserve-data uninstall, complete uninstall, external sentinel protection."
}
finally
{
    foreach ($process in @($restartProcess, $runningProcess))
    {
        if ($null -ne $process -and -not $process.HasExited)
        {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path $testRoot)
    {
        Remove-Item -Path $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
