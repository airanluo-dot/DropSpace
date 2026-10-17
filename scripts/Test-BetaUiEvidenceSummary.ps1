# One explicit helper-only scenario; no App process, PNG transport or launcher body runs.
[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'Test-BetaUiDiagnostics.ps1'),
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'The summary evidence directory must be fresh.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$source = [IO.Path]::GetFullPath($SourcePath)
$receipt = [ordered]@{
    kind = 'beta-ui-summary-helper-shape-probe'; scenario = 'lifecycle-checkpoint-summary-shapes'
    sourcePath = $source; sourceSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    actualScenarioCount = 1; passedScenarioCount = 0; assertionsPassed = 0
    status = 'running'; stage = 'load-production-helpers'; failure = $null
    nativeAppExecutions = 0; launcherExecutions = 0; pngTransportExecutions = 0
}
function Assert-Shape([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $receipt.assertionsPassed++
}
try {
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
    foreach ($name in @('Limit-Observation', 'Select-EvidenceFields', 'Get-VisualCheckpointSummary')) {
        $definitions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true))
        if ($definitions.Count -ne 1) { throw "Expected exactly one production helper: $name" }
        . ([scriptblock]::Create($definitions[0].Extent.Text))
    }
    # These values use the App receipt schema, including its empty post-close layers.
    $samples = @(
        @{ name = 'empty'; layers = @(); events = @(); truncated = $false },
        @{ name = 'singleton'; layers = @(@{ name = 'Surface'; xamlOpacity = 1.0; compositionOpacity = 1.0 }); events = @(@{ sequence = 1; kind = 'app-window-closing'; detail = @{ cancel = $true } }); truncated = $false },
        @{ name = 'nonempty'; layers = @(@{ name = 'Surface' }, @{ name = 'ExpandedPanel' }, @{ name = 'WidgetsExpanded' }); events = @(@{ sequence = 1; kind = 'app-window-closing' }, @{ sequence = 2; kind = 'window-closed' }); truncated = $true }
    )
    $summaries = @(
        foreach ($sample in $samples) {
            $receipt.stage = $sample.name
            $checkpoint = @{
                stage = $sample.name; captureOrder = 'frozen-opacity-then-desktop-before-rtb'
                motionSettled = $true; frameActive = $null; phase = $null; nativeVisibility = $null
                layers = $sample.layers
                lifecycle = @{
                    hwnd = '0x1234'; processId = 123; closingEvents = 1; closedEvents = 1; cancelObserved = $true
                    closeRequestPosted = $true; shutdownRequested = $true; experienceChangedEvents = 2; experienceState = 'Hidden'
                    native = @{ isWindow = $false; processId = 0; regionType = $null; regionBounds = $null }
                    eventSequence = $sample.events; eventSequenceTruncated = $sample.truncated
                }
            }
            $summary = Get-VisualCheckpointSummary $checkpoint
            $json = ConvertTo-Json -InputObject $summary -Depth 10 -Compress
            $roundtrip = ConvertFrom-Json -InputObject $json -AsHashtable
            Assert-Shape ($roundtrip.layers -is [array]) "$($sample.name): layers must remain a JSON array."
            Assert-Shape ($roundtrip.layers.Count -eq $sample.layers.Count) "$($sample.name): layer count changed."
            Assert-Shape ($roundtrip.lifecycle.eventSequence -is [array]) "$($sample.name): eventSequence must remain a JSON array."
            Assert-Shape ($roundtrip.lifecycle.eventSequence.Count -eq $sample.events.Count) "$($sample.name): event count changed."
            Assert-Shape ($roundtrip.lifecycle.eventSequenceTruncated -eq $sample.truncated) "$($sample.name): upstream truncation flag changed."
            $summary
        }
    )
    $receipt.stage = 'nullable'
    $nullable = Get-VisualCheckpointSummary @{ stage = 'nullable'; layers = $null; popups = $null; lifecycle = $null }
    $nullableJson = ConvertTo-Json -InputObject $nullable -Depth 10 -Compress
    $nullableRoundtrip = ConvertFrom-Json -InputObject $nullableJson -AsHashtable
    Assert-Shape ($nullableRoundtrip.Contains('layers') -and $null -eq $nullableRoundtrip.layers) 'Nullable layers changed shape.'
    Assert-Shape ($nullableRoundtrip.Contains('popups') -and $null -eq $nullableRoundtrip.popups) 'Nullable popups changed shape.'
    Assert-Shape (-not $nullableRoundtrip.Contains('lifecycle')) 'Nullable lifecycle must retain its existing omission behavior.'
    $receipt.stage = 'bounded-event-sequence'
    $large = Get-VisualCheckpointSummary @{ stage = 'large'; layers = @(); lifecycle = @{ eventSequence = @(@{ detail = ('x' * 2100) }); eventSequenceTruncated = $true } }
    Assert-Shape ($large.lifecycle.eventSequence.truncated -eq $true -and $large.lifecycle.eventSequence.jsonPrefix.Length -eq 2000) 'Oversized event sequence lost its existing explicit summary bound.'
    Assert-Shape ($large.lifecycle.eventSequenceTruncated -eq $true) 'Upstream truncation must remain visible beside log truncation.'
    ConvertTo-Json -InputObject @($summaries + @($nullable, $large)) -Depth 10 | Set-Content (Join-Path $OutputDirectory 'summaries.json') -Encoding utf8
    $receipt.status = 'passed'; $receipt.passedScenarioCount = 1; $receipt.stage = 'completed'
}
catch {
    $receipt.status = 'failed'
    $receipt.failure = $_.Exception.Message
    $receipt.failureDetail = $_ | Out-String
}
finally {
    ConvertTo-Json -InputObject $receipt -Depth 6 | Set-Content (Join-Path $OutputDirectory 'receipt.json') -Encoding utf8
    Write-Output ('DROPSPACE_BETA_UI_SUMMARY_PROBE=' + (ConvertTo-Json -InputObject $receipt -Depth 6 -Compress))
}
if ($receipt.status -cne 'passed') { exit 1 }
