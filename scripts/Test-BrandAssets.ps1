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
if ($sourceManifest.schemaVersion -ne 1 -or
    $sourceManifest.authoritativeRuntimeVariant -ne "Black Main" -or
    $sourceManifest.runtimeBackground -ne "black" -or
    $sourceManifest.transparentArtworkExtractionAllowed -ne $false)
{
    throw "Brand source manifest does not enforce the Black Main runtime policy."
}

$generator = Get-Content (Join-Path $repositoryRoot "scripts/Generate-BrandAssets.ps1") -Raw
foreach ($requiredText in @(
    'black/DropSpace-Black-Main-Original.png',
    'Write-RenderedPng $blackMain',
    '[System.Windows.Media.Brushes]::Black'))
{
    if (-not $generator.Contains($requiredText, [StringComparison]::Ordinal))
    {
        throw "Brand generator is missing the Black Main invariant: $requiredText"
    }
}
if ($generator.Contains('Read-Bitmap $whiteBackupSource', [StringComparison]::Ordinal))
{
    throw "White Backup must be retained as a source but cannot be selected by the default runtime generator."
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
    throw "AppIcon.ico must contain exactly nine Black Main frames; found $($sizes.Count)."
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
    "scripts/Generate-BrandAssets.ps1" = 'black/DropSpace-Black-Main-Original.png'
    "README.md" = 'branding/generated/docs/DropSpace-Logo-Black.png'
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
    "branding/generated/docs/DropSpace-Logo-Black.png",
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
Write-Host "Brand assets passed: immutable Black/White source hashes, Black Main default, White Backup retained, nine-frame ICO, PE resource 101, WinUI, tray, installer, README, and MSIX references."
