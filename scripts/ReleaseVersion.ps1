Set-StrictMode -Version Latest

function Get-DropSpaceReleaseInfo
{
    param([Parameter(Mandatory = $true)][string]$Tag)

    $value = $Tag.Trim()
    if ($value -notmatch '^v(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-preview\.(?<preview>[1-9][0-9]*))?$')
    {
        throw "Unsupported DropSpace release version: $Tag"
    }

    $major = [int]$Matches.major
    $minor = [int]$Matches.minor
    $patch = [int]$Matches.patch
    $isPreview = $Matches.ContainsKey("preview") -and -not [string]::IsNullOrWhiteSpace([string]$Matches.preview)
    $preview = if ($isPreview) { [int]$Matches.preview } else { 9999 }
    if ($major -gt 20 -or $minor -gt 99 -or $patch -gt 99 -or ($isPreview -and $preview -gt 9998))
    {
        throw "Release version exceeds the shared VersionCode/package-version range: $Tag"
    }

    $semanticVersion = $value.Substring(1)
    [PSCustomObject]@{
        Tag = $value
        SemanticVersion = $semanticVersion
        Major = $major
        Minor = $minor
        Patch = $patch
        PreviewNumber = if ($isPreview) { $preview } else { $null }
        IsPreview = $isPreview
        Channel = if ($isPreview) { "preview" } else { "stable" }
        FileVersion = if ($isPreview) { "$major.$minor.$patch.$preview" } else { "$major.$minor.$patch.0" }
        PackageVersion = "$major.$minor.$patch.$preview"
        VersionCode = ($major * 100000000) + ($minor * 1000000) + ($patch * 10000) + $preview
        GitHubPrerelease = $isPreview
        MakeLatest = -not $isPreview
    }
}

function Get-DropSpaceLifecycleBaselineVersion
{
    param([Parameter(Mandatory = $true)]$ReleaseInfo)

    if ($ReleaseInfo.IsPreview -and $ReleaseInfo.PreviewNumber -gt 1)
    {
        return "$($ReleaseInfo.Major).$($ReleaseInfo.Minor).$($ReleaseInfo.Patch)-preview.$($ReleaseInfo.PreviewNumber - 1)"
    }

    if (-not $ReleaseInfo.IsPreview)
    {
        $notesRoot = Join-Path (Split-Path $PSScriptRoot -Parent) ".github/release-notes"
        $prefix = "v$($ReleaseInfo.Major).$($ReleaseInfo.Minor).$($ReleaseInfo.Patch)-preview."
        $latestPreview = Get-ChildItem $notesRoot -File -Filter "$prefix*.md" |
            ForEach-Object {
                if ($_.BaseName -match "^$([regex]::Escape($prefix))(?<number>[1-9][0-9]*)$")
                {
                    [PSCustomObject]@{ Number = [int]$Matches.number; Version = $_.BaseName.Substring(1) }
                }
            } |
            Sort-Object Number -Descending |
            Select-Object -First 1
        if ($null -eq $latestPreview)
        {
            throw "Stable release $($ReleaseInfo.Tag) has no same-line Preview lifecycle baseline."
        }
        return $latestPreview.Version
    }

    # A first Preview has no same-line predecessor. Use the nearest lower
    # stable release so the lifecycle fixture exercises a real upgrade and
    # never trips the installer's downgrade guard (for example 0.1.0 ->
    # 0.2.0-preview.1).
    if ($ReleaseInfo.Patch -gt 0)
    {
        return "$($ReleaseInfo.Major).$($ReleaseInfo.Minor).$($ReleaseInfo.Patch - 1)"
    }
    if ($ReleaseInfo.Minor -gt 0)
    {
        return "$($ReleaseInfo.Major).$($ReleaseInfo.Minor - 1).0"
    }
    if ($ReleaseInfo.Major -gt 0)
    {
        return "$($ReleaseInfo.Major - 1).0.0"
    }

    throw "Release $($ReleaseInfo.Tag) has no representable lower lifecycle baseline."
}
