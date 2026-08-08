param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "src/DropSpace.App/DropSpace.App.csproj"
$packageDirectory = Join-Path $repositoryRoot "artifacts/msix"
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

$arguments = @(
    "build",
    $projectPath,
    "-c", $Configuration,
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:UapAppxPackageBuildMode=SideloadOnly",
    "-p:AppxBundle=Never",
    "-p:AppxPackageSigningEnabled=false",
    "-p:PackageCertificateThumbprint=",
    "-p:AppxPackageDir=$packageDirectory\"
)

if ($NoRestore)
{
    $arguments += "--no-restore"
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0)
{
    throw "MSIX package build failed with exit code $LASTEXITCODE."
}

$packages = Get-ChildItem -Path $packageDirectory -Recurse -File |
    Where-Object { $_.Extension -in ".msix", ".appx" }
if ($packages.Count -eq 0)
{
    throw "The build completed without producing an MSIX/AppX package."
}

Write-Host "Unsigned package output: $packageDirectory"
