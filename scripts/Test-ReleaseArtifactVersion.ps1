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
    # Exercise the immediately preceding shipped Beta as well as a distant mismatch.
    foreach ($differentTag in @('v0.0.0-beta.1', 'v0.3.0-beta.25'))
    {
        $different = Get-DropSpaceReleaseInfo $differentTag
        if ($different.SemanticVersion -eq $info.SemanticVersion) { continue }
        $rejected = $false
        try { Assert-DropSpaceExecutableVersion -Path $path -ReleaseInfo $different -Installer:$entry.Installer }
        catch { $rejected = $_.Exception.Message -like 'Release executable identity/version mismatch:*' }
        if (-not $rejected) { throw "Mislabeled payload was accepted: $($entry.Name)" }
    }
}
Write-Host 'Real PE artifact versions match RELEASE_VERSION; mismatched release identities are rejected.'
