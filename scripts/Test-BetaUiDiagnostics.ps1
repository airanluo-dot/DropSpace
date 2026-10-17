# Launches only the selected bounded native UI diagnostic, never the broad smoke suite.
[CmdletBinding()]
param(
    [string]$BuildReceiptPath = 'artifacts/beta-ui-diagnostics/build/build-receipt.json',
    [string]$OutputDirectory = 'artifacts/beta-ui-diagnostics/run',
    [ValidateSet('all', 'timers', 'batch-selection', 'timer-visual', 'overlay-lifecycle')][string]$Scenario = 'all',
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 240,
    [switch]$IncludeSupplementalImagesInLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native Beta UI diagnostics require Windows.' }
$Scenario = $Scenario.ToLowerInvariant()
$timerScenarios = @('timer-menu-retirement')
$timerVisualScenarios = @('timer-first-frame-and-popup-retirement')
$overlayLifecycleScenarios = @('overlay-first-frame-and-system-close')
$batchScenarios = @('batch-collapse-selection')
$timerCaptures = @('timers-initial.png', 'timer-menu-FocusTimer.png', 'timer-menu-Countdown.png', 'timers-reopened.png')
$timerVisualCaptures = @('timers-initial.png', 'timer-menu-FocusTimer.png', 'timers-reopened.png')
$overlayLifecycleCaptures = @('timers-initial.png', 'timers-reopened.png')
$batchCaptures = @('batch-expanded-selected.png', 'batch-collapsed-selection.png')
$supplementalCaptureNames = @('timers-initial-desktop.png', 'timer-menu-FocusTimer-desktop.png', 'timer-menu-Countdown-desktop.png', 'timers-reopened-desktop.png')
$selectionContracts = @{
    'all' = @{ scenarios = $timerScenarios + $batchScenarios; captures = $timerCaptures + $batchCaptures }
    'timers' = @{ scenarios = $timerScenarios; captures = $timerCaptures }
    'batch-selection' = @{ scenarios = $batchScenarios; captures = $batchCaptures }
    'timer-visual' = @{ scenarios = $timerVisualScenarios; captures = $timerVisualCaptures }
    'overlay-lifecycle' = @{ scenarios = $overlayLifecycleScenarios; captures = $overlayLifecycleCaptures }
}
$expectedScenarios = @($selectionContracts[$Scenario].scenarios)
$expectedCaptures = @($selectionContracts[$Scenario].captures)
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Resolve-RepositoryPath([string]$Path) {
    return [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathFullyQualified($Path)) { $Path } else { Join-Path $repo $Path }))
}
$output = Resolve-RepositoryPath $OutputDirectory
if (Test-Path -LiteralPath $output) { throw 'The native diagnostic evidence directory must be fresh.' }
New-Item -ItemType Directory -Path $output | Out-Null
$appOutput = Join-Path $output 'app' # The App, not this launcher, creates and owns this fresh directory.
$receiptPath = Join-Path $appOutput 'receipt.json'
$stagePath = Join-Path $appOutput 'stage.json'
$launch = [ordered]@{
    schemaVersion = 1; kind = 'windows-debug-native-ui-launch'; status = 'failed'; stage = 'validate-build'; failureStage = $null
    sourceCommit = $null; sourceTree = $null; executableSha256 = $null; resourceIndexSha256 = $null; managedAssemblyHashes = @{}
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); finishedAtUtc = $null; processId = $null
    exitCode = $null; timedOut = $false; processExited = $false; actualCaseCount = 0; passedCaseCount = 0
    requestedScenario = $Scenario; selectedScenario = $null; plannedCaseCount = $expectedScenarios.Count; reportedPlannedCaseCount = $null
    visualStatus = 'not-run'; scenarios = @(); captures = @(); captureFailures = @(); observations = @(); exceptions = @(); environment = $null; failure = $null
    supplementalCaptures = @(); supplementalCaptureFailures = @(); supplementalEvidenceFailure = $null
    supplementalImageLog = @(); supplementalImageLogRequested = [bool]$IncludeSupplementalImagesInLog
    visualCheckpoints = @()
    releasePromotionEligible = $false; physicalPointerValidated = $false; appReceiptSha256 = $null
}
function Copy-AppEvidenceSummary([System.Collections.IDictionary]$AppReceipt) {
    $launch.stage = $AppReceipt.stage
    $launch.failureStage = $AppReceipt.failureStage
    $launch.selectedScenario = $AppReceipt.selectedScenario
    $launch.reportedPlannedCaseCount = $AppReceipt.plannedCaseCount
    $launch.actualCaseCount = $AppReceipt.actualCaseCount
    $launch.passedCaseCount = $AppReceipt.passedCaseCount
    $launch.visualStatus = $AppReceipt.visualStatus
    $launch.environment = $AppReceipt.environment
    $launch.scenarios = @($AppReceipt.scenarios)
    $launch.captureFailures = @($AppReceipt.captureFailures)
    $launch.observations = @($AppReceipt.observations)
    $launch.exceptions = @($AppReceipt.exceptions)
    if ($AppReceipt.Contains('visualCheckpoints')) { $launch.visualCheckpoints = @($AppReceipt.visualCheckpoints) }
    $launch.captures = @($AppReceipt.captures | ForEach-Object {
        $capture = $_
        $metadata = [ordered]@{ file = $capture.file; sha256 = $capture.sha256; width = $capture.pixelWidth; height = $capture.pixelHeight; pixelReadbackPassed = $capture.pixelReadbackPassed }
        foreach ($key in @('rasterizationScale', 'targetKind', 'opaquePixelCount', 'transparentPixelCount', 'nonzeroAlphaPixelCount', 'distinctColorCountUpTo64', 'xamlOpacity', 'compositionOpacity')) {
            if ($capture.ContainsKey($key)) { $metadata[$key] = $capture[$key] }
        }
        $metadata
    })
    # Supplemental desktop evidence cannot replace the RTB captures or alter their gate.
    try {
        $launch.supplementalCaptures = @(
            if ($AppReceipt.Contains('supplementalCaptures')) {
                foreach ($file in $supplementalCaptureNames) {
                    $matchingCaptures = @($AppReceipt.supplementalCaptures | Where-Object { $_.file -ceq $file })
                    if ($matchingCaptures.Count -eq 0) { continue }
                    $metadata = [ordered]@{ file = $file; receiptEntryCount = $matchingCaptures.Count }
                    foreach ($key in @('sha256', 'pixelWidth', 'pixelHeight', 'status', 'captureKind', 'sourceApi', 'sourcePixelSha256', 'sourcePixelFormat', 'synthesizedOpaqueAlpha', 'bounds', 'boundsAfter', 'overlayHwnd', 'ownedHwnd', 'ownershipBefore', 'ownershipAfter', 'environment', 'limitations', 'stage', 'reason', 'details')) {
                        if ($matchingCaptures[0].ContainsKey($key)) { $metadata[$key] = $matchingCaptures[0][$key] }
                    }
                    $metadata
                }
            }
        )
        if ($AppReceipt.Contains('supplementalCaptureFailures')) {
            $launch.supplementalCaptureFailures = @($AppReceipt.supplementalCaptureFailures)
        }
    }
    catch { $launch.supplementalEvidenceFailure = Limit-SummaryText $_.Exception.Message }
}
function Limit-SummaryText([object]$Value) {
    if ($null -eq $Value) { return $null }
    $text = [string]$Value
    if ($text.Length -gt 1500) { return $text.Substring(0, 1500) + ' [truncated; see artifact]' }
    return $text
}
function Limit-Observation([object]$Value) {
    if ($null -eq $Value) { return $null }
    $json = $Value | ConvertTo-Json -Depth 8 -Compress
    if ($json.Length -gt 2000) {
        return [ordered]@{ truncated = $true; jsonPrefix = $json.Substring(0, 2000); completeEvidence = 'launcher.json and app/receipt.json' }
    }
    return $Value
}
function Select-EvidenceFields([System.Collections.IDictionary]$Value, [string[]]$Keys) {
    $selected = [ordered]@{}
    if ($null -ne $Value) {
        foreach ($key in $Keys) {
            if ($Value.Contains($key)) { $selected[$key] = $Value[$key] }
        }
    }
    return $selected
}
function Limit-DesktopOwnership([System.Collections.IDictionary]$Value) {
    if ($null -eq $Value) { return $null }
    $result = Select-EvidenceFields $Value @('bounds', 'virtualScreen', 'overlayHwnd', 'ownedHwnd', 'processId', 'isWindow', 'isVisible', 'isIconic', 'cloakResult', 'cloaked', 'dpi')
    $samples = @(if ($Value.Contains('samples')) { $Value.samples })
    $failed = @($samples | Where-Object { $_.ownedByOverlay -ne $true })
    $result.sampleCount = $samples.Count
    $result.failedSampleCount = $failed.Count
    # Preserve the center identity and up to three selected ownership samples.
    # The complete nine-point snapshots remain in both JSON artifacts.
    $selectedIndices = @(
        if ($samples.Count -gt 4) { 4 }
        for ($index = 0; $index -lt $samples.Count; $index++) {
            if ($samples[$index].ownedByOverlay -ne $true) { $index }
        }
    )
    $indices = @($selectedIndices | Select-Object -Unique | Select-Object -First 3)
    $result.samples = @(
        foreach ($index in $indices) {
            $sample = Select-EvidenceFields $samples[$index] @('x', 'y', 'hwnd', 'processId', 'rootHwnd', 'rootProcessId', 'rootOwnerHwnd', 'rootIsVisible', 'rootIsIconic', 'rootCloakResult', 'rootCloaked', 'ownedByOverlay')
            $sample.sampleIndex = $index
            $owners = @($samples[$index].ownerChain)
            $sample.ownerChain = @($owners | Select-Object -First 4)
            $sample.ownerChainTruncated = $owners.Count -gt 4
            $sample
        }
    )
    $result.omittedSampleCount = $samples.Count - $result.samples.Count
    $result.truncated = $result.omittedSampleCount -gt 0 -or @($result.samples | Where-Object { $_.ownerChainTruncated }).Count -gt 0
    $result.completeEvidence = 'launcher.json and app/receipt.json'
    return $result
}
function Get-SupplementalSummary([System.Collections.IDictionary]$Value) {
    # Never truncate the capture identity, source, dimensions, bounds or failure
    # ROI as one JSON prefix. Only the nested ownership snapshots are reduced.
    $result = Select-EvidenceFields $Value @('file', 'receiptEntryCount', 'sha256', 'pixelWidth', 'pixelHeight', 'status', 'captureKind', 'sourceApi', 'sourcePixelSha256', 'sourcePixelFormat', 'synthesizedOpaqueAlpha', 'bounds', 'boundsAfter', 'overlayHwnd', 'ownedHwnd', 'environment', 'limitations', 'stage')
    if ($Value.Contains('reason')) { $result.reason = Limit-SummaryText $Value.reason }
    foreach ($key in @('ownershipBefore', 'ownershipAfter')) {
        if ($Value.Contains($key)) { $result[$key] = Limit-DesktopOwnership $Value[$key] }
    }
    if ($Value.Contains('details') -and $null -ne $Value.details) {
        $result.details = Select-EvidenceFields $Value.details @('boundsProbe', 'boundsAfterProbe', 'elapsedMilliseconds')
        foreach ($key in @('before', 'after')) {
            if ($Value.details.Contains($key)) { $result.details[$key] = Limit-DesktopOwnership $Value.details[$key] }
        }
    }
    return $result
}
function Get-VisualCheckpointSummary([System.Collections.IDictionary]$Value) {
    $result = Select-EvidenceFields $Value @('stage', 'captureOrder', 'motionSettled', 'frameActive', 'phase', 'nativeVisibility')
    foreach ($key in @('layers', 'popups')) {
        if ($Value.Contains($key)) { $result[$key] = Limit-Observation $Value[$key] }
    }
    if ($Value.Contains('lifecycle') -and $null -ne $Value.lifecycle) {
        $result.lifecycle = Select-EvidenceFields $Value.lifecycle @('hwnd', 'processId', 'closingEvents', 'closedEvents', 'cancelObserved', 'closeRequestPosted', 'shutdownRequested', 'experienceChangedEvents', 'experienceState', 'native', 'eventSequenceTruncated')
        if ($Value.lifecycle.Contains('eventSequence')) { $result.lifecycle.eventSequence = Limit-Observation $Value.lifecycle.eventSequence }
    }
    return $result
}
function Export-SupplementalImageLog {
    $totalBytes = 0
    $launch.supplementalImageLog = @(
        foreach ($capture in $launch.supplementalCaptures) {
            $entry = [ordered]@{ file = $capture.file; status = 'skipped'; reason = $null; bytes = $null; sha256 = $null; chunkCount = 0 }
            try {
                if (-not $IncludeSupplementalImagesInLog) { $entry.reason = 'log-transfer-not-requested'; continue }
                if ($Scenario -ceq 'batch-selection') { $entry.reason = 'timer-scenario-not-selected'; continue }
                if ($capture.receiptEntryCount -ne 1) { $entry.reason = 'repeated-receipt-entry'; continue }
                if (-not $capture.Contains('status') -or $capture.status -cne 'captured-for-manual-review') {
                    $entry.reason = 'supplemental-capture-not-completed'; continue
                }
                if ($null -eq $child -or -not $launch.processExited -or $launch.processId -ne $child.Id -or
                    $null -eq $launch.environment -or $launch.environment.processId -ne $launch.processId -or
                    -not $capture.Contains('environment') -or $capture.environment.processId -ne $launch.processId) {
                    $entry.reason = 'owned-child-identity-not-confirmed'; continue
                }
                if (-not $capture.Contains('sha256') -or $capture.sha256 -cnotmatch '^[a-f0-9]{64}$') {
                    $entry.reason = 'missing-or-invalid-receipt-hash'; continue
                }
                # The allowlist is exact, and only this process's fresh App directory is read.
                if ($capture.file -cnotin $supplementalCaptureNames) { $entry.reason = 'filename-not-allowed'; continue }
                $image = Join-Path $appOutput $capture.file
                if (-not (Test-Path -LiteralPath $image -PathType Leaf)) { $entry.reason = 'file-not-created'; continue }
                $item = Get-Item -LiteralPath $image
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                    ((Get-Item -LiteralPath $appOutput).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $entry.reason = 'linked-path-refused'; continue
                }
                $entry.bytes = $item.Length
                if ($item.Length -lt 8 -or $item.Length -gt 128KB) { $entry.reason = 'per-image-byte-limit'; continue }
                if ($totalBytes + $item.Length -gt 256KB) { $entry.reason = 'total-image-byte-limit'; continue }
                $bytes = [IO.File]::ReadAllBytes($image)
                if ($bytes.Length -ne $item.Length) { $entry.reason = 'file-size-changed'; continue }
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                $entry.sha256 = $hash
                if ($hash -cne $capture.sha256) { $entry.reason = 'receipt-hash-mismatch'; continue }
                if ([Convert]::ToHexString([byte[]]$bytes[0..7]) -cne '89504E470D0A1A0A') { $entry.reason = 'invalid-png-signature'; continue }
                $base64 = [Convert]::ToBase64String($bytes)
                $chunkCount = [int][Math]::Ceiling($base64.Length / 4096.0)
                for ($index = 0; $index -lt $chunkCount; $index++) {
                    $offset = $index * 4096
                    $chunk = [ordered]@{
                        file = $capture.file; bytes = $bytes.Length; sha256 = $hash
                        chunkIndex = $index; chunkCount = $chunkCount
                        base64 = $base64.Substring($offset, [Math]::Min(4096, $base64.Length - $offset))
                    }
                    # Original PNG bytes only; no resizing, re-encoding, or visual-pass claim.
                    Write-Host ('DROPSPACE_BETA_UI_SUPPLEMENTAL_PNG=' + ($chunk | ConvertTo-Json -Compress))
                }
                $totalBytes += $bytes.Length
                $entry.status = 'transferred'; $entry.reason = $null; $entry.chunkCount = $chunkCount
            }
            catch { $entry.reason = 'transfer-error: ' + (Limit-SummaryText $_.Exception.Message) }
            finally { $entry }
        }
    )
}
$child = $null; $stdout = $null; $stderr = $null
Push-Location $repo
try {
    $build = Get-Content -LiteralPath (Resolve-RepositoryPath $BuildReceiptPath) -Raw | ConvertFrom-Json -AsHashtable
    if ($build.kind -cne 'windows-debug-native-ui-build' -or $build.status -cne 'passed' -or
        $build.configuration -cne 'Debug' -or $build.releasePromotionEligible -ne $false) {
        throw 'The supplied receipt is not a successful nonpublishing Debug UI build.'
    }
    $launch.sourceCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the source commit.' }
    $launch.sourceTree = (& git rev-parse 'HEAD^{tree}').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the source tree.' }
    if ($launch.sourceCommit -cne $build.sourceCommit -or $launch.sourceTree -cne $build.sourceTree -or
        ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $launch.sourceCommit)) { throw 'The current checkout differs from the executable build receipt.' }
    $changes = @(& git status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'The diagnostic source checkout changed after the build.' }
    $exe = $build.executablePath
    if (-not [IO.Path]::IsPathFullyQualified($exe) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The recorded executable is missing.' }
    $launch.executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $launch.resourceIndexSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) 'DropSpace.resources.pri') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($launch.executableSha256 -cne $build.executableSha256 -or $launch.resourceIndexSha256 -cne $build.resourceIndexSha256) { throw 'Executable or resource-index bytes changed after the build.' }
    foreach ($assembly in @('DropSpace.dll', 'DropSpace.Core.dll', 'DropSpace.Infrastructure.dll')) {
        $hash = (Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) $assembly) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $build.managedAssemblyHashes[$assembly]) { throw "Managed application bytes changed after the build: $assembly" }
        $launch.managedAssemblyHashes[$assembly] = $hash
    }
    if (Get-Process -Name DropSpace -ErrorAction SilentlyContinue) { throw 'Close other DropSpace instances before isolated diagnostics.' }

    $launch.stage = 'start-process'
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $exe
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--beta-ui-diagnostics')
    $start.ArgumentList.Add('--beta-ui-output')
    $start.ArgumentList.Add($appOutput)
    $start.ArgumentList.Add('--beta-ui-scenario')
    $start.ArgumentList.Add($Scenario)
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start
    if (-not $child.Start()) { throw 'The diagnostic process did not start.' }
    $launch.processId = $child.Id
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    $launch.stage = 'native-ui-running'
    if (-not $child.WaitForExit($TimeoutSeconds * 1000)) {
        $launch.timedOut = $true
        throw "Native UI diagnostics exceeded $TimeoutSeconds seconds; no completion is claimed."
    }
    $launch.exitCode = $child.ExitCode
    $launch.processExited = $true
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw "The process exited with code $($child.ExitCode) without a diagnostic receipt." }
    $app = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
    $launch.appReceiptSha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-AppEvidenceSummary $app
    if ($app.status -ceq 'blocked') { $launch.status = 'blocked' }
    if ($child.ExitCode -ne 0 -or $app.status -cne 'passed' -or $app.visualStatus -cne 'passed' -or
        $app.selectedScenario -cne $Scenario -or $app.plannedCaseCount -ne $expectedScenarios.Count -or
        $app.actualCaseCount -ne $expectedScenarios.Count -or $app.passedCaseCount -ne $expectedScenarios.Count -or
        @($app.scenarios).Count -ne $expectedScenarios.Count -or @($app.exceptions).Count -ne 0 -or @($app.captureFailures).Count -ne 0) {
        throw "Native UI diagnostics did not completely pass: status=$($app.status), visual=$($app.visualStatus), exit=$($child.ExitCode)."
    }
    foreach ($name in $expectedScenarios) {
        if (@($app.scenarios | Where-Object { $_.name -ceq $name -and $_.status -ceq 'passed' }).Count -ne 1) {
            throw "The diagnostic receipt did not complete the expected scenario: $name"
        }
    }
    if (@($app.captures).Count -ne $expectedCaptures.Count) { throw "The diagnostic receipt does not contain the $($expectedCaptures.Count) native captures required for '$Scenario'." }
    foreach ($file in $expectedCaptures) {
        if (@($app.captures | Where-Object { $_.file -ceq $file }).Count -ne 1) { throw "Missing or repeated native capture: $file" }
    }
    foreach ($capture in $app.captures) {
        if ([IO.Path]::GetFileName($capture.file) -cne $capture.file -or $capture.file -cnotlike '*.png') { throw 'Invalid capture filename.' }
        $image = Join-Path $appOutput $capture.file
        if (-not (Test-Path -LiteralPath $image -PathType Leaf) -or (Get-Item -LiteralPath $image).Length -le 100 -or
            $capture.pixelWidth -le 0 -or $capture.pixelHeight -le 0 -or $capture.pixelReadbackPassed -ne $true) { throw 'A native pixel capture is missing, empty or failed content validation.' }
        $hash = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $capture.sha256) { throw 'A native capture differs from its recorded bytes.' }
    }
    $launch.status = 'passed'
}
catch {
    $launch.failure = $_.Exception.Message
}
finally {
    if ($null -ne $child) {
        try {
            if (-not $child.HasExited) {
                $child.Kill($true)
                if (-not $child.WaitForExit(10000)) { throw 'The owned diagnostic process did not exit after termination.' }
            }
            $launch.exitCode = $child.ExitCode
            $launch.processExited = $true
        }
        catch {
            $launch.status = 'failed'
            $launch.failure = "$($launch.failure) Cleanup: $($_.Exception.Message)"
        }
    }
    foreach ($stream in @(@{ task = $stdout; file = 'stdout.txt' }, @{ task = $stderr; file = 'stderr.txt' })) {
        if ($null -ne $stream.task) {
            try {
                if (-not $stream.task.Wait(5000)) { throw 'Output stream did not close.' }
                $stream.task.Result | Set-Content (Join-Path $output $stream.file) -Encoding utf8
            }
            catch { $_.Exception.Message | Set-Content (Join-Path $output $stream.file) -Encoding utf8 }
        }
    }
    if ($launch.timedOut -or -not (Test-Path -LiteralPath $receiptPath)) {
        if (Test-Path -LiteralPath $stagePath -PathType Leaf) {
            try {
                $lastStage = Get-Content -LiteralPath $stagePath -Raw | ConvertFrom-Json -AsHashtable
                Copy-AppEvidenceSummary $lastStage
            }
            catch { $launch.failure += ' The last stage receipt could not be read.' }
        }
    }
    # Both passed and failed runs may retain useful attachments. Transport never changes
    # the scenario result, original failure, expected RTB captures, or visual status.
    try { Export-SupplementalImageLog }
    catch { $launch.supplementalImageLog = @(@{ status = 'skipped'; reason = 'supplemental-export-error: ' + (Limit-SummaryText $_.Exception.Message) }) }
    $launch.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $launch | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'launcher.json') -Encoding utf8
    # Small metadata summary; optional bounded PNG chunks have their own explicit marker.
    $summary = [ordered]@{}
    foreach ($key in $launch.Keys) {
        if ($key -ne 'scenarios') { $summary[$key] = $launch[$key] }
    }
    $summary.failure = Limit-SummaryText $launch.failure
    $summary.scenarios = @($launch.scenarios | Select-Object -First 2 | ForEach-Object {
        [ordered]@{ name = $_.name; status = $_.status; failureStage = $_.failureStage; failure = (Limit-SummaryText $_.failure) }
    })
    $summary.captureFailures = @($launch.captureFailures | Select-Object -First 8 | ForEach-Object { Limit-SummaryText $_ })
    $summary.exceptions = @($launch.exceptions | Select-Object -First 8 | ForEach-Object { Limit-SummaryText $_ })
    $summary.captures = @($launch.captures | Select-Object -First 16)
    $summary.observations = @($launch.observations | Select-Object -Last 6 | ForEach-Object { Limit-Observation $_ })
    $summary.supplementalCaptures = @($launch.supplementalCaptures | ForEach-Object { Get-SupplementalSummary $_ })
    # Four allowed attachments may each report capture and evidence-write failure.
    $summary.supplementalCaptureFailures = @($launch.supplementalCaptureFailures | Select-Object -First 8 | ForEach-Object { Get-SupplementalSummary $_ })
    $summary.supplementalCaptureFailuresTruncated = $launch.supplementalCaptureFailures.Count -gt 8
    $summary.visualCheckpoints = @($launch.visualCheckpoints | Select-Object -First 8 | ForEach-Object { Get-VisualCheckpointSummary $_ })
    $summary.visualCheckpointsTruncated = $launch.visualCheckpoints.Count -gt 8
    Write-Output ('DROPSPACE_BETA_UI_RECEIPT=' + ($summary | ConvertTo-Json -Depth 10 -Compress))
    if ($null -ne $child) { $child.Dispose() }
    Pop-Location
}
if ($launch.status -cne 'passed') { throw $launch.failure }
