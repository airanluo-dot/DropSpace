param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,

    [Parameter(Mandatory = $true)][string]$OutputPath,

    [Parameter(Mandatory = $true)][string]$ConfigurationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Find-MakePri
{
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $candidateRoots = @(
        (Join-Path $env:USERPROFILE ".nuget\\packages\\microsoft.windows.sdk.buildtools"),
        (Join-Path $programFilesX86 "Windows Kits\\10\\bin")
    ) | Where-Object { Test-Path $_ -PathType Container }

    foreach ($root in $candidateRoots)
    {
        $makePri = Get-ChildItem -Path $root -Filter "makepri.exe" -File -Recurse |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]makepri\.exe$' } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
        if ($null -ne $makePri)
        {
            return $makePri.FullName
        }
    }

    throw "MakePri.exe was not found in Microsoft.Windows.SDK.BuildTools or the Windows SDK."
}

$resolvedProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$resolvedConfigurationPath = [System.IO.Path]::GetFullPath($ConfigurationPath)
if (-not (Test-Path $resolvedProjectRoot -PathType Container))
{
    throw "PRI project root does not exist: $resolvedProjectRoot"
}

$sourceStringsPath = Join-Path $resolvedProjectRoot "Strings"
if (-not (Test-Path $sourceStringsPath -PathType Container))
{
    throw "PRI source strings directory does not exist: $sourceStringsPath"
}

$resourceProjectRoot = Join-Path (Split-Path -Parent $resolvedOutputPath) "resources-pri-input"
New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedOutputPath) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedConfigurationPath) -Force | Out-Null
foreach ($path in @($resolvedOutputPath, $resolvedConfigurationPath, $resourceProjectRoot))
{
    if (Test-Path $path)
    {
        Remove-Item -Path $path -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $resourceProjectRoot -Force | Out-Null
Copy-Item -Path $sourceStringsPath -Destination (Join-Path $resourceProjectRoot "Strings") -Recurse -Force

$makePri = Find-MakePri
& $makePri createconfig /cf $resolvedConfigurationPath /dq en-US
if ($LASTEXITCODE -ne 0)
{
    throw "MakePri createconfig failed with exit code $LASTEXITCODE."
}

[xml]$configuration = Get-Content -Path $resolvedConfigurationPath -Raw
$packagingNodes = @($configuration.SelectNodes("//*[translate(local-name(), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = 'packaging']"))
if ($packagingNodes.Count -eq 0)
{
    throw "The generated PRI configuration did not contain a packaging section to remove."
}

foreach ($packagingNode in $packagingNodes)
{
    [void]$packagingNode.ParentNode.RemoveChild($packagingNode)
}

$configuration.Save($resolvedConfigurationPath)
# The source project also contains a package manifest for MSIX. Build the unpackaged PRI from
# the staged strings only so MakePri gives the index the Application root namespace.
& $makePri new /pr $resourceProjectRoot /cf $resolvedConfigurationPath /of $resolvedOutputPath
if ($LASTEXITCODE -ne 0)
{
    throw "MakePri new failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $resolvedOutputPath -PathType Leaf) -or (Get-Item $resolvedOutputPath).Length -eq 0)
{
    throw "MakePri completed without producing a non-empty resource index: $resolvedOutputPath"
}

Write-Host "Generated unpackaged resource index: $resolvedOutputPath"
