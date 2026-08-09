param(
    [string]$PortableExecutable = "artifacts/release/DropSpace.exe",

    [string]$OutputDirectory = "artifacts/installer",

    [string]$AppVersion = "",

    [int]$VersionCode = 0,

    [string]$OutputBaseFilename = "DropSpaceSetup",

    [string]$InnoCompiler = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sourceExe = if ([System.IO.Path]::IsPathRooted($PortableExecutable))
{
    [System.IO.Path]::GetFullPath($PortableExecutable)
}
else
{
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $PortableExecutable))
}
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory))
{
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else
{
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$releaseTag = (Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw).Trim()
if ($releaseTag -notmatch '^v(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)-preview\.(?<preview>[0-9]+)$')
{
    throw "RELEASE_VERSION is not a supported preview version: $releaseTag"
}

if ([string]::IsNullOrWhiteSpace($AppVersion))
{
    $AppVersion = $releaseTag.Substring(1)
}
if ($AppVersion -notmatch '^(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)-preview\.(?<preview>[0-9]+)$')
{
    throw "AppVersion is not a supported preview version: $AppVersion"
}

$versionInfoVersion = "$($Matches.major).$($Matches.minor).$($Matches.patch).$($Matches.preview)"
if ($VersionCode -eq 0)
{
    $VersionCode = ([int]$Matches.major * 100000000) +
        ([int]$Matches.minor * 1000000) +
        ([int]$Matches.patch * 10000) +
        [int]$Matches.preview
}
if (-not (Test-Path $sourceExe -PathType Leaf))
{
    throw "Portable DropSpace.exe does not exist: $sourceExe"
}

if ([string]::IsNullOrWhiteSpace($InnoCompiler))
{
    $InnoCompiler = (& (Join-Path $PSScriptRoot "Install-InnoSetup.ps1") | Select-Object -Last 1)
}
if (-not (Test-Path $InnoCompiler -PathType Leaf))
{
    throw "ISCC.exe does not exist: $InnoCompiler"
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$scriptPath = Join-Path $repositoryRoot "installer/DropSpace.iss"
$compilerArguments = @(
    "/DAppVersion=$AppVersion",
    "/DVersionInfoVersion=$versionInfoVersion",
    "/DVersionCode=$VersionCode",
    "/DSourceExe=$sourceExe",
    "/DOutputDir=$outputPath",
    "/DOutputBaseFilename=$OutputBaseFilename",
    $scriptPath
)
& $InnoCompiler @compilerArguments
if ($LASTEXITCODE -ne 0)
{
    throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $outputPath "$OutputBaseFilename.exe"
if (-not (Test-Path $installerPath -PathType Leaf))
{
    throw "Inno Setup completed without producing $installerPath."
}

$versionInfo = (Get-Item $installerPath).VersionInfo
if ($versionInfo.FileVersion -ne $versionInfoVersion -or $versionInfo.ProductVersion -ne $AppVersion)
{
    throw "Installer version metadata mismatch: file=$($versionInfo.FileVersion), product=$($versionInfo.ProductVersion)."
}

Write-Host "Installer: $installerPath"
Write-Host "Inno Setup: 7.0.2"
Write-Host "AppId: E11EC281-BCE7-4F98-8EEF-2387E202CF0F"
Write-Host "AppVersion: $AppVersion"
Write-Host "VersionCode: $VersionCode"
Write-Host "Bytes: $((Get-Item $installerPath).Length)"
Write-Host "SHA256: $((Get-FileHash $installerPath -Algorithm SHA256).Hash.ToLowerInvariant())"
