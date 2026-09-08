param(
    [string]$ArtifactRoot = "artifacts/msix"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$buildScript = Get-Content (Join-Path $repositoryRoot "scripts/Build-UnsignedPackage.ps1") -Raw
$policy = Get-Content (Join-Path $repositoryRoot "docs/release/msix-symbol-policy.md") -Raw

if ($buildScript -notmatch '(?m)-p:AppxPackageIncludePrivateSymbols=false' -or
    $buildScript -notmatch '(?m)-p:AppxPackageIncludePublicSymbols=false') {
    throw "The unsigned MSIX build does not declare the Preview.20 no-symbol policy."
}

if ($policy -notmatch '(?im)mspdbcmf\.exe' -or $policy -notmatch '(?im)no-symbol') {
    throw "The MSIX symbol policy document is incomplete."
}

$resolvedArtifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ArtifactRoot))
if (Test-Path $resolvedArtifactRoot -PathType Container) {
    $symbolFiles = @(Get-ChildItem $resolvedArtifactRoot -Recurse -File -Include *.appxsym,*.appxsym.zip)
    if ($symbolFiles.Count -gt 0) {
        throw "The Preview.20 no-symbol package contains unexpected symbol artifacts: $($symbolFiles.FullName -join ', ')"
    }
}

Write-Host "MSIX symbol policy passed: Preview.20 intentionally publishes no .appxsym artifact."
