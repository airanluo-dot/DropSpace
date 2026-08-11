param(
    [string]$DestinationRoot = "src/DropSpace.App/Assets",
    [string]$DocumentationRoot = "branding/generated/docs",
    [switch]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$masterRoot = Join-Path $repositoryRoot "branding/master"
$blackMainSource = Join-Path $masterRoot "black/DropSpace-Black-Main-Original.png"
$whiteBackupSource = Join-Path $masterRoot "white/DropSpace-White-Backup-Original.png"
$sourceManifest = Join-Path $repositoryRoot "branding/SOURCE_MANIFEST.json"

foreach ($source in @($blackMainSource, $whiteBackupSource, $sourceManifest))
{
    if (-not (Test-Path $source -PathType Leaf))
    {
        throw "Required canonical brand source is missing: $source"
    }
}

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

function Resolve-RepositoryPath
{
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path))
    {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Read-Bitmap
{
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try
    {
        $bitmap = [System.Windows.Media.Imaging.BitmapImage]::new()
        $bitmap.BeginInit()
        $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
        $bitmap.CreateOptions = [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat
        $bitmap.StreamSource = $stream
        $bitmap.EndInit()
        $bitmap.Freeze()
        return $bitmap
    }
    finally
    {
        $stream.Dispose()
    }
}

function Write-RenderedPng
{
    param(
        [Parameter(Mandatory = $true)][System.Windows.Media.Imaging.BitmapSource]$Source,
        [Parameter(Mandatory = $true)][int]$Width,
        [Parameter(Mandatory = $true)][int]$Height,
        [Parameter(Mandatory = $true)][double]$ContentScale,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ($Width -lt 1 -or $Height -lt 1 -or $ContentScale -le 0 -or $ContentScale -gt 1)
    {
        throw "Invalid render geometry: ${Width}x${Height}, scale $ContentScale."
    }

    $contentSize = [Math]::Min($Width, $Height) * $ContentScale
    $left = ($Width - $contentSize) / 2
    $top = ($Height - $contentSize) / 2
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try
    {
        # The supplied Black Main artwork includes its authoritative black canvas,
        # glow, and reflection. Keep the entire generated surface black so wide
        # tiles and splash assets do not expose an unintended transparent variant.
        $context.DrawRectangle(
            [System.Windows.Media.Brushes]::Black,
            $null,
            [System.Windows.Rect]::new(0, 0, $Width, $Height))
        $context.DrawImage(
            $Source,
            [System.Windows.Rect]::new($left, $top, $contentSize, $contentSize))
    }
    finally
    {
        $context.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Width,
        $Height,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()

    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path)) | Out-Null
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $output = [System.IO.File]::Create($Path)
    try
    {
        $encoder.Save($output)
    }
    finally
    {
        $output.Dispose()
    }
}

function Write-Ico
{
    param(
        [Parameter(Mandatory = $true)][hashtable]$Frames,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $sizes = @($Frames.Keys | ForEach-Object { [int]$_ } | Sort-Object)
    if ($sizes.Count -gt [UInt16]::MaxValue)
    {
        throw "Too many ICO frames."
    }

    $payloads = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes)
    {
        $payloads.Add([System.IO.File]::ReadAllBytes([string]$Frames[$size]))
    }

    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path)) | Out-Null
    $stream = [System.IO.File]::Create($Path)
    try
    {
        $writer = [System.IO.BinaryWriter]::new($stream, [System.Text.Encoding]::UTF8, $true)
        try
        {
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]$sizes.Count)
            $offset = 6 + (16 * $sizes.Count)
            for ($index = 0; $index -lt $sizes.Count; $index++)
            {
                $size = $sizes[$index]
                $payload = $payloads[$index]
                $dimension = if ($size -eq 256) { 0 } else { $size }
                $writer.Write([byte]$dimension)
                $writer.Write([byte]$dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$payload.Length)
                $writer.Write([UInt32]$offset)
                $offset += $payload.Length
            }

            foreach ($payload in $payloads)
            {
                $writer.Write($payload)
            }
        }
        finally
        {
            $writer.Dispose()
        }
    }
    finally
    {
        $stream.Dispose()
    }
}

