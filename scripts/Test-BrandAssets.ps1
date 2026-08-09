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

$requiredSizes = @(16, 24, 32, 48, 64, 128, 256)
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
}
foreach ($entry in $references.GetEnumerator())
{
    $content = Get-Content (Join-Path $repositoryRoot $entry.Key) -Raw
    if (-not $content.Contains($entry.Value, [StringComparison]::Ordinal))
    {
        throw "Brand asset reference is missing from $($entry.Key): $($entry.Value)"
    }
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
Write-Host "Brand assets passed: canonical ICO frames $($sizes -join ', '), PE icons, Win32 resource, WinUI, tray, and installer references."
