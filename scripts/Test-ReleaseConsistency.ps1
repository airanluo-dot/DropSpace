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

# Also check current prose against the canonical package and release metadata.
& node (Join-Path $PSScriptRoot "test-release-metadata.mjs") $repositoryRoot
if ($LASTEXITCODE -ne 0) { throw "Release metadata consistency failed." }
& node --test (Join-Path $PSScriptRoot "test-release-metadata.test.mjs")
if ($LASTEXITCODE -ne 0) { throw "Release metadata regression tests failed." }

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

# Start-Process joins ArgumentList into one native command line. Exercise the
# actual install command's argument expression with a spaced directory so the
# compiler bootstrap keeps the /DIR value intact on a spaced checkout path.
$tokens = $null
$parseErrors = $null
$innoAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repositoryRoot "scripts/Install-InnoSetup.ps1"), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw "The Inno bootstrap script has parser errors." }
$installCommand = $innoAst.Find({ param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq "Start-Process"
}, $true)
if ($null -eq $installCommand) { throw "The Inno bootstrap install command is missing." }
$argumentIndex = -1
for ($index = 0; $index -lt $installCommand.CommandElements.Count; $index++) {
    $element = $installCommand.CommandElements[$index]
    if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and
        $element.ParameterName -eq "ArgumentList") { $argumentIndex = $index + 1; break }
}
if ($argumentIndex -lt 0 -or $argumentIndex -ge $installCommand.CommandElements.Count) {
    throw "The Inno bootstrap argument list is missing."
}
$resolvedInstallDirectory = $installPathFixture
$installerArguments = @(& ([scriptblock]::Create($installCommand.CommandElements[$argumentIndex].Extent.Text)))
$nativeArguments = @([regex]::Matches(($installerArguments -join ' '), '"[^"]*"|[^\s"]+') |
    ForEach-Object { $_.Value.Trim('"') })
$expectedArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=$installPathFixture")
if (($nativeArguments -join [char]0) -cne ($expectedArguments -join [char]0)) {
    throw "The Inno bootstrap splits a directory path containing spaces."
}

Write-Host "Release consistency passed for $($releaseInfo.Tag); identity lifecycle and Inno bootstrap command paths remain quoted."
Write-Host "Manifest summary: $summary"
