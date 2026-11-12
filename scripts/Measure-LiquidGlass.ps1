#requires -Version 7.0
<#
.SYNOPSIS
Measures the Windows island over a moving background using a private diagnostic profile.
.EXAMPLE
./scripts/Measure-LiquidGlass.ps1 -Executable ./DropSpace.exe -OutputDirectory ./glass-after -Tier High -Samples 3 -CheckMovement -CheckLifecycle
.NOTES
Run both builds on the same display with the same tier, scale and island state. Each sample uses
ten seconds of motion. The output contains capture timing and CPU submission times.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Standard','High','VeryHigh','Ultra')][string]$Tier = 'Standard',
    [ValidateRange('NonNegative')][int]$Samples = 2,
    [switch]$Compact,
    [ValidateRange(0.5, 2.0)][double]$Scale = 1,
    [switch]$CheckLifecycle,
    [switch]$CheckMovement
)

$ErrorActionPreference = 'Stop'
$exe = (Get-Item -LiteralPath $Executable).FullName
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
# New-Item without -Force reserves this directory and rejects an existing run.
$null = New-Item -Path $output -ItemType Directory
$tiers = @{ Standard = 0; High = 1; VeryHigh = 2; Ultra = 3 }

function Wait-Diagnostic([scriptblock]$condition, [string]$operation, [int]$timeout = 30) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $condition)) {
        if ($process.HasExited) { throw "Diagnostic exited during $operation. See $run." }
        if ($watch.Elapsed.TotalSeconds -gt $timeout) { throw "Timed out during $operation. See $run." }
        Start-Sleep -Milliseconds 100
    }
}

function Send-Control([string]$name, [string]$receipt) {
    # The diagnostic writes exactly one new state after the command settles. Remove only our
    # previous acknowledgement, so equal timestamps or a slow writer cannot satisfy this wait.
    [IO.File]::Delete($stateFile)
    $temp = Join-Path $run 'control.tmp'
    [IO.File]::WriteAllText($temp, (@{name=$name} | ConvertTo-Json -Compress))
    [IO.File]::Move($temp, (Join-Path $run 'control.json'))
    Wait-Diagnostic {
        try {
            $stream = [IO.File]::Open($stateFile, 'Open', 'Read', 'None')
            try { return $stream.Length -gt 0 } finally { $stream.Dispose() }
        } catch [IO.IOException] { return $false }
    } $name
    if ($receipt) { [IO.File]::Copy($stateFile, (Join-Path $run "$receipt.json")) }
    Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
}

function Assert-Glass($state, [string]$expectedState) {
    if (!$state.material.ready -or !$state.material.visible -or $state.material.requested -ne 'LiquidGlass' -or
        $state.material.refreshRate -ne $Tier -or $state.island.State -ne $expectedState) {
        throw "Expected visible $Tier glass in $expectedState. See $run."
    }
}

