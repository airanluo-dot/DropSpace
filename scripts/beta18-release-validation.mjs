// Read the existing Beta18 receipt in ci-release-promotion without rebuilding its historical test projects.
import assert from 'node:assert/strict';

const focusedFilter = 'FullyQualifiedName~IslandContentPriorityTests|FullyQualifiedName~IslandAppearanceSettingsTests';
export const focusedCaseCount = 8;
export const originalSuiteCaseCount = 2124;
export const focusedProject = 'tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj';
export const infrastructureFilter = 'FullyQualifiedName=DropSpace.Infrastructure.Tests.LyricsRecoveryRegressionTests.CompletedNoMatchIsDistinctFromAllProviderFailures';
export const infrastructureCaseCount = 2;
export const infrastructureProject = 'tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj';
export const focusedRuns = Object.freeze([
  Object.freeze({id:'core',project:focusedProject,filter:focusedFilter,caseCount:focusedCaseCount,trx:'focused.trx',buildLog:'core-build.txt'}),
  Object.freeze({id:'infrastructure',project:infrastructureProject,filter:infrastructureFilter,caseCount:infrastructureCaseCount,trx:'focused-infrastructure.trx',buildLog:'infrastructure-build.txt'}),
]);
export const focusedUnitCaseCount = focusedCaseCount + infrastructureCaseCount;
export function verifyFocusedResults(xml,expectedCaseCount=focusedCaseCount) {
  assert.ok(focusedRuns.some(run=>run.caseCount===expectedCaseCount),'Unreviewed focused execution count');
  const summaries=xml.match(/<Counters\b[^>]*\/>/g);
  assert.equal(summaries?.length,1,'One real TRX summary required');
  const counts=Object.fromEntries([...summaries[0].matchAll(/(\w+)="(\d+)"/g)].map(([,key,value])=>[key,Number(value)]));
  assert.equal(counts.total,expectedCaseCount,'Actual execution count differs from reviewed focused case count');
  assert.equal(counts.executed,expectedCaseCount);
  assert.equal(counts.passed,expectedCaseCount,'Focused failures block publication');
  assert.equal(counts.failed,0);
  assert.equal(counts.notExecuted,0,'Skipped cases cannot qualify final packages');
  return {total:expectedCaseCount,passed:expectedCaseCount};
}
