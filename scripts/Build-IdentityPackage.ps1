param(
    [string]$OutputDirectory = "artifacts/identity",

    [string]$MakeAppx = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$releaseTag = (Get-Content (Join-Path $repositoryRoot "RELEASE_VERSION") -Raw).Trim()
if ($releaseTag -notmatch '^v(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)-preview\.(?<preview>[0-9]+)$')
{
    throw "RELEASE_VERSION is not a supported preview version: $releaseTag"
}

$packageVersion = "$($Matches.major).$($Matches.minor).$($Matches.patch).$($Matches.preview)"
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory))
{
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else
{
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}

if ([string]::IsNullOrWhiteSpace($MakeAppx))
{
    $command = Get-Command MakeAppx.exe -ErrorAction SilentlyContinue
    if ($null -ne $command)
    {
        $MakeAppx = $command.Source
    }
    else
    {
        $kits = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending
        $MakeAppx = $kits |
            ForEach-Object { Join-Path $_.FullName "x64\makeappx.exe" } |
            Where-Object { Test-Path $_ -PathType Leaf } |
            Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($MakeAppx) -or -not (Test-Path $MakeAppx -PathType Leaf))
{
    throw "MakeAppx.exe was not found in the installed Windows SDK."
}

$stage = Join-Path $outputPath "stage"
if (Test-Path $stage)
{
    Remove-Item $stage -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $stage "Assets") -Force | Out-Null

$template = Get-Content (Join-Path $repositoryRoot "identity/AppxManifest.xml.template") -Raw
$manifest = $template.Replace("__PACKAGE_VERSION__", $packageVersion)
$manifestPath = Join-Path $stage "AppxManifest.xml"
$manifest | Set-Content $manifestPath -Encoding utf8NoBOM

$assetRoot = Join-Path $repositoryRoot "src/DropSpace.App/Assets"
foreach ($asset in @(
    "StoreLogo.png",
    "Square44x44Logo.scale-200.png",
    "Square150x150Logo.scale-200.png"))
{
    $source = Join-Path $assetRoot $asset
    if (-not (Test-Path $source -PathType Leaf))
    {
        throw "Identity package asset is missing: $source"
    }
    Copy-Item $source (Join-Path $stage "Assets/$asset") -Force
}

$packagePath = Join-Path $outputPath "DropSpace.Identity.msix"
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
if (Test-Path $packagePath)
{
    Remove-Item $packagePath -Force
}

& $MakeAppx pack /d $stage /p $packagePath /nv /o
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $packagePath -PathType Leaf))
{
    throw "MakeAppx failed to build the external-location identity package."
}

[xml]$xml = Get-Content $manifestPath -Raw
$identity = $xml.Package.Identity
if ($identity.Name -ne "AiranLuo.DropSpace.Identity" -or
    $identity.Publisher -ne "CN=airanluo-dot" -or
    $identity.Version -ne $packageVersion)
{
    throw "The generated identity package manifest does not have the stable DropSpace identity."
}

Write-Host "Identity package: $packagePath"
Write-Host "Identity: AiranLuo.DropSpace.Identity / CN=airanluo-dot / DropSpace"
Write-Host "Version: $packageVersion"
Write-Host "Signed: false (CI artifact only until Artifact Signing is configured)"
Write-Host "Bytes: $((Get-Item $packagePath).Length)"
Write-Host "SHA256: $((Get-FileHash $packagePath -Algorithm SHA256).Hash.ToLowerInvariant())"
