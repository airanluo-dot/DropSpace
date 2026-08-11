param(
    [string]$ManifestPath = "artifacts/release/update-manifest.json",
    [string]$ReleaseDirectory = "artifacts/release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")
$releaseInfo = Get-DropSpaceReleaseInfo ((Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw).Trim())
$manifestFile = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ManifestPath))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ReleaseDirectory))
if (-not (Test-Path $manifestFile -PathType Leaf) -or (Get-Item $manifestFile).Length -gt 65536)
{
    throw "update-manifest.json is missing or exceeds 64 KiB."
}

$manifest = Get-Content $manifestFile -Raw | ConvertFrom-Json
$expectedTop = @("schemaVersion", "channel", "version", "versionCode", "publishedAt", "minimumWindowsBuild", "mandatory", "summary", "installer", "portable")
$actualTop = @($manifest.PSObject.Properties.Name)
if (@(Compare-Object ($expectedTop | Sort-Object) ($actualTop | Sort-Object)).Count -ne 0)
{
    throw "update-manifest.json has missing or unexpected top-level fields."
}
if ($manifest.schemaVersion -ne 1 -or $manifest.version -ne $releaseInfo.SemanticVersion -or
    $manifest.versionCode -ne $releaseInfo.VersionCode -or $manifest.channel -ne $releaseInfo.Channel -or
    $manifest.minimumWindowsBuild -lt 26100 -or $manifest.mandatory -ne $false)
{
    throw "update-manifest.json release metadata is inconsistent."
}

foreach ($entry in @(
    @{ Value = $manifest.installer; Name = "DropSpaceSetup.exe" },
    @{ Value = $manifest.portable; Name = "DropSpace.exe" }))
{
    $properties = @($entry.Value.PSObject.Properties.Name)
    if (@(Compare-Object @("assetName", "sha256", "size") ($properties | Sort-Object)).Count -ne 0 -or
        $entry.Value.assetName -ne $entry.Name -or $entry.Value.size -le 0 -or
        $entry.Value.sha256 -cnotmatch '^[0-9a-f]{64}$')
    {
        throw "Invalid manifest descriptor for $($entry.Name)."
    }
    $path = Join-Path $releaseRoot $entry.Name
    if ((Get-Item $path).Length -ne $entry.Value.size -or
        (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value.sha256)
    {
        throw "Manifest size/hash mismatch for $($entry.Name)."
    }
}

Write-Host "update-manifest.json passed strict schema, version, size, and SHA-256 validation."
