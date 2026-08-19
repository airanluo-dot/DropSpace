Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")
. (Join-Path $PSScriptRoot "ReleaseNotes.ps1")

$releaseInfo = Get-DropSpaceReleaseInfo ((Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw).Trim())
$notesPath = Get-DropSpaceReleaseNotesPath -RepositoryRoot $repositoryRoot -Tag $releaseInfo.Tag
$notes = Get-Content $notesPath -Raw
$firstLine = ($notes -split '\r?\n', 2)[0].Trim()
if ($firstLine -notmatch "^#\s+DropSpace\s+$([regex]::Escape($releaseInfo.Tag))(?:\s|$)")
{
    throw "Release notes must start with a heading for $($releaseInfo.Tag)."
}

$summary = Get-DropSpaceUpdateSummary -RepositoryRoot $repositoryRoot -Tag $releaseInfo.Tag
foreach ($relativePath in @("README.md", "ROADMAP.md"))
{
    $contents = Get-Content (Join-Path $repositoryRoot $relativePath) -Raw
    if (-not $contents.Contains($releaseInfo.Tag, [StringComparison]::Ordinal))
    {
        throw "$relativePath does not mention the current release $($releaseInfo.Tag)."
    }
}

$readme = Get-Content (Join-Path $repositoryRoot "README.md") -Raw
if ($readme -match '(?i)Dynamic Island/Notch|Dynamic Island or Notch|灵动岛\s*/\s*刘海|灵动岛或刘海')
{
    throw "README.md still presents the removed Notch mode as an active product option."
}

Write-Host "Release consistency passed for $($releaseInfo.Tag)."
Write-Host "Manifest summary: $summary"
