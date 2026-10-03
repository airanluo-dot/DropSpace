import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
const workflow = fs.readFileSync(new URL('../.github/workflows/release.yml', import.meta.url), 'utf8');
const harness = fs.readFileSync(new URL('./plain-hy-production-evidence/Run-WindowsProductionEvidence.ps1', import.meta.url), 'utf8');
function step(name) {
  const start = workflow.indexOf(`      - name: ${name}\n`);
  assert.notEqual(start, -1, `Missing step: ${name}`);
  const end = workflow.indexOf('\n      - name:', start + 1);
  return { start, text: workflow.slice(start, end < 0 ? undefined : end) };
}
test('generic evidence capture precedes RID publication while retaining exact runtime prerequisite', () => {
  const order = [
    'Upload runtime for isolated candidate diagnostics',
    'Test persistence, manifests, downloads, and update coordination',
    'Test production evidence harness without native inference',
    'Download pinned models for native AI smoke',
    'Validate native offline inference and cancellation',
    'Capture final plain-Hy production evidence on both runtime variants',
    'Publish portable self-contained EXE',
    'Build unsigned MSIX',
  ].map(name => step(name).start);
  assert.deepEqual(order, [...order].sort((a, b) => a - b));
  assert.match(harness, /dotnet restore \$project -p:RestoreLockedMode=true/);
  assert.doesNotMatch(harness, /RestoreLockedMode=false|--force-evaluate/);
});
test('a failed evidence variant stops subsequent model launches and preserves failure evidence', () => {
  const capture = step('Capture final plain-Hy production evidence on both runtime variants').text;
  assert.match(capture, /foreach \(\$variant in @\('Baseline', 'Avx2'\)\)/);
  assert.match(capture, /\$captureSucceeded = \$\?\s+\$captureExitCode = \$LASTEXITCODE\s+if \(-not \$captureSucceeded -or \$captureExitCode -ne 0\) \{ throw/);
  assert.match(capture, /inputs\.publish != true/);
  assert.match(step('Preserve final production evidence including failures').text, /if: always\(\)/);
  assert.match(step('Preserve final production evidence including failures').text, /retention-days: 60/);
});
