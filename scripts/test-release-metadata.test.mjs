import assert from 'node:assert/strict';
import test from 'node:test';
import { checkReleaseMetadata, readMetadata } from './test-release-metadata.mjs';
import { fileURLToPath } from 'node:url';
const current = readMetadata(fileURLToPath(new URL('../', import.meta.url)));
test('current metadata agrees', () => checkReleaseMetadata(current));
// A fixed historical fixture keeps regression tests independent of later release numbering.
const good = {
  tag: 'v0.3.0-beta.32',
  packages: '<PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.5.1" />',
  notices: 'Microsoft Windows App SDK 2.5.1 https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1',
  readme: 'The current Beta target is `v0.3.0-beta.32` (Beta 32). Beta 31 is the immediate upgrade baseline.',
  roadmap: '## Release target: v0.3.0-beta.32\nThe crash was reproduced and fixed. [Evidence](.github/release-notes/v0.3.0-beta.32.md)\n## Snapshot\nv0.3.0-beta.32\nis the current Beta target.\nThe current Beta target is `v0.3.0-beta.32` (Beta 32). Beta 31 is the immediate upgrade baseline.',
  notes: 'Beta 31 is the immediate upgrade baseline. A regression reproduced native 0xc0000409.',
};
test('regression fixture is valid', () => checkReleaseMetadata(good));
for (const [name, field, before, after] of [
  ['stale SDK label', 'notices', 'Windows App SDK 2.5.1', 'Windows App SDK 2.3.1'],
  ['stale SDK link', 'notices', 'Microsoft.WindowsAppSDK/2.5.1', 'Microsoft.WindowsAppSDK/2.3.1'],
  ['wrong Beta display number', 'readme', '(Beta 32)', '(Beta 31)'],
  ['wrong README baseline', 'readme', 'Beta 31 is the immediate', 'Beta 30 is the immediate'],
  ['wrong ROADMAP baseline', 'roadmap', 'Beta 31 is the immediate', 'Beta 28 is the immediate'],
  ['stale ROADMAP snapshot', 'roadmap', 'v0.3.0-beta.32\nis the current', 'v0.3.0-beta.29\nis the current'],
  ['stale ROADMAP naming', 'roadmap', 'target is `v0.3.0-beta.32`', 'target is `v0.3.0-beta.29`'],
  ['contradictory crash status', 'roadmap', 'crash was reproduced', 'crash remains under investigation; it was reproduced'],
  ['wrong evidence link', 'roadmap', '.github/release-notes/v0.3.0-beta.32.md', '.github/release-notes/v0.3.0-beta.31.md'],
]) {
  test(name, () => {
    assert.ok(good[field].includes(before), 'Mutation must actually change the fixture');
    assert.throws(() => checkReleaseMetadata({ ...good, [field]: good[field].replace(before, after) }));
  });
}
test('historical delivered milestones remain valid', () => {
  checkReleaseMetadata({ ...good, roadmap: good.roadmap + '\n## Delivered slice: v0.3.0-beta.12\nHistorical evidence remains unchanged.\n' });
});
