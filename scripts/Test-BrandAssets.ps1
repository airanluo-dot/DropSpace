param(
    [string]$PortableExecutable = "artifacts/release/DropSpace.exe",
    [string]$InstallerExecutable = "artifacts/installer/DropSpaceSetup.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$iconPath = Join-Path $repositoryRoot "src/DropSpace.App/Assets/AppIcon.ico"
if (-not (Test-Path $iconPath -PathType Leaf))
{
    throw "Canonical application icon is missing: $iconPath"
}

$stream = [System.IO.File]::OpenRead($iconPath)
try
{
    $reader = [System.IO.BinaryReader]::new($stream)
    if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1)
    {
        throw "AppIcon.ico has an invalid ICO header."
    }

    $count = $reader.ReadUInt16()
    $sizes = for ($index = 0; $index -lt $count; $index++)
    {
        $width = [int]$reader.ReadByte()
        $height = [int]$reader.ReadByte()
        $null = $reader.ReadBytes(14)
        if ($width -eq 0) { $width = 256 }
        if ($height -eq 0) { $height = 256 }
        if ($width -ne $height) { throw "AppIcon.ico contains a non-square frame." }
        $width
    }
}
finally
{
    $stream.Dispose()
}

$requiredSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
if ($sizes.Count -ne $requiredSizes.Count)
{
    throw "AppIcon.ico must contain exactly the approved nine optical frames; found $($sizes.Count)."
}
foreach ($required in $requiredSizes)
{
    if ($sizes -notcontains $required)
    {
        throw "AppIcon.ico is missing the required $required x $required frame."
    }
}

$references = @{
    "src/DropSpace.App/DropSpace.rc" = '101 ICON "Assets\\AppIcon.ico"'
    "src/DropSpace.App/DropSpace.App.csproj" = 'Assets\AppIcon.ico'
    "installer/DropSpace.iss" = 'SetupIconFile=..\src\DropSpace.App\Assets\AppIcon.ico'
    "src/DropSpace.App/MainWindow.xaml.cs" = 'NativeApplicationIcon.ApplyToWindow'
    "scripts/Generate-BrandAssets.ps1" = 'DropSpace-AppIcon-Master-2048.png'
}
foreach ($entry in $references.GetEnumerator())
{
    $content = Get-Content (Join-Path $repositoryRoot $entry.Key) -Raw
    if (-not $content.Contains($entry.Value, [StringComparison]::Ordinal))
    {
        throw "Brand asset reference is missing from $($entry.Key): $($entry.Value)"
    }
}

foreach ($asset in @(
    "branding/master/DropSpace-AppIcon-Master-2048.png",
    "branding/master/DropSpace-MiniMark-Purple-1024.png",
    "branding/generated/docs/DropSpace-Lockup-Horizontal-Black.png",
    "branding/generated/docs/DropSpace-Lockup-Horizontal-White.png",
    "src/DropSpace.App/Assets/StoreLogo.png"))
{
    if (-not (Test-Path (Join-Path $repositoryRoot $asset) -PathType Leaf))
    {
        throw "Brand source/output is missing: $asset"
    }
}

foreach ($scale in @(100, 125, 150, 200, 400))
{
    foreach ($name in @("Square44x44Logo", "Square150x150Logo", "StoreLogo", "Wide310x150Logo", "SplashScreen"))
    {
        $asset = Join-Path $repositoryRoot "src/DropSpace.App/Assets/$name.scale-$scale.png"
        if (-not (Test-Path $asset -PathType Leaf)) { throw "MSIX brand asset is missing: $asset" }
    }
}

$legacyReferences = Get-ChildItem $repositoryRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '[\\/]\.git[\\/]' -and
        $_.FullName -notmatch '[\\/]artifacts[\\/]' -and
        $_.FullName -ne $PSCommandPath
    } |
    Select-String -SimpleMatch "targetsize-48_altform-lightunplated" -ErrorAction SilentlyContinue
if ($legacyReferences)
{
    throw "The retired light-unplated icon is still referenced by the build."
}

function Assert-EmbeddedIcon
{
    param([Parameter(Mandatory = $true)][string]$Executable)

    $resolved = if ([System.IO.Path]::IsPathRooted($Executable))
    {
        $Executable
    }
    else
    {
        Join-Path $repositoryRoot $Executable
    }
    if (-not (Test-Path $resolved -PathType Leaf))
    {
        throw "Icon verification executable is missing: $resolved"
    }

    Add-Type -AssemblyName System.Drawing.Common
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($resolved)
    if ($null -eq $icon -or $icon.Width -lt 16 -or $icon.Height -lt 16)
    {
        throw "$resolved does not expose a usable embedded application icon."
    }
    $icon.Dispose()
}

Assert-EmbeddedIcon $PortableExecutable
Assert-EmbeddedIcon $InstallerExecutable
Write-Host "Brand assets passed: official Mini Mark at 16/20/24/32, full 3D icon at 40/48/64/128/256, PE resource 101, WinUI, tray, installer, and MSIX references."
