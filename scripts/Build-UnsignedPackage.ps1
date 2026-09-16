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
$releaseTag = (Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw -Encoding UTF8).Trim()
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")
$releaseInfo = Get-DropSpaceReleaseInfo $releaseTag

if (Test-Path $packageDirectory)
{
    # Package generation can be incremental. Remove the exact release-artifact root
    # first so a previous version cannot be mistaken for the current package.
    Remove-Item -Path $packageDirectory -Recurse -Force
}
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
    # Beta.25 package publishes without app-local symbol artifacts. The
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

$packages = @(Get-ChildItem -Path $packageDirectory -Recurse -File |
    Where-Object { $_.Extension -in ".msix", ".appx" })
$expectedPrefix = "DropSpace.App_$($releaseInfo.PackageVersion)_"
$appPackages = @($packages | Where-Object { $_.Name.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) })
if ($appPackages.Count -eq 0)
{
    throw "The build did not produce an MSIX/AppX package for version $($releaseInfo.PackageVersion)."
}

if ($appPackages.Count -ne 1)
{
    throw "The build produced an unexpected number of current DropSpace packages: $($appPackages.Count)."
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($appPackages[0].FullName)
try
{
    $manifestEntry = $archive.GetEntry("AppxManifest.xml")
    if ($null -eq $manifestEntry)
    {
        throw "The generated MSIX package has no AppxManifest.xml."
    }

    $reader = New-Object System.IO.StreamReader($manifestEntry.Open())
    try
    {
        [xml]$packageManifest = $reader.ReadToEnd()
    }
    finally
    {
        $reader.Dispose()
    }
}
finally
{
    $archive.Dispose()
}

if ($packageManifest.Package.Identity.Version -ne $releaseInfo.PackageVersion)
{
    throw "The generated MSIX package manifest version '$($packageManifest.Package.Identity.Version)' does not match '$($releaseInfo.PackageVersion)'."
}

Write-Host "Unsigned package output: $packageDirectory"
Write-Host "Package: $($appPackages[0].FullName)"
Write-Host "Version: $($releaseInfo.PackageVersion)"
