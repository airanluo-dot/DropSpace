param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$source = [System.IO.Path]::GetFullPath($SourcePath)
$output = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path $source -PathType Leaf))
{
    throw "Win32 resource source does not exist: $source"
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
Write-Host "Win32 resource compiler: $($resourceCompiler.FullName)"
& $resourceCompiler.FullName /nologo /fo $output $source
if ($LASTEXITCODE -ne 0)
{
    throw "rc.exe failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $output -PathType Leaf))
{
    throw "rc.exe completed without producing $output."
}
