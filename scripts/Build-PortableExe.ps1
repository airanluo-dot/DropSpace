param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Version = "",

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "src/DropSpace.App/DropSpace.App.csproj"
$publishDirectory = Join-Path $repositoryRoot "artifacts/portable/win-x64"
$releaseDirectory = Join-Path $repositoryRoot "artifacts/release"
$releaseTag = (Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw).Trim()
if ($releaseTag -notmatch '^v(?<prefix>[0-9]+\.[0-9]+\.[0-9]+)-preview\.(?<preview>[0-9]+)$')
{
    throw "RELEASE_VERSION is not a supported preview version: $releaseTag"
}
$repositoryVersion = $releaseTag.Substring(1)
if ([string]::IsNullOrWhiteSpace($Version))
{
    $Version = $repositoryVersion
}
elseif ($Version -ne $repositoryVersion)
{
    throw "Requested version $Version does not match RELEASE_VERSION $repositoryVersion."
}
$fileVersion = "$($Matches.prefix).$($Matches.preview)"

if (Test-Path $publishDirectory)
{
    Remove-Item -Path $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

$arguments = @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:Platform=x64",
    "-p:DropSpaceDeployment=Portable",
    "-p:WindowsPackageType=None",
    "-p:WindowsAppSDKSelfContained=true",
    "-p:PublishSingleFile=true",
    "-p:IncludeAllContentForSelfExtract=true",
    "-p:EnableMsixTooling=true",
    "-p:PublishTrimmed=false",
    "-p:PublishReadyToRun=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    "-p:PublishDir=$publishDirectory\"
)

if ($NoRestore)
{
    $arguments += "--no-restore"
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0)
{
    throw "Portable EXE publish failed with exit code $LASTEXITCODE."
}

$publishedExe = Join-Path $publishDirectory "DropSpace.exe"
if (-not (Test-Path $publishedExe -PathType Leaf))
{
    throw "Publish completed without producing DropSpace.exe."
}

$versionInfo = (Get-Item $publishedExe).VersionInfo
if ($versionInfo.ProductName -ne "DropSpace" -or
    $versionInfo.FileDescription -notlike "DropSpace*" -or
    $versionInfo.InternalName -ne "DropSpace" -or
    $versionInfo.OriginalFilename -ne "DropSpace.exe" -or
    $versionInfo.FileVersion -ne $fileVersion -or
    $versionInfo.ProductVersion -ne $Version)
{
    throw "DropSpace.exe version metadata is incomplete."
}

$releaseExe = Join-Path $releaseDirectory "DropSpace.exe"
Copy-Item -Path $publishedExe -Destination $releaseExe -Force
$hash = (Get-FileHash -Path $releaseExe -Algorithm SHA256).Hash.ToLowerInvariant()
$length = (Get-Item $releaseExe).Length

Write-Host "Portable EXE: $releaseExe"
Write-Host "Bytes: $length"
Write-Host "SHA256: $hash"
Write-Host "ProductName: $($versionInfo.ProductName)"
Write-Host "FileDescription: $($versionInfo.FileDescription)"
Write-Host "InternalName: $($versionInfo.InternalName)"
Write-Host "OriginalFilename: $($versionInfo.OriginalFilename)"
Write-Host "FileVersion: $($versionInfo.FileVersion)"
Write-Host "ProductVersion: $($versionInfo.ProductVersion)"
