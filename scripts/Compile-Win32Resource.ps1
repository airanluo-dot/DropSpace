param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$source = [System.IO.Path]::GetFullPath($SourcePath)
$manifest = [System.IO.Path]::GetFullPath($ManifestPath)
$output = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path $source -PathType Leaf))
{
    throw "Win32 resource source does not exist: $source"
}
if (-not (Test-Path $manifest -PathType Leaf))
{
    throw "Application manifest does not exist: $manifest"
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$roots = @(
    (Join-Path $programFilesX86 "Windows Kits/10/bin"),
    (Join-Path $env:USERPROFILE ".nuget/packages/microsoft.windows.sdk.buildtools")
) | Where-Object { Test-Path $_ -PathType Container }

$resourceCompiler = $roots |
    ForEach-Object { Get-ChildItem -Path $_ -Filter rc.exe -File -Recurse -ErrorAction SilentlyContinue } |
    Where-Object { $_.Directory.Name -eq "x64" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $resourceCompiler)
{
    throw "Windows SDK resource compiler rc.exe was not found."
}

$outputDirectory = [System.IO.Path]::GetDirectoryName($output)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$generatedSource = "$output.rc"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetDirectoryName($source)) "../.."))
$releaseTag = ([System.IO.File]::ReadAllText((Join-Path $repositoryRoot "RELEASE_VERSION"))).Trim()
if ($releaseTag -notmatch '^v(?<prefix>[0-9]+\.[0-9]+\.[0-9]+)-preview\.(?<preview>[0-9]+)$')
{
    throw "RELEASE_VERSION is not a supported preview version: $releaseTag"
}
$productVersion = $releaseTag.Substring(1)
$fileVersion = "$($Matches.prefix).$($Matches.preview)"
$fileVersionCommas = $fileVersion.Replace('.', ',')
$backslash = [string][char]92
$escapedManifest = $manifest.Replace($backslash, $backslash + $backslash).Replace('"', '\"')
$resourceTemplate = [System.IO.File]::ReadAllText($source)
$resourceSource = $resourceTemplate.Replace('@FILE_VERSION_COMMAS@', $fileVersionCommas)
$resourceSource = $resourceSource.Replace('@FILE_VERSION@', $fileVersion)
$resourceSource = $resourceSource.Replace('@PRODUCT_VERSION@', $productVersion) +
    [Environment]::NewLine +
    "1 24 `"$escapedManifest`"" +
    [Environment]::NewLine
[System.IO.File]::WriteAllText($generatedSource, $resourceSource, [System.Text.UTF8Encoding]::new($false))
Write-Host "Win32 resource compiler: $($resourceCompiler.FullName)"
Write-Host "Embedded application manifest: $manifest"
& $resourceCompiler.FullName /nologo /fo $output $generatedSource
if ($LASTEXITCODE -ne 0)
{
    throw "rc.exe failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $output -PathType Leaf))
{
    throw "rc.exe completed without producing $output."
}
