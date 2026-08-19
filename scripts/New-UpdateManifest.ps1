param(
    [string]$ReleaseDirectory = "artifacts/release",
    [string]$OutputPath = "artifacts/release/update-manifest.json",
    [string]$PublishedAt = "",
    [string]$Summary = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")
. (Join-Path $PSScriptRoot "ReleaseNotes.ps1")
$releaseInfo = Get-DropSpaceReleaseInfo ((Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw).Trim())
$releaseSummary = if (-not [string]::IsNullOrWhiteSpace($Summary))
{
    $Summary.Trim()
}
else
{
    Get-DropSpaceUpdateSummary -RepositoryRoot $repositoryRoot -Tag $releaseInfo.Tag
}
if ($releaseSummary.Length -gt 500)
{
    throw "The update summary must not exceed 500 characters."
}
$directory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ReleaseDirectory))
$output = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
$installer = Join-Path $directory "DropSpaceSetup.exe"
$portable = Join-Path $directory "DropSpace.exe"
foreach ($path in @($installer, $portable))
{
    if (-not (Test-Path $path -PathType Leaf) -or (Get-Item $path).Length -le 0)
    {
        throw "Final update payload is missing or empty: $path"
    }
}

$timestamp = if ([string]::IsNullOrWhiteSpace($PublishedAt))
{
    [DateTimeOffset]::UtcNow
}
else
{
    [DateTimeOffset]::Parse($PublishedAt, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

$manifest = [ordered]@{
    schemaVersion = 1
    channel = $releaseInfo.Channel
    version = $releaseInfo.SemanticVersion
    versionCode = $releaseInfo.VersionCode
    publishedAt = $timestamp.ToString("o", [Globalization.CultureInfo]::InvariantCulture)
    minimumWindowsBuild = 26100
    mandatory = $false
    summary = $releaseSummary
    installer = [ordered]@{
        assetName = "DropSpaceSetup.exe"
        size = (Get-Item $installer).Length
        sha256 = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    portable = [ordered]@{
        assetName = "DropSpace.exe"
        size = (Get-Item $portable).Length
        sha256 = (Get-FileHash $portable -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($output)) | Out-Null
$manifest | ConvertTo-Json -Depth 5 | Set-Content $output -Encoding utf8NoBOM
Write-Host "Update manifest: $output"
Get-Content $output
