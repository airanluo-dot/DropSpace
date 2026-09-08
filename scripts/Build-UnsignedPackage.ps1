param(
    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "src/DropSpace.App/DropSpace.App.csproj"
$packageDirectory = Join-Path $repositoryRoot "artifacts/msix"
$runtimeIdentifier = "win-x64"
$releaseTag = (Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw).Trim()
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")
$releaseInfo = Get-DropSpaceReleaseInfo $releaseTag

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

$arguments = @(
    "build",
    $projectPath,
    "-c", $Configuration,
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:UapAppxPackageBuildMode=SideloadOnly",
    "-p:AppxBundle=Never",
    "-p:AppxPackageSigningEnabled=false",
    # Preview.20 publishes the package without app-local symbol artifacts. The
    # hosted toolchain does not provide mspdbcmf.exe, so this policy is explicit
    # and is checked after every package build.
    "-p:AppxPackageIncludePrivateSymbols=false",
    "-p:AppxPackageIncludePublicSymbols=false",
    "-p:PackageCertificateThumbprint=",
    "-p:AppxPackageVersion=$($releaseInfo.PackageVersion)",
    "-p:AppxPackageDir=$packageDirectory\"
)

if ($NoRestore)
{
    $arguments += "--no-restore"
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0)
{
    throw "MSIX package build failed with exit code $LASTEXITCODE."
}

$packages = Get-ChildItem -Path $packageDirectory -Recurse -File |
    Where-Object { $_.Extension -in ".msix", ".appx" }
if ($packages.Count -eq 0)
{
    throw "The build completed without producing an MSIX/AppX package."
}

Write-Host "Unsigned package output: $packageDirectory"
