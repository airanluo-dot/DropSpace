# Launches only the bounded native timer and batch-selection entry point, never the broad smoke suite.
[CmdletBinding()]
param(
    [string]$BuildReceiptPath = 'artifacts/beta-ui-diagnostics/build/build-receipt.json',
    [string]$OutputDirectory = 'artifacts/beta-ui-diagnostics/run',
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 240
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native Beta UI diagnostics require Windows.' }
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
    visualStatus = 'not-run'; scenarios = @(); captures = @(); captureFailures = @(); exceptions = @(); environment = $null; failure = $null
    releasePromotionEligible = $false; physicalPointerValidated = $false; appReceiptSha256 = $null
}
function Copy-AppEvidenceSummary([System.Collections.IDictionary]$AppReceipt) {
    $launch.stage = $AppReceipt.stage
    $launch.failureStage = $AppReceipt.failureStage
    $launch.actualCaseCount = $AppReceipt.actualCaseCount
    $launch.passedCaseCount = $AppReceipt.passedCaseCount
    $launch.visualStatus = $AppReceipt.visualStatus
    $launch.environment = $AppReceipt.environment
    $launch.scenarios = @($AppReceipt.scenarios)
    $launch.captureFailures = @($AppReceipt.captureFailures)
    $launch.exceptions = @($AppReceipt.exceptions)
    $launch.captures = @($AppReceipt.captures | ForEach-Object {
        [ordered]@{ file = $_.file; sha256 = $_.sha256; width = $_.pixelWidth; height = $_.pixelHeight; pixelReadbackPassed = $_.pixelReadbackPassed }
    })
}
function Limit-SummaryText([object]$Value) {
    if ($null -eq $Value) { return $null }
    $text = [string]$Value
    if ($text.Length -gt 1500) { return $text.Substring(0, 1500) + ' [truncated; see artifact]' }
    return $text
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
        $app.plannedCaseCount -ne 2 -or $app.actualCaseCount -ne 2 -or $app.passedCaseCount -ne 2 -or
        @($app.scenarios).Count -ne 2 -or @($app.exceptions).Count -ne 0 -or @($app.captureFailures).Count -ne 0) {
        throw "Native UI diagnostics did not completely pass: status=$($app.status), visual=$($app.visualStatus), exit=$($child.ExitCode)."
    }
    foreach ($name in @('timer-menu-retirement', 'batch-collapse-selection')) {
        if (@($app.scenarios | Where-Object { $_.name -ceq $name -and $_.status -ceq 'passed' }).Count -ne 1) {
            throw "The diagnostic receipt did not complete the expected scenario: $name"
        }
    }
    $expectedCaptures = @('timers-initial.png', 'timer-menu-FocusTimer.png', 'timer-menu-Countdown.png',
        'timers-reopened.png', 'batch-expanded-selected.png', 'batch-collapsed-selection.png')
    if (@($app.captures).Count -ne $expectedCaptures.Count) { throw 'The diagnostic receipt does not contain all six native captures.' }
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
    $launch.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $launch | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'launcher.json') -Encoding utf8
    # Small metadata only: PNG bytes and large visual-tree observations remain artifacts.
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
    Write-Output ('DROPSPACE_BETA_UI_RECEIPT=' + ($summary | ConvertTo-Json -Depth 6 -Compress))
    if ($null -ne $child) { $child.Dispose() }
    Pop-Location
}
if ($launch.status -cne 'passed') { throw $launch.failure }