function Generate-Assets
{
    param(
        [Parameter(Mandatory = $true)][string]$AssetRoot,
        [Parameter(Mandatory = $true)][string]$DocsRoot
    )

    [System.IO.Directory]::CreateDirectory($AssetRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($DocsRoot) | Out-Null
    $blackMain = Read-Bitmap $blackMainSource

    $icoSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $icoFrames = @{}
    foreach ($size in $icoSizes)
    {
        $framePath = Join-Path $AssetRoot "AppIcon.frame-$size.png"
        Write-RenderedPng $blackMain $size $size 1 $framePath
        $icoFrames[$size] = $framePath
    }
    Write-Ico $icoFrames (Join-Path $AssetRoot "AppIcon.ico")
    foreach ($framePath in $icoFrames.Values)
    {
        [System.IO.File]::Delete([string]$framePath)
    }
    $scaleFactors = [ordered]@{ 100 = 1.0; 125 = 1.25; 150 = 1.5; 200 = 2.0; 400 = 4.0 }
    foreach ($entry in $scaleFactors.GetEnumerator())
    {
        $scale = [int]$entry.Key
        $factor = [double]$entry.Value
        $square44 = [int][Math]::Round(44 * $factor, [MidpointRounding]::AwayFromZero)
        $square150 = [int][Math]::Round(150 * $factor, [MidpointRounding]::AwayFromZero)
        $store = [int][Math]::Round(50 * $factor, [MidpointRounding]::AwayFromZero)
        $wideWidth = [int][Math]::Round(310 * $factor, [MidpointRounding]::AwayFromZero)
        $wideHeight = [int][Math]::Round(150 * $factor, [MidpointRounding]::AwayFromZero)
        $splashWidth = [int][Math]::Round(620 * $factor, [MidpointRounding]::AwayFromZero)
        $splashHeight = [int][Math]::Round(300 * $factor, [MidpointRounding]::AwayFromZero)
        Write-RenderedPng $blackMain $square44 $square44 1 (Join-Path $AssetRoot "Square44x44Logo.scale-$scale.png")
        Write-RenderedPng $blackMain $square150 $square150 1 (Join-Path $AssetRoot "Square150x150Logo.scale-$scale.png")
        Write-RenderedPng $blackMain $store $store 1 (Join-Path $AssetRoot "StoreLogo.scale-$scale.png")
        Write-RenderedPng $blackMain $wideWidth $wideHeight 0.72 (Join-Path $AssetRoot "Wide310x150Logo.scale-$scale.png")
        Write-RenderedPng $blackMain $splashWidth $splashHeight 0.72 (Join-Path $AssetRoot "SplashScreen.scale-$scale.png")
    }
    [System.IO.File]::Copy((Join-Path $AssetRoot "StoreLogo.scale-100.png"), (Join-Path $AssetRoot "StoreLogo.png"), $true)

    foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256))
    {
        Write-RenderedPng $blackMain $size $size 1 (Join-Path $AssetRoot "Square44x44Logo.targetsize-$($size)_altform-unplated.png")
    }
    Write-RenderedPng $blackMain 48 48 1 (Join-Path $AssetRoot "LockScreenLogo.scale-200.png")

    Write-RenderedPng $blackMain 512 512 1 (Join-Path $DocsRoot "DropSpace-Logo-Black.png")
}

$destination = Resolve-RepositoryPath $DestinationRoot
$documentation = Resolve-RepositoryPath $DocumentationRoot
if (-not $Verify)
{
    Generate-Assets $destination $documentation
    Write-Host "Generated DropSpace brand assets from the authoritative Black Main master."
    Write-Host "ICO frames: Black Main artwork at 16/20/24/32/40/48/64/128/256. White Backup is retained only as a canonical fallback source."
    return
}

$verificationRoot = Join-Path ([System.IO.Path]::GetTempPath()) "DropSpace-brand-$([Guid]::NewGuid().ToString('N'))"
$verificationAssets = Join-Path $verificationRoot "assets"
$verificationDocs = Join-Path $verificationRoot "docs"
try
{
    Generate-Assets $verificationAssets $verificationDocs
    $generatedFiles = Get-ChildItem $verificationAssets -File | Sort-Object Name
    foreach ($generated in $generatedFiles)
    {
        $existing = Join-Path $destination $generated.Name
        if (-not (Test-Path $existing -PathType Leaf))
        {
            throw "Generated brand asset is missing from the repository output: $existing"
        }

        $expectedHash = (Get-FileHash $generated.FullName -Algorithm SHA256).Hash
        $actualHash = (Get-FileHash $existing -Algorithm SHA256).Hash
        if ($expectedHash -ne $actualHash)
        {
            throw "Generated brand asset is stale: $existing"
        }
    }

    foreach ($generated in (Get-ChildItem $verificationDocs -File | Sort-Object Name))
    {
        $existing = Join-Path $documentation $generated.Name
        if (-not (Test-Path $existing -PathType Leaf) -or
            (Get-FileHash $generated.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash $existing -Algorithm SHA256).Hash)
        {
            throw "Generated documentation brand asset is stale: $existing"
        }
    }

    Write-Host "Brand generation is deterministic and all generated assets are current."
}
finally
{
    if (Test-Path $verificationRoot)
    {
        Remove-Item $verificationRoot -Recurse -Force
    }
}
