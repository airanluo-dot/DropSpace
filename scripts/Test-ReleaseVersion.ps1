Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")

function Assert-Equal
{
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Actual -ne $Expected)
    {
        throw "$Label mismatch. Expected '$Expected', got '$Actual'."
    }
}

$stable = Get-DropSpaceReleaseInfo "v0.1.0"
Assert-Equal $stable.SemanticVersion "0.1.0" "Stable semantic version"
Assert-Equal $stable.Channel "stable" "Stable channel"
Assert-Equal $stable.FileVersion "0.1.0.0" "Stable file version"
Assert-Equal $stable.PackageVersion "0.1.0.9999" "Stable package version"
Assert-Equal $stable.VersionCode 1009999 "Stable version code"
Assert-Equal $stable.GitHubPrerelease $false "Stable prerelease flag"
Assert-Equal $stable.MakeLatest $true "Stable latest flag"

$preview = Get-DropSpaceReleaseInfo "v0.1.1-preview.10"
Assert-Equal $preview.SemanticVersion "0.1.1-preview.10" "Preview semantic version"
Assert-Equal $preview.Channel "preview" "Preview channel"
Assert-Equal $preview.FileVersion "0.1.1.10" "Preview file version"
Assert-Equal $preview.PackageVersion "0.1.1.10" "Preview package version"
Assert-Equal $preview.VersionCode 1010010 "Preview version code"
Assert-Equal $preview.GitHubPrerelease $true "Preview prerelease flag"
Assert-Equal $preview.MakeLatest $false "Preview latest flag"

$smartDragPreview = Get-DropSpaceReleaseInfo "v0.2.0-preview.1"
Assert-Equal $smartDragPreview.SemanticVersion "0.2.0-preview.1" "Smart-drag Preview semantic version"
Assert-Equal $smartDragPreview.Channel "preview" "Smart-drag Preview channel"
Assert-Equal $smartDragPreview.FileVersion "0.2.0.1" "Smart-drag Preview file version"
Assert-Equal $smartDragPreview.PackageVersion "0.2.0.1" "Smart-drag Preview package version"
Assert-Equal $smartDragPreview.GitHubPrerelease $true "Smart-drag Preview prerelease flag"
Assert-Equal $smartDragPreview.MakeLatest $false "Smart-drag Preview latest flag"
Assert-Equal (Get-DropSpaceLifecycleBaselineVersion $smartDragPreview) "0.1.0" "First Preview lifecycle baseline"
$smartDragHotfix = Get-DropSpaceReleaseInfo "v0.2.0-preview.2"
Assert-Equal $smartDragHotfix.SemanticVersion "0.2.0-preview.2" "Smart-drag hotfix semantic version"
Assert-Equal $smartDragHotfix.GitHubPrerelease $true "Smart-drag hotfix prerelease flag"
Assert-Equal (Get-DropSpaceLifecycleBaselineVersion $smartDragHotfix) "0.2.0-preview.1" "Later Preview lifecycle baseline"
$smartDragSignalHotfix = Get-DropSpaceReleaseInfo "v0.2.0-preview.3"
Assert-Equal $smartDragSignalHotfix.SemanticVersion "0.2.0-preview.3" "Smart-drag signal hotfix semantic version"
Assert-Equal $smartDragSignalHotfix.GitHubPrerelease $true "Smart-drag signal hotfix prerelease flag"
Assert-Equal (Get-DropSpaceLifecycleBaselineVersion $smartDragSignalHotfix) "0.2.0-preview.2" "Signal hotfix lifecycle baseline"
$singleIslandPreview = Get-DropSpaceReleaseInfo "v0.2.0-preview.4"
Assert-Equal $singleIslandPreview.SemanticVersion "0.2.0-preview.4" "Single-Island Preview semantic version"
Assert-Equal $singleIslandPreview.FileVersion "0.2.0.4" "Single-Island Preview file version"
Assert-Equal $singleIslandPreview.PackageVersion "0.2.0.4" "Single-Island Preview package version"
Assert-Equal $singleIslandPreview.GitHubPrerelease $true "Single-Island Preview prerelease flag"
Assert-Equal (Get-DropSpaceLifecycleBaselineVersion $singleIslandPreview) "0.2.0-preview.3" "Single-Island Preview lifecycle baseline"
Assert-Equal (Get-DropSpaceLifecycleBaselineVersion $stable) "0.1.0-preview.5" "Stable lifecycle baseline"

foreach ($invalid in @("0.1.0", "v0.1", "v0.1.0-rc.1", "v0.1.0-preview.0", "v0.1.0-preview.9999"))
{
    try
    {
        $null = Get-DropSpaceReleaseInfo $invalid
        throw "Invalid release version was accepted: $invalid"
    }
    catch
    {
        if ($_.Exception.Message -like "Invalid release version was accepted:*")
        {
            throw
        }
    }
}

Write-Host "Stable/Preview release metadata and shared VersionCode rules passed."
