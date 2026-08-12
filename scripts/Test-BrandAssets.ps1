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
    throw "Generated application icon is missing: $iconPath"
}

$sourceHashes = [ordered]@{
    "branding/master/transparent/DropSpace-Logo-Transparent-Final.png" = "25330466ffe4b593dbe33e4c28327833210d676418a3a61d68c1dd403e8ae8e0"
    "branding/master/black/DropSpace-Black-Main-Original.png" = "519664732432643022331a7c9b1c57dcedac361f09500691512738aef5bb73bf"
    "branding/master/black/DropSpace-Black-Main-Resampled-2048.png" = "319470a340b5402537ff4a6dc7c4c80fe41d1e4a08cf850d08bf3eb2f6e393ac"
    "branding/master/black/DropSpace-Black-Main-Resampled-4096.png" = "431710f7e7447a6bda4a861ec64667aef366f5c5fffca4fd681bbe964321a8f6"
    "branding/master/white/DropSpace-White-Backup-Original.png" = "d463f19661b2eb4e9a86f261f3a6363385b0936cb9947d0c5b80a71b36e221b2"
    "branding/master/white/DropSpace-White-Backup-Resampled-2048.png" = "6ee681eeb56f0bd889bd4665d3d0f16b4d5177ca254f4225d92417017110b156"
    "branding/master/white/DropSpace-White-Backup-Resampled-4096.png" = "d6f95ca369fdb05851d224f6614a9ff73d6ebc3e8622bcb96b92859116f3f0ae"
    "branding/master/说明.txt" = "5bef96f8f352c0c99843ed0c15f0ca56eced2ed155adf9c82b5bcfaa39e206e0"
}
foreach ($entry in $sourceHashes.GetEnumerator())
{
    $path = Join-Path $repositoryRoot $entry.Key
    if (-not (Test-Path $path -PathType Leaf))
    {
        throw "Canonical brand package file is missing: $($entry.Key)"
    }

    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Value)
    {
        throw "Canonical brand package file changed unexpectedly: $($entry.Key)"
    }
}

$sourceManifestPath = Join-Path $repositoryRoot "branding/SOURCE_MANIFEST.json"
$sourceManifest = Get-Content $sourceManifestPath -Raw | ConvertFrom-Json
if ($sourceManifest.schemaVersion -ne 2 -or
    $sourceManifest.authoritativeRuntimeVariant -ne "Transparent Final" -or
    $sourceManifest.runtimeBackground -ne "transparent" -or
    $sourceManifest.activeSource.path -ne "master/transparent/DropSpace-Logo-Transparent-Final.png" -or
    $sourceManifest.activeSource.sha256 -ne $sourceHashes["branding/master/transparent/DropSpace-Logo-Transparent-Final.png"])
{
    throw "Brand source manifest does not enforce the transparent Final runtime policy."
}

$generator = Get-Content (Join-Path $repositoryRoot "scripts/Generate-BrandAssets.ps1") -Raw
foreach ($requiredText in @(
    'transparent/DropSpace-Logo-Transparent-Final.png',
    'Write-RenderedPng $runtimeMain',
    'DropSpace-Logo-Transparent.png',
    'WebsiteAssetRoot'))
{
    if (-not $generator.Contains($requiredText, [StringComparison]::Ordinal))
    {
        throw "Brand generator is missing the transparent Final invariant: $requiredText"
    }
}
foreach ($forbiddenSelection in @('Read-Bitmap $blackLegacySource', 'Read-Bitmap $whiteBackupSource'))
{
    if ($generator.Contains($forbiddenSelection, [StringComparison]::Ordinal))
    {
        throw "Legacy artwork must remain retained but inactive: $forbiddenSelection"
    }
}

$legacyExports = @(
    "branding/generated/docs/DropSpace-Logo-Black.png",
    "branding/legacy/runtime-black-v1/website/dropspace-logo.png",
    "branding/legacy/runtime-black-v1/website/favicon.png",
    "branding/legacy/runtime-black-v1/website/og-image.png"
)
foreach ($legacyExport in $legacyExports)
{
    if (-not (Test-Path (Join-Path $repositoryRoot $legacyExport) -PathType Leaf))
    {
        throw "Inactive legacy brand export is missing: $legacyExport"
    }
}

