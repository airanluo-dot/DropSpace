import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export function checkReleaseMetadata({ tag, packages, notices, readme, roadmap, notes }) {
  const sdk = packages.match(/<PackageVersion\b(?=[^>]*\bInclude="Microsoft\.WindowsAppSDK")(?=[^>]*\bVersion="([^"]+)")[^>]*\/>/)?.[1];
  assert.ok(sdk, 'Missing Windows App SDK package version');
  assert.equal(notices.match(/Microsoft Windows App SDK ([\w.-]+)/)?.[1], sdk, 'Notice SDK label differs from package');
  assert.equal(notices.match(/nuget\.org\/packages\/Microsoft\.WindowsAppSDK\/([\w.-]+)/)?.[1], sdk, 'Notice SDK link differs from package');
  const section = roadmap.split(/^## /m).find(text => text.startsWith('Release target:'));
  assert.ok(section?.startsWith(`Release target: ${tag}\n`), 'ROADMAP current target differs from RELEASE_VERSION');
  assert.ok(section.includes(`.github/release-notes/${tag}.md`), 'ROADMAP must link the current release evidence');
  if (/reproduced native/i.test(notes)) {
    assert.doesNotMatch(section, /remains under investigation|not been conclusively reproduced/i, 'ROADMAP contradicts reproduced crash evidence');
  }
  const beta = tag.match(/^v\d+\.\d+\.\d+-beta\.(\d+)$/)?.[1];
  if (beta) {
    const baselinePattern = /\b(Beta \d+|v\d+\.\d+\.\d+(?:-(?:beta|preview)\.\d+)?) is the immediate upgrade baseline\b/g;
    const baseline = [...notes.matchAll(baselinePattern)].map(match => match[1]);
    assert.equal(baseline.length, 1, 'Current notes must state one immediate upgrade baseline');
    for (const [name, text] of [['README', readme], ['ROADMAP', roadmap]]) {
      const labels = [...text.matchAll(/The current Beta target is `([^`]+)` \(Beta (\d+)\)/g)];
      assert.equal(labels.length, 1, `${name} must identify one current Beta target`);
      assert.equal(labels[0][1], tag, `${name} has a stale current Beta target`);
      assert.equal(labels[0][2], beta, `${name} Beta display label differs from its tag`);
      const declared = [...text.matchAll(baselinePattern)].map(match => match[1]);
      assert.deepEqual(declared, baseline, `${name} upgrade baseline differs from current notes`);
    }
    const snapshots = [...roadmap.matchAll(/(v\d+\.\d+\.\d+-beta\.\d+)\s+is the current Beta target/g)];
    assert.equal(snapshots.length, 1, 'ROADMAP current snapshot is missing');
    assert.equal(snapshots[0][1], tag, 'ROADMAP current snapshot is stale');
  }
}

export function readMetadata(root) {
  const read = name => fs.readFileSync(path.join(root, name), 'utf8').replace(/\r\n/g, '\n');
  const tag = read('RELEASE_VERSION').trim();
  return { tag, packages: read('Directory.Packages.props'), notices: read('THIRD_PARTY_NOTICES.md'),
    readme: read('README.md'), roadmap: read('ROADMAP.md'), notes: read(`.github/release-notes/${tag}.md`) };
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  checkReleaseMetadata(readMetadata(path.resolve(process.argv[2] ?? path.join(path.dirname(fileURLToPath(import.meta.url)), '..'))));
  console.log('Current release metadata consistency passed.');
}
