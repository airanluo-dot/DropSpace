Set-StrictMode -Version Latest

function Get-DropSpaceReleaseNotesPath
{
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Tag
    )

    return Join-Path $RepositoryRoot ".github/release-notes/$Tag.md"
}

function Get-DropSpaceUpdateSummary
{
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Tag
    )

    $notesPath = Get-DropSpaceReleaseNotesPath -RepositoryRoot $RepositoryRoot -Tag $Tag
    if (-not (Test-Path $notesPath -PathType Leaf))
    {
        throw "Missing release notes for $Tag."
    }

    $notes = Get-Content $notesPath -Raw
    $matches = [regex]::Matches(
        $notes,
        '(?im)^\s*<!--\s*update-summary:\s*(?<summary>[^\r\n]*?)\s*-->\s*$')
    if ($matches.Count -ne 1)
    {
        throw "Release notes for $Tag must contain exactly one single-line '<!-- update-summary: ... -->' marker."
    }

    $summary = $matches[0].Groups['summary'].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($summary) -or $summary.Length -gt 500)
    {
        throw "The update summary for $Tag must contain 1 to 500 characters."
    }

    return $summary
}