function Invoke-Session([string]$session, [bool]$measure, [bool]$lifecycle) {
    $run = Join-Path $output $session
    $stateFile = Join-Path $run 'state.json'
    $seed = Join-Path $output "$session-settings.json"
    @{ IslandAppearance = @{
        MaterialStyle = 1; Resident = !$lifecycle; CompactScale = $Scale; ExpandedScale = $Scale
        LiquidGlassRefreshRate = $tiers[$Tier]; LiquidGlassBlur = 2; LiquidGlassRefraction = 100
        LiquidGlassDispersion = 75; LiquidGlassBlackGradient = $true; LiquidGlassDarkOverlay = 0
        LiquidGlassRefractionWidth = 200
    } } | ConvertTo-Json | Set-Content -LiteralPath $seed -Encoding utf8NoBOM

    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.WorkingDirectory = Split-Path -LiteralPath $exe
    $start.UseShellExecute = $false
    foreach ($arg in '--liquid-glass-check', $run, '--liquid-glass-settings', $seed) {
        $start.ArgumentList.Add($arg)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        Wait-Diagnostic { [IO.File]::Exists((Join-Path $run 'ready.txt')) } 'startup' 50
        Assert-Glass (Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json) 'Expanded'
        [IO.File]::Copy($stateFile, (Join-Path $run 'expanded.json'))
        if ($measure) {
            if ($Compact) { Assert-Glass (Send-Control 'compact' 'compact') 'Compact' }
            for ($i = 0; $i -lt $Samples; $i++) { $null = Send-Control 'measure' }
            $null = Send-Control 'compare'
            $comparison = Get-ChildItem -LiteralPath $run -Filter 'compare-pixels-*.json' -File
            $pixels = Get-Content -LiteralPath $comparison.FullName -Raw | ConvertFrom-Json
            if (!$pixels.identical -or $pixels.changedPixels -ne 0 -or $pixels.comparedBytes -le 0) {
                throw "Pixel comparison failed. See $run."
            }
            if (!$lifecycle) {
                if (!$Compact) { Assert-Glass (Send-Control 'compact' 'compact') 'Compact' }
                Assert-Glass (Send-Control 'expand' 'reexpanded') 'Expanded'
            }
            if ($CheckMovement) {
                $null = Send-Control 'move-check'
                $move = Get-Content -LiteralPath (Join-Path $run 'move-check.json') -Raw | ConvertFrom-Json
                if ([Math]::Abs($move.actualOffset) -ne 32 -or $move.restoredX -ne 0 -or
                    !$move.before.ready -or !$move.moved.ready -or !$move.repeated.ready -or !$move.restored.ready -or
                    $move.moved.frames -ne $move.before.frames + 1 -or $move.repeated.frames -ne $move.moved.frames -or
                    $move.restored.frames -ne $move.repeated.frames + 1) { throw "Movement check failed. See $run." }
            }
        }
        if ($lifecycle) {
            $acrylic = Send-Control 'acrylic' 'acrylic'
            if ($acrylic.island.State -ne 'Expanded' -or !$acrylic.material.visible -or
                $acrylic.material.requested -ne 'Acrylic' -or $acrylic.material.ready) { throw "Acrylic switch failed. See $run." }
            Assert-Glass (Send-Control 'glass' 'glass-restored') 'Expanded'
            $hidden = Send-Control 'hide'
            $dismissal = [Diagnostics.Stopwatch]::StartNew()
            while ($hidden.island.State -eq 'Dismissing' -and $dismissal.Elapsed.TotalSeconds -lt 5) {
                $hidden = Send-Control 'snapshot'
            }
            [IO.File]::Copy($stateFile, (Join-Path $run 'hidden.json'))
            if ($hidden.island.State -ne 'Hidden' -or $hidden.material.ready -or $hidden.material.visible) {
                throw "Hidden island retained glass. See $run."
            }
            Assert-Glass (Send-Control 'expand' 'reopened') 'Expanded'
        }
    } finally {
        try {
            if ([IO.Directory]::Exists($run) -and !$process.HasExited) {
                [IO.File]::WriteAllText((Join-Path $run 'finish.txt'), 'done')
                if (!$process.WaitForExit(20000)) { throw "Diagnostic PID $($process.Id) did not exit. See $run." }
            }
            if ($process.HasExited) { $exitCode = $process.ExitCode }
        } finally { $process.Dispose() }
    }
    $closed = Get-Content -LiteralPath (Join-Path $run 'exit.json') -Raw | ConvertFrom-Json
    if ($exitCode -ne 0 -or $closed.status -ne 'closed') { throw "Diagnostic shutdown failed. See $run." }
    if ($measure) {
        $files = @(Get-ChildItem -LiteralPath $run -Filter 'refresh-*.json' -File | Sort-Object Name)
        if ($files.Count -ne $Samples) { throw "Expected $Samples measurement files. See $run." }
        foreach ($file in $files) {
            $s = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if ($s.capturedFrames -le 0 -or $s.renderedFrames -le 0) { throw "Empty capture in $file." }
            [pscustomobject]@{
                File = $file.FullName; TargetHz = $s.targetHz; CaptureFps = $s.captureFps
                AgeP95Ms = $s.captureAgeAtReadMs.p95; LatestFrameWaitP95Ms = $s.latestFrameWaitMs.p95
                CaptureIntervalP95Ms = $s.consumedCaptureIntervalMs.p95; PresentCpuP95Ms = $s.presentCpuMs.p95
            }
        }
    }
}

# The empty compact island needs residency. The hide/reopen check needs a nonresident island.
$separateLifecycle = $Compact -and $CheckLifecycle
Invoke-Session 'run' $true ($CheckLifecycle -and !$separateLifecycle)
if ($separateLifecycle) { Invoke-Session 'lifecycle' $false $true }
