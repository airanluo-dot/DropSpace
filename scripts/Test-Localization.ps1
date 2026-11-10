param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot ".."),
    [ValidateSet('all','app','website')][string]$Scope = 'all'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$arguments = @((Join-Path $PSScriptRoot 'check-localization.mjs'), '--root', $repositoryRoot)
if ($Scope -ne 'all') { $arguments += @('--scope', $Scope) }
& node @arguments
if ($LASTEXITCODE -ne 0) { throw 'Localization integrity failed; publication is blocked.' }
