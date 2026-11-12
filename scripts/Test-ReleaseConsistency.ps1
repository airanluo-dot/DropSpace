Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")
. (Join-Path $PSScriptRoot "ReleaseNotes.ps1")

$releaseInfo = Assert-DropSpaceNewReleaseVersion ((Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw -Encoding UTF8).Trim())
# Also check current prose against the canonical package and release metadata.
& node (Join-Path $PSScriptRoot "test-release-metadata.mjs") $repositoryRoot
if ($LASTEXITCODE -ne 0) { throw "Release metadata consistency failed." }
$null = Get-DropSpaceUpdateSummary -RepositoryRoot $repositoryRoot -Tag $releaseInfo.Tag
Write-Host "Release metadata and updater summary verified for $($releaseInfo.Tag)."
