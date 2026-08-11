param(
    [string]$PortableExecutable = "artifacts/release/DropSpace.exe",

    [string]$OutputDirectory = "artifacts/installer",

    [string]$AppVersion = "",

    [int]$VersionCode = 0,

    [string]$OutputBaseFilename = "DropSpaceSetup",

    [string]$IdentityPackage = "",

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
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")
$repositoryRelease = Get-DropSpaceReleaseInfo $releaseTag

if ([string]::IsNullOrWhiteSpace($AppVersion))
{
    $AppVersion = $repositoryRelease.SemanticVersion
}
$releaseInfo = Get-DropSpaceReleaseInfo "v$AppVersion"
$versionInfoVersion = $releaseInfo.FileVersion
if ($VersionCode -eq 0)
{
    $VersionCode = $releaseInfo.VersionCode
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
if (-not [string]::IsNullOrWhiteSpace($IdentityPackage))
{
    $identityPath = if ([System.IO.Path]::IsPathRooted($IdentityPackage))
    {
        [System.IO.Path]::GetFullPath($IdentityPackage)
    }
    else
    {
        [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $IdentityPackage))
    }
    if (-not (Test-Path $identityPath -PathType Leaf))
    {
        throw "Identity package does not exist: $identityPath"
    }
    $identitySignature = Get-AuthenticodeSignature $identityPath
    if ($identitySignature.Status -ne "Valid")
    {
        throw "External-location identity packages may only be embedded after trusted signing. Signature status: $($identitySignature.Status)."
    }
    $compilerArguments = $compilerArguments[0..($compilerArguments.Count - 2)] +
        "/DIdentityPackage=$identityPath" +
        $compilerArguments[-1]
}
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
$numericFileVersion = "$($versionInfo.FileMajorPart).$($versionInfo.FileMinorPart).$($versionInfo.FileBuildPart).$($versionInfo.FilePrivatePart)"
$fileTextVersion = $versionInfo.FileVersion.Trim()
$productTextVersion = $versionInfo.ProductVersion.Trim()
if ($numericFileVersion -ne $versionInfoVersion -or
    $fileTextVersion -ne $AppVersion -or
    $productTextVersion -ne $AppVersion)
{
    throw "Installer version metadata mismatch: numeric=$numericFileVersion, fileText=$fileTextVersion, productText=$productTextVersion."
}

Write-Host "Installer: $installerPath"
Write-Host "Inno Setup: 7.0.2"
Write-Host "AppId: E11EC281-BCE7-4F98-8EEF-2387E202CF0F"
Write-Host "AppVersion: $AppVersion"
Write-Host "VersionCode: $VersionCode"
Write-Host "Bytes: $((Get-Item $installerPath).Length)"
Write-Host "SHA256: $((Get-FileHash $installerPath -Algorithm SHA256).Hash.ToLowerInvariant())"
