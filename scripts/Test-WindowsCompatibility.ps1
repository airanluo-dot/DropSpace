param(
    [string]$RepositoryRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = if ([string]::IsNullOrWhiteSpace($RepositoryRoot))
{
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}
elseif ([System.IO.Path]::IsPathRooted($RepositoryRoot))
{
    [System.IO.Path]::GetFullPath($RepositoryRoot)
}
else
{
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $RepositoryRoot))
}

. (Join-Path $PSScriptRoot "WindowsCompatibility.ps1")
$compatibility = Get-DropSpaceWindowsCompatibility
$errors = New-Object System.Collections.Generic.List[string]

function Add-CompatibilityError
{
    param([string]$Message)

    $script:errors.Add($Message)
}

function Read-CompatibilityFile
{
    param([string]$RelativePath)

    $path = Join-Path $root $RelativePath
    if (-not (Test-Path $path -PathType Leaf))
    {
        Add-CompatibilityError "Required compatibility file is missing: $RelativePath"
        return ""
    }

    return Get-Content -Path $path -Raw
}

function Assert-CompatibilityText
{
    param(
        [string]$RelativePath,
        [string]$Pattern,
        [string]$Description
    )

    $content = Read-CompatibilityFile $RelativePath
    if ([string]::IsNullOrEmpty($content) -or $content -notmatch $Pattern)
    {
        Add-CompatibilityError $Description
    }
}

$props = Read-CompatibilityFile "Directory.Build.props"
if ($props -notmatch '<DropSpaceMinimumWindowsBuild>17763</DropSpaceMinimumWindowsBuild>' -or
    $props -notmatch '<DropSpaceMinimumWindowsVersion>10\.0\.17763\.0</DropSpaceMinimumWindowsVersion>')
{
    Add-CompatibilityError "Directory.Build.props does not declare the 17763 compatibility baseline."
}

$project = Read-CompatibilityFile "src/DropSpace.App/DropSpace.App.csproj"
if ($project -notmatch '<TargetFramework>net10\.0-windows10\.0\.\$\(DropSpaceMinimumWindowsBuild\)\.0</TargetFramework>' -or
    $project -notmatch '<SupportedOSPlatformVersion>\$\(DropSpaceMinimumWindowsVersion\)</SupportedOSPlatformVersion>' -or
    $project -notmatch '<TargetPlatformMinVersion>\$\(DropSpaceMinimumWindowsVersion\)</TargetPlatformMinVersion>')
{
    Add-CompatibilityError "The app project target framework and platform minimum are not tied to the shared baseline."
}

foreach ($relativePath in @("src/DropSpace.App/Package.appxmanifest", "identity/AppxManifest.xml.template"))
{
    $manifestText = Read-CompatibilityFile $relativePath
    if ($manifestText -notmatch 'MinVersion="10\.0\.17763\.0"')
    {
        Add-CompatibilityError "$relativePath does not target Windows build 17763."
    }
}

$installer = Read-CompatibilityFile "installer/DropSpace.iss"
if ($installer -notmatch '(?m)^MinVersion=10\.0\.17763\s*$')
{
    Add-CompatibilityError "The installer minimum is not Windows 10 build 17763."
}

Assert-CompatibilityText "scripts/New-UpdateManifest.ps1" 'WindowsCompatibility\.ps1' "The update manifest generator does not import the shared compatibility baseline."
Assert-CompatibilityText "scripts/Test-UpdateManifest.ps1" 'WindowsCompatibility\.ps1' "The update manifest test does not import the shared compatibility baseline."
Assert-CompatibilityText "scripts/Test-PortableSmoke.ps1" 'WindowsCompatibility\.ps1' "The portable smoke test does not import the shared compatibility baseline."
Assert-CompatibilityText "src/DropSpace.Infrastructure/Updates/UpdateManifestParser.cs" 'WindowsCompatibilityPolicy\.IsSupportedBuild' "The updater parser is not using the shared compatibility policy."
Assert-CompatibilityText "src/DropSpace.App/App.xaml.cs" 'WindowsCompatibilityPolicy\.MinimumSupportedWindowsBuild' "Startup does not enforce the shared Windows minimum."
Assert-CompatibilityText "src/DropSpace.App/App.xaml.cs" 'IWindowsCapabilityService' "Startup is not wired to the capability service."

$mainWindowXaml = Read-CompatibilityFile "src/DropSpace.App/MainWindow.xaml"
if ($mainWindowXaml -match '<MicaBackdrop\b')
{
    Add-CompatibilityError "MainWindow.xaml directly parses MicaBackdrop without a runtime capability gate."
}
Assert-CompatibilityText "src/DropSpace.App/MainWindow.xaml.cs" 'WindowsCapability\.ModernWindowAppearance' "MainWindow does not gate Mica through the compatibility service."
Assert-CompatibilityText "src/DropSpace.App/Services/OverlayWindowInterop.cs" 'if \(modernDwmAttributes\)' "Overlay DWM attributes are not guarded for Windows 10."
Assert-CompatibilityText "src/DropSpace.App/OverlayWindow.xaml.cs" 'WindowsCapability\.ModernDwmAttributes' "Overlay construction does not pass the DWM capability gate."

Assert-CompatibilityText "src/DropSpace.App/App.xaml.cs" '--startup' "Startup-launch behavior is not present in App.xaml.cs."
if ((Read-CompatibilityFile "src/DropSpace.App/App.xaml.cs") -match 'ApplicationLanguages\.PrimaryLanguageOverride')
{
    Add-CompatibilityError "The unpackaged app must not use ApplicationLanguages.PrimaryLanguageOverride."
}

Assert-CompatibilityText "compatibility-baseline.md" '17763' "The compatibility baseline report is missing the Windows 10 1809 minimum."
Assert-CompatibilityText "docs/test-plan/v0.3.0-preview.8.md" 'Windows 10 (version )?1809' "The Preview.8 test plan is missing the required Windows 10 1809 matrix."
Assert-CompatibilityText "website/_source/src/index.html" 'Windows 10 version 1809' "The website does not state the current Windows 10 minimum."

if ($errors.Count -ne 0)
{
    $errors | ForEach-Object { Write-Error $_ }
    throw "Windows compatibility gate failed with $($errors.Count) error(s)."
}

Write-Host "Windows compatibility gate passed: minimum build $($compatibility.MinimumBuild), Windows 11 capability gate $($compatibility.Windows11Build)."