$activeWindowsRoot = Join-Path $repositoryRoot "src/DropSpace.App/Assets"
$legacyWindowsRoot = Join-Path $repositoryRoot "branding/legacy/runtime-black-v1/windows"
$activeWindowsNames = @(Get-ChildItem $activeWindowsRoot -File | ForEach-Object Name | Sort-Object)
$legacyWindowsNames = @(Get-ChildItem $legacyWindowsRoot -File | ForEach-Object Name | Sort-Object)
if ($null -ne (Compare-Object $activeWindowsNames $legacyWindowsNames))
{
    throw "The complete previous Windows brand export set must remain archived and inactive."
}
foreach ($name in $activeWindowsNames)
{
    if ((Get-FileHash (Join-Path $activeWindowsRoot $name) -Algorithm SHA256).Hash -eq
        (Get-FileHash (Join-Path $legacyWindowsRoot $name) -Algorithm SHA256).Hash)
    {
        throw "An active Windows brand output still contains its archived predecessor: $name"
    }
}

foreach ($pair in @(
    @("website/_source/src/assets/dropspace-logo.png", "branding/legacy/runtime-black-v1/website/dropspace-logo.png"),
    @("website/_source/src/assets/favicon.png", "branding/legacy/runtime-black-v1/website/favicon.png"),
    @("website/_source/src/assets/og-image.png", "branding/legacy/runtime-black-v1/website/og-image.png")
))
{
    $active = Join-Path $repositoryRoot $pair[0]
    $legacy = Join-Path $repositoryRoot $pair[1]
    if ((Get-FileHash $active -Algorithm SHA256).Hash -eq (Get-FileHash $legacy -Algorithm SHA256).Hash)
    {
        throw "An active brand output still contains its archived predecessor: $($pair[0])"
    }
}

Add-Type -AssemblyName System.Drawing.Common
$runtimeMasterPath = Join-Path $repositoryRoot "branding/master/transparent/DropSpace-Logo-Transparent-Final.png"
$runtimeMaster = [System.Drawing.Bitmap]::new($runtimeMasterPath)
try
{
    if ($runtimeMaster.Width -ne 1254 -or $runtimeMaster.Height -ne 1254 -or
        $runtimeMaster.GetPixel(0, 0).A -ne 0 -or $runtimeMaster.GetPixel(600, 500).A -eq 0)
    {
        throw "Transparent Final must remain the supplied 1254 x 1254 true-alpha artwork."
    }
}
finally
{
    $runtimeMaster.Dispose()
}

foreach ($retired in @(
    "branding/master/DropSpace-AppIcon-Master-2048.png",
    "branding/master/DropSpace-MiniMark-Purple-1024.png",
    "branding/master/DropSpace-AppIcon-Flat-Vector.svg",
    "src/DropSpace.App/Assets/AppIcon.svg"))
{
    if (Test-Path (Join-Path $repositoryRoot $retired))
    {
        throw "A retired brand asset remains in the build tree: $retired"
    }
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
    throw "AppIcon.ico must contain exactly nine transparent Final frames; found $($sizes.Count)."
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
    "scripts/Generate-BrandAssets.ps1" = 'transparent/DropSpace-Logo-Transparent-Final.png'
    "README.md" = 'branding/generated/docs/DropSpace-Logo-Transparent.png'
    "website/_source/src/index.html" = './assets/dropspace-logo.png'
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
    "branding/generated/docs/DropSpace-Logo-Transparent.png",
    "src/DropSpace.App/Assets/StoreLogo.png"))
{
    if (-not (Test-Path (Join-Path $repositoryRoot $asset) -PathType Leaf))
    {
        throw "Generated brand output is missing: $asset"
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

function Assert-EmbeddedIcon
{
    param([Parameter(Mandatory = $true)][string]$Executable)

    $resolved = if ([System.IO.Path]::IsPathRooted($Executable)) { $Executable } else { Join-Path $repositoryRoot $Executable }
    if (-not (Test-Path $resolved -PathType Leaf))
    {
        throw "Icon verification executable is missing: $resolved"
    }

    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($resolved)
    if ($null -eq $icon -or $icon.Width -lt 16 -or $icon.Height -lt 16)
    {
        throw "$resolved does not expose a usable embedded application icon."
    }
    $icon.Dispose()
}

Assert-EmbeddedIcon $PortableExecutable
Assert-EmbeddedIcon $InstallerExecutable
Write-Host "Brand assets passed: immutable transparent Final source, inactive Black/White legacy sources and exports, nine-frame alpha ICO, PE resource 101, WinUI, tray, installer, README, website, and MSIX references."
