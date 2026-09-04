Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Read-TextFile([string]$relativePath)
{
    return Get-Content -LiteralPath (Join-Path $repositoryRoot $relativePath) -Raw
}

$visualFiles = @(
    "src/DropSpace.App/OverlayWindow.xaml",
    "src/DropSpace.App/MainWindow.xaml",
    "src/DropSpace.App/Views/MainPage.xaml",
    "src/DropSpace.App/Views/DesignTokens.xaml"
)
foreach ($relativePath in $visualFiles)
{
    $path = Join-Path $repositoryRoot $relativePath
    $nakedColors = Select-String -LiteralPath $path -Pattern '(?i)(?:Color|Background|Foreground|BorderBrush|Fill)s*=s*["'']#'
    if ($null -ne $nakedColors)
    {
        throw "Naked XAML color found in $relativePath. Use semantic ThemeResource or DesignTokens resources."
    }
}

$motionSource = Read-TextFile "src/DropSpace.Core/Overlay/OverlayMotionProfiles.cs"
$motionDocs = Read-TextFile "DESIGN_SYSTEM.md"
$motionTokens = [ordered]@{
    InstantMilliseconds = 0
    FasterMilliseconds = 83
    FastMilliseconds = 167
    NormalMilliseconds = 250
    SlowMilliseconds = 333
}
foreach ($entry in $motionTokens.GetEnumerator())
{
    $sourceToken = "public const double $($entry.Key) = $($entry.Value);"
    if ($motionSource.IndexOf($sourceToken, [StringComparison]::Ordinal) -lt 0)
    {
        throw "Motion token $($entry.Key) is missing from OverlayMotionTokens."
    }

    $documentationToken = "OverlayMotionTokens.$($entry.Key)"
    if ($motionDocs.IndexOf($documentationToken, [StringComparison]::Ordinal) -lt 0)
    {
        throw "DESIGN_SYSTEM.md does not identify the owner of motion token $($entry.Key)."
    }
}

$dropLinkContract = Read-TextFile "src/DropSpace.Infrastructure/Network/DropLinkProtocolContract.cs"
$routeFiles = Get-ChildItem (Join-Path $repositoryRoot "src/DropSpace.Infrastructure/Network") -Filter "*.cs" -File |
    Where-Object { $_.Name -ne "DropLinkProtocolContract.cs" }
foreach ($file in $routeFiles)
{
    $directRoute = Select-String -LiteralPath $file.FullName -Pattern "/v1/" -SimpleMatch
    if ($null -ne $directRoute)
    {
        throw "Direct /v1/ route literal found in $($file.FullName). Consume DropLinkProtocolRoutes instead."
    }
}
if ($dropLinkContract.IndexOf("VersionPrefix = ""/v1""", [StringComparison]::Ordinal) -lt 0)
{
    throw "DropLinkProtocolContract no longer owns the protocol version prefix."
}

$policyOwners = @(
    "src/DropSpace.App/Services/Ole/SmartDragRuntimePolicy.cs",
    "src/DropSpace.Core/Models/SettingsValidationPolicy.cs",
    "src/DropSpace.Infrastructure/Network/DropLinkProtocolContract.cs",
    "src/DropSpace.Core/Overlay/OverlayMotionProfiles.cs"
)
foreach ($relativePath in $policyOwners)
{
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath) -PathType Leaf))
    {
        throw "Required policy/contract owner is missing: $relativePath"
    }
}

$delayFiles = @(
    "src/DropSpace.App/Services/DragSessionDetector.cs",
    "src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs"
)
foreach ($relativePath in $delayFiles)
{
    $literalDelay = Select-String -LiteralPath (Join-Path $repositoryRoot $relativePath) -Pattern 'Task.Delay(s*(?:d+|TimeSpan.From(?:Milliseconds|Seconds)(s*d+)'
    if ($null -ne $literalDelay)
    {
        throw "Unowned Task.Delay literal found in $relativePath. Use SmartDragRuntimePolicy."
    }
}

$releaseContract = Read-TextFile "website/_source/scripts/release-contract.mjs"
$releaseWorkflow = Read-TextFile ".github/workflows/release.yml"
$requiredArtifacts = @(
    "DropSpaceSetup.exe",
    "DropSpace.exe",
    "DropSpace-x64.msix",
    "SHA256SUMS.txt",
    "update-manifest.json"
)
foreach ($artifact in $requiredArtifacts)
{
    if ($releaseContract.IndexOf($artifact, [StringComparison]::Ordinal) -lt 0 -or
        $releaseWorkflow.IndexOf($artifact, [StringComparison]::Ordinal) -lt 0)
    {
        throw "Release artifact $artifact is not represented in both the website contract and release workflow."
    }
}

Write-Host "Hardcoding governance passed: semantic colors, motion ownership, DropLink routes, policy owners, delay literals, and release artifacts are governed."
