# Frozen 8 Core + 2 Infrastructure tests. Manual suites are separate.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if ($env:GITHUB_EVENT_NAME -cne 'pull_request') { throw 'Passive focused cases require PR event.' }
$results=Join-Path $env:RUNNER_TEMP 'dropspace-passive-focused'
New-Item $results -ItemType Directory -Force | Out-Null
$runs=@(
  @{ Project='tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj'; Filter='FullyQualifiedName~IslandContentPriorityTests|FullyQualifiedName~IslandAppearanceSettingsTests'; Trx='passive-core.trx' },
  @{ Project='tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj'; Filter='FullyQualifiedName=DropSpace.Infrastructure.Tests.LyricsRecoveryRegressionTests.CompletedNoMatchIsDistinctFromAllProviderFailures'; Trx='passive-infrastructure.trx' }
)
foreach($item in $runs){
  & dotnet test $item.Project -c Release --no-restore -p:Platform=x64 --filter $item.Filter --results-directory $results --logger "trx;LogFileName=$($item.Trx)"
  if($LASTEXITCODE -ne 0){throw "Bounded passive cases failed: $($item.Project)"}
}
& node scripts/passive-test-budget.mjs verify $results
if($LASTEXITCODE -ne 0){throw 'Passive test result/count verification failed'}
