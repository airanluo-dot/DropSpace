[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)] [string] $Timeline,
    [string] $OutputDirectory,
    [string] $OcrLanguage = 'en-US'
)

$ErrorActionPreference = 'Stop'
$timelinePath = (Resolve-Path -LiteralPath $Timeline).ProviderPath
$frames = "$timelinePath.frames"
if (-not (Test-Path -LiteralPath (Join-Path $frames 'pairs.jsonl') -PathType Leaf)) {
    throw "Missing frame manifest next to the timeline. Record with AppleMusicTimeline --frames first."
}
if (-not $OutputDirectory) { $OutputDirectory = "$timelinePath.comparison" }
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$pixels = Join-Path $output 'pixels.jsonl'
$project = Join-Path $PSScriptRoot 'ReadLyricFrames.csproj'
$compare = Join-Path $PSScriptRoot 'compare.py'
$python = Get-Command py -ErrorAction SilentlyContinue
$pythonArgs = @('-3')
if (-not $python) {
    $python = Get-Command python
    $pythonArgs = @()
}

& dotnet run --project $project -c Release -- $frames $pixels $OcrLanguage
if ($LASTEXITCODE -ne 0) { throw "Frame OCR failed (exit $LASTEXITCODE)." }
& $python.Source @pythonArgs $compare $timelinePath $pixels $output
if ($LASTEXITCODE -ne 0) { throw "Word comparison failed (exit $LASTEXITCODE)." }
Write-Output "Report: $output"
