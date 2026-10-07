import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const workflow = fs.readFileSync(new URL('../.github/workflows/release.yml', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
const publishJob = workflow.slice(workflow.indexOf('\n  publish-release:'));

function step(name) {
  const marker = `      - name: ${name}\n`;
  const start = publishJob.indexOf(marker);
  assert.ok(start >= 0, `Missing publication step: ${name}`);
  const end = publishJob.indexOf('\n      - name:', start + marker.length);
  return publishJob.slice(start, end < 0 ? undefined : end);
}

const publishName = 'Publish immutable Stable or Beta release';

test('release action stays pinned to the reviewed Node 24 v3.0.3 commit', () => {
  const action = step(publishName).match(/^        uses: (.+)$/m)?.[1];
  assert.equal(action, 'softprops/action-gh-release@efb35369e0ad2afab669f228072c1b0d510eae64 # v3.0.3');
  assert.equal((workflow.match(/uses: softprops\/action-gh-release@/g) ?? []).length, 1);
});

test('release action retains every metadata input and the exact conditional asset list', () => {
  // Reviewed against v3.0.3 action.yml, src/util.ts, src/run.ts and src/github.ts.
  // Keep the existing publication contract; changing the action runtime must not
  // change tags, channel/latest policy, release notes or upload selection.
  const inputs = step(publishName).split('        with:\n')[1]?.trimEnd();
  assert.equal(inputs, [
    '          tag_name: ${{ needs.validate-release.outputs.tag }}',
    '          target_commitish: ${{ github.sha }}',
    '          name: DropSpace ${{ needs.validate-release.outputs.tag }}',
    '          body_path: .github/release-notes/${{ needs.validate-release.outputs.tag }}.md',
    '          prerelease: ${{ needs.validate-release.outputs.prerelease }}',
    '          draft: false',
    '          make_latest: ${{ needs.validate-release.outputs.make_latest }}',
    '          fail_on_unmatched_files: true',
    '          files: |',
    '            artifacts/release/DropSpaceSetup.exe',
    '            artifacts/release/DropSpace.exe',
    '            artifacts/release/DropSpace-x64.msix',
    '            artifacts/release/SHA256SUMS.txt',
    '            artifacts/release/update-manifest.json',
    "            ${{ contains(fromJSON('[\"v0.3.1-beta.17\",\"v0.3.1-beta.18\"]'), needs.validate-release.outputs.tag) && 'artifacts/release/runtime-publication.json' || '' }}",
    "            ${{ contains(fromJSON('[\"v0.3.1-beta.17\",\"v0.3.1-beta.18\"]'), needs.validate-release.outputs.tag) && 'artifacts/release/cuda-runtime-download.json' || '' }}",
    "            ${{ contains(fromJSON('[\"v0.3.1-beta.17\",\"v0.3.1-beta.18\"]'), needs.validate-release.outputs.tag) && 'artifacts/release/cuda-runtime-manifest.json' || '' }}",
  ].join('\n'));
});

test('publication remains manual, opt-in, owner-authorized and validation-gated', () => {
  const triggers = workflow.match(/^on:\n([\s\S]*?)(?=^[^\s#])/m)?.[1];
  assert.ok(triggers, 'Missing workflow triggers');
  assert.deepEqual([...triggers.matchAll(/^  ([\w_]+):/gm)].map(match => match[1]), ['workflow_dispatch']);
  assert.match(triggers, /    inputs:\n[\s\S]*?      publish:\n[\s\S]*?        default: false\n/);
  const condition = publishJob.match(/^    if: >-\n([\s\S]*?)(?=^    \S)/m)?.[1];
  assert.equal(condition?.trim(), [
    'always() &&',
    "      github.event_name == 'workflow_dispatch' &&",
    '      inputs.publish == true &&',
    "      github.ref == 'refs/heads/main' &&",
    "      (github.actor == github.repository_owner || github.actor == 'github-actions[bot]') &&",
    "      needs.validate-release.result == 'success' &&",
    "      needs.validate-release.outputs.ai_publication_authorized == 'true' &&",
    "      (needs.validate-release.outputs.signing_required != 'true' || needs.sign-release.result == 'success')",
  ].join('\n'));
  assert.match(publishJob, /^    needs: \[validate-release, sign-release\]$/m);
});

test('existing-release rejection and AI recheck remain before publication', () => {
  const rejectName = 'Reject an existing tag or release';
  const recheckName = 'Recheck AI publication decision';
  const reject = step(rejectName);
  assert.match(reject, /set -euo pipefail/);
  for (const endpoint of ['releases/tags', 'git/ref/tags']) {
    assert.ok(reject.includes(`gh api "repos/\${GITHUB_REPOSITORY}/${endpoint}/\${tag}"`));
  }
  assert.equal((reject.match(/^            exit 1$/gm) ?? []).length, 2);
  assert.match(step(recheckName), /run: node scripts\/test-ai-release-approval\.mjs --release-bundle artifacts\/release/);
  const positions = [rejectName, recheckName, publishName].map(name => publishJob.indexOf(`- name: ${name}\n`));
  assert.ok(positions[0] < positions[1] && positions[1] < positions[2]);
  assert.doesNotMatch(publishJob, /continue-on-error: true/);
});

test('website refresh and end-to-end verification remain after publication', () => {
  const refreshName = 'Refresh official website release metadata';
  const verifyName = 'Verify published Release and official website end to end';
  assert.match(step(refreshName), /run: gh workflow run deploy-website\.yml --ref main/);
  assert.ok(step(verifyName).includes('run: node website/_source/scripts/verify-published-release.mjs "${{ needs.validate-release.outputs.tag }}" 1200'));
  const positions = [publishName, refreshName, verifyName].map(name => publishJob.indexOf(`- name: ${name}\n`));
  assert.ok(positions[0] < positions[1] && positions[1] < positions[2]);
});
