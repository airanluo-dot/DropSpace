param([string]$ReleaseDirectory = 'artifacts/release')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
. (Join-Path $PSScriptRoot 'ReleaseArtifactVersion.ps1')
$root = Split-Path $PSScriptRoot -Parent
$info = Get-DropSpaceReleaseInfo ((Get-Content (Join-Path $root 'RELEASE_VERSION') -Raw).Trim())
$directory = Join-Path $root $ReleaseDirectory
foreach ($entry in @(@{Name='DropSpace.exe'; Installer=$false}, @{Name='DropSpaceSetup.exe'; Installer=$true}))
{
    $path = Join-Path $directory $entry.Name
    Assert-DropSpaceExecutableVersion -Path $path -ReleaseInfo $info -Installer:$entry.Installer
}
Write-Host 'Real PE artifact versions match RELEASE_VERSION.'
