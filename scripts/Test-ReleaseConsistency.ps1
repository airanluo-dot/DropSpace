Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "ReleaseVersion.ps1")
. (Join-Path $PSScriptRoot "ReleaseNotes.ps1")

$releaseInfo = Assert-DropSpaceNewReleaseVersion ((Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw -Encoding UTF8).Trim())
$notesPath = Get-DropSpaceReleaseNotesPath -RepositoryRoot $repositoryRoot -Tag $releaseInfo.Tag
$notes = Get-Content $notesPath -Raw -Encoding UTF8
$firstLine = ($notes -split '\r?\n', 2)[0].Trim()
if ($firstLine -notmatch "^#\s+DropSpace\s+$([regex]::Escape($releaseInfo.Tag))(?:\s|$)")
{
    throw "Release notes must start with a heading for $($releaseInfo.Tag)."
}

$summary = Get-DropSpaceUpdateSummary -RepositoryRoot $repositoryRoot -Tag $releaseInfo.Tag
foreach ($relativePath in @("README.md", "ROADMAP.md"))
{
    $contents = Get-Content (Join-Path $repositoryRoot $relativePath) -Raw -Encoding UTF8
    if ($contents.IndexOf($releaseInfo.Tag, [StringComparison]::Ordinal) -lt 0)
    {
        throw "$relativePath does not mention the current release $($releaseInfo.Tag)."
    }
}

$readme = Get-Content (Join-Path $repositoryRoot "README.md") -Raw -Encoding UTF8
if ($readme -match '(?i)Dynamic Island/Notch|Dynamic Island or Notch|灵动岛\s*/\s*刘海|灵动岛或刘海')
{
    throw "README.md still presents the removed Notch mode as an active product option."
}

$releaseWorkflow = Get-Content (Join-Path $repositoryRoot ".github/workflows/release.yml") -Raw -Encoding UTF8
foreach ($requiredPattern in @(
    "github\.actor == github\.repository_owner",
    "runs-on: windows-2025",
    "RestoreLockedMode=true",
    "Reject an existing tag or release"
))
{
    if ($releaseWorkflow -notmatch $requiredPattern)
    {
        throw "Release workflow is missing the Preview.19 governance requirement: $requiredPattern"
    }
}

$installerScript = Get-Content (Join-Path $repositoryRoot "installer/DropSpace.iss") -Raw -Encoding UTF8
$identityCommands = @($installerScript -split '\r?\n' | Where-Object {
    $_ -match '^Filename:' -and $_ -match 'DropSpace\.Identity\.ps1'
})
if ($identityCommands.Count -ne 2) { throw "The installer must define both identity lifecycle commands." }
$installPathFixture = 'C:\Users\Test Account\App Data\DropSpace'
foreach ($line in $identityCommands)
{
    $parameterMatch = [regex]::Match($line, 'Parameters:\s*"(?<command>(?:""|[^"])*)"(?:;|$)')
    if (-not $parameterMatch.Success -or $line -match '&quot;')
    {
        throw "Identity lifecycle parameters must use Inno quoted-string syntax."
    }
    $command = $parameterMatch.Groups['command'].Value.Replace('""', '"').Replace('{app}', $installPathFixture)
    $arguments = @([regex]::Matches($command, '"[^"]*"|[^\s"]+') | ForEach-Object { $_.Value.Trim('"') })
    $register = $line -match '-Action Register'
    $expected = @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', "$installPathFixture\DropSpace.Identity.ps1", '-Action', $(if ($register) { 'Register' } else { 'Unregister' }))
    if ($register) { $expected += @('-PackagePath', "$installPathFixture\DropSpace.Identity.msix", '-ExternalLocation', $installPathFixture) }
    if (($arguments -join [char]0) -cne ($expected -join [char]0))
    {
        throw "Identity lifecycle command splits paths containing spaces or has unexpected parameters."
    }
}

Write-Host "Release consistency passed for $($releaseInfo.Tag); identity lifecycle command paths remain quoted."
Write-Host "Manifest summary: $summary"
