param([switch]$UninstallConcurrencyOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
& (Join-Path $PSScriptRoot 'Prepare-LifecycleFixtures.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Fixture preparation failed.' }
$catalog = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/lifecycle-fixtures/catalog.json'))
$project = Join-Path $PSScriptRoot 'FeatureModuleProbe.csproj'
dotnet build $project -c Release "-p:ProbeCatalogFile=$catalog" -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Focused production-linked probe build failed.' }
$probe = Join-Path $PSScriptRoot 'bin/Release/net10.0/DropSpace.FeatureModuleProbe.exe'
$cases = @('uninstall-crash','cleanup-retry','update-cleanup','capabilities','dependency-order',
    'dependency-corrupt','dependency-cycle','dependency-race','job-stop','job-parent-crash')
if ($UninstallConcurrencyOnly) { $cases = @('uninstall-withdrawal','uninstall-dependent-rejection') }
$failed = @()
foreach ($case in $cases) {
    & $probe --lifecycle-check $case --fixtures (Split-Path -Parent $catalog)
    if ($LASTEXITCODE -ne 0) { $failed += $case }
}
if (!$UninstallConcurrencyOnly) {
& $probe --case 1 --root (Join-Path $PSScriptRoot 'artifacts/lifecycle-empty') --evidence (Join-Path $PSScriptRoot 'artifacts/lifecycle-empty.json')
if ($LASTEXITCODE -ne 0) { $failed += 'empty-modules' }
}
if ($env:GITHUB_STEP_SUMMARY) {
    $selection = if ($UninstallConcurrencyOnly) { '2 new uninstall concurrency scenarios / 2 functional cases; prior 15 cases reused from run 37748231676 on 7a0fe75, not rerun or claimed on this head' } else { '11 scenarios / 15 functional cases' }
    "Official module lifecycle probes: $selection; failed scenarios: $($failed.Count). Production-linked Windows automation with test-only pinned fixtures. No installed App/UI or user-machine verification." >> $env:GITHUB_STEP_SUMMARY
}
if ($failed.Count) { throw "Focused lifecycle failures: $($failed -join ', ')" }
