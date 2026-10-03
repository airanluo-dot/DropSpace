Set-StrictMode -Version Latest

function Get-DropSpaceReleaseInfo
{
    param([Parameter(Mandatory = $true)][string]$Tag)

    $value = $Tag.Trim()
    if ($value -notmatch '^v(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<label>preview|beta)\.(?<prerelease>[1-9][0-9]*))?$')
    {
        throw "Unsupported DropSpace release version: $Tag"
    }

    $major = [int]$Matches.major
    $minor = [int]$Matches.minor
    $patch = [int]$Matches.patch
    $isPrerelease = $Matches.ContainsKey("prerelease") -and -not [string]::IsNullOrWhiteSpace([string]$Matches.prerelease)
    $prerelease = if ($isPrerelease) { [int]$Matches.prerelease } else { 9999 }
    if ($major -gt 20 -or $minor -gt 99 -or $patch -gt 99 -or ($isPrerelease -and $prerelease -gt 9998))
    {
        throw "Release version exceeds the shared VersionCode/package-version range: $Tag"
    }

    $semanticVersion = $value.Substring(1)
    [PSCustomObject]@{
        PrereleaseLabel = if ($isPrerelease) { $Matches.label } else { $null }
        Tag = $value
        SemanticVersion = $semanticVersion
        Major = $major
        Minor = $minor
        Patch = $patch
        PrereleaseNumber = if ($isPrerelease) { $prerelease } else { $null }
        IsPrerelease = $isPrerelease
        Channel = if ($isPrerelease) { "beta" } else { "stable" }
        FileVersion = if ($isPrerelease) { "$major.$minor.$patch.$prerelease" } else { "$major.$minor.$patch.0" }
        PackageVersion = "$major.$minor.$patch.$prerelease"
        VersionCode = ($major * 100000000) + ($minor * 1000000) + ($patch * 10000) + $prerelease
        GitHubPrerelease = $isPrerelease
        MakeLatest = -not $isPrerelease
    }
}

function Get-DropSpaceLifecycleBaselineVersion
{
    param(
        [Parameter(Mandatory = $true)]$ReleaseInfo,
        [string]$NotesRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) ".github/release-notes")
    )

    if ($ReleaseInfo.IsPrerelease -and $ReleaseInfo.PrereleaseNumber -gt 1)
    {
        if ($ReleaseInfo.Tag -eq "v0.3.0-beta.24") { return "0.3.0-preview.23" }
        return "$($ReleaseInfo.Major).$($ReleaseInfo.Minor).$($ReleaseInfo.Patch)-$($ReleaseInfo.PrereleaseLabel).$($ReleaseInfo.PrereleaseNumber - 1)"
    }

    if (-not $ReleaseInfo.IsPrerelease)
    {
        $prefix = "v$($ReleaseInfo.Major).$($ReleaseInfo.Minor).$($ReleaseInfo.Patch)-"
        $latestPrerelease = Get-ChildItem $notesRoot -File -Filter "$prefix*.md" |
            ForEach-Object {
                if ($_.BaseName -match "^$([regex]::Escape($prefix))(?:preview|beta)\.(?<number>[1-9][0-9]*)$")
                {
                    [PSCustomObject]@{ Number = [int]$Matches.number; Version = $_.BaseName.Substring(1) }
                }
            } |
            Sort-Object Number -Descending |
            Select-Object -First 1
        if ($null -eq $latestPrerelease)
        {
            throw "Stable release $($ReleaseInfo.Tag) has no same-line prerelease lifecycle baseline."
        }
        return $latestPrerelease.Version
    }

    # A first prerelease may follow a prerelease-only series. Select a real,
    # documented predecessor instead of inventing an unpublished stable version.
    $previous = Get-ChildItem $NotesRoot -File -Filter 'v*.md' |
        ForEach-Object {
            try { Get-DropSpaceReleaseInfo $_.BaseName } catch { }
        } |
        Where-Object { $_.VersionCode -lt $ReleaseInfo.VersionCode } |
        Sort-Object VersionCode -Descending |
        Select-Object -First 1
    if ($null -ne $previous) { return $previous.SemanticVersion }

    throw "Release $($ReleaseInfo.Tag) has no representable lower lifecycle baseline."
}

function Assert-DropSpaceNewReleaseVersion
{
    param([Parameter(Mandatory = $true)][string]$Tag)
    $info = Get-DropSpaceReleaseInfo $Tag
    if ($info.IsPrerelease -and $info.PrereleaseLabel -ne "beta") {
        throw "New prereleases must use -beta.N; historical Preview releases are read-only."
    }
    return $info
}


function Assert-DropSpacePublicationCommit {
    param([string]$ExpectedCommit, [string]$ActualCommit)
    if ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$' -or $ExpectedCommit -cne $ActualCommit) {
        throw 'Release publication requires the exact reviewed commit; main may have advanced since dispatch.'
    }
}
