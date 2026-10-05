# Beta11 packaging readiness (preparation only)

This lane inspected implementation head `c9611fe` after fetching the remote, then rechecked
`983deab` (the added native-crash audit does not change the packaging scripts reviewed here).
No tag, release, merge, Windows workflow dispatch or native engine build was performed.
RELEASE_VERSION, README/ROADMAP and approval remain Beta10 deliberately. This is not a
Beta11 release approval. The machine-readable snapshot is
[beta11-release-readiness-evidence.json](beta11-release-readiness-evidence.json).

## Resolved packaging issues

- Added the selection-role model catalog to the code-owned shipping fingerprint. The existing
  production-source coverage test reproduced its omission before the fix.
- Bound Core.csproj as well, because its embedded artist-folding dictionary/license declarations
  determine what actually ships. Changing these declarations now invalidates approval.
- Replaced the old hard-coded Beta25 mismatch in Test-ReleaseArtifactVersion.ps1 with the actual
  lifecycle baseline. Beta11's final EXEs must reject the Beta10 identity. Version-policy checks
  now cover Beta11's `0.3.1.11` file/package version and `0.3.1-beta.10` upgrade baseline.

## Actual checks

Linux cloud, Node 24.19.0, temporary PowerShell 7.6.6; no .NET SDK or Windows desktop:

```sh
node --test scripts/test-ai-release-approval.test.mjs scripts/test-ai-runtime-publication.test.mjs scripts/ci-release-promotion.test.mjs scripts/test-release-metadata.test.mjs scripts/ci-portable-handoff.test.mjs
pwsh -NoProfile -Command './scripts/Test-ReleaseVersion.ps1; ./scripts/Test-ReleaseConsistency.ps1'
cd website/_source
node --test scripts/release-contract.test.mjs scripts/sync-releases.test.mjs scripts/verify-published-release.test.mjs
```

435/435 first-command tests passed (zero skipped); website contracts 18/18 passed. Version and
consistency scripts passed for current Beta10 metadata. Artifact validator syntax parsed;
its real PE checks were not run. Initial run had three failures: the missing catalog and two
missing-pwsh failures. PowerShell was installed under /tmp, without weakening or skipping tests.

The existing public Beta10 baseline was also read-only verified, after configuring Node's
environment proxy to resolve the initial direct-connection refusal:

```sh
NODE_USE_ENV_PROXY=1 node website/_source/scripts/verify-published-release.mjs v0.3.1-beta.10 0 live
```

It verified GitHub Release, all five public assets, manifest/checksum agreement, official release
and latest-change APIs, and the homepage. This verifies feed metadata/URLs, not downloaded
installer execution, every asset's bytes, or a future Beta11 deployment.

## Runtime reuse and blockers

The current source fingerprint is `632143db4c5af63755b7443d2b8874fd60cd235cea6224c557c89bd2552997a4`;
the old approval fingerprint is `f27174084c942c14e916ccbcb6ae3bd82ffb4a5b30c01feef1488a474ba86ab7`.
There are 33 changed/new bound paths after this fix. Recompute at the final integrated revision.
The current resident source digest is
`64230bcee62d9b4598734a784af7b8285209189ad3c20dd8f4820cd48636c0c6`, versus approved
`9b6ddee0fb3238bf3341755a2f71ee76ccd82290118146a2a6328ce7e06276a2`.
validateApproval currently fails with the stale-input error, as required.

The Beta10 approval is owner-accepted experimental, not semantic approval. Its review expires
2026-10-11T18:03:24Z; neither expiry nor that old owner decision covers Beta11. The user's
requirement for working AI selection and fixed crashes is not satisfied by copying old review
hashes or by prepare-corrective-release's reviewed-source override. Runtime/model-role/selector
changes need actual qualification bound to the final inputs; unchanged-model preparation cannot
manufacture it. Current Qwen diagnostic reports are technical observations, not an integrated
shipping profile or Windows/GPU product approval.

Read-only Actions metadata on 2026-10-05 confirmed the old reviewed artifact 11264582019 is
unexpired through 2026-12-02, with its expected archive digest. The selection diagnostic runtime
11316610580 from successful run 37235696457 is also unexpired; its name is
`ai-selection-runtime-37235696457-1`, which intentionally fails the formal publication naming
contract. Its successful workflow is not evidence of successful AI choices: all three original
500 ms cases returned no complete answer. No archive bytes were downloaded/qualified in this lane.

CI and release use Get-ReviewedAiRuntime.ps1 and require valid approval before consuming exact
runtime bytes; no automatic engine rebuild fallback exists. Both will currently stop on stale
approval. The old artifact contains EXEs/license/manifest, no trusted linkable build tree.
Unchanged completion/tokenizer bytes may be reused only through the existing verified legacy
route. Check existing candidate producer artifacts before rebuilding any worker. Never rebuild
full engine targets merely to obtain a renamed artifact. A new final worker, if needed, belongs
to the explicit nonpublishing producer and must retain actual source/toolchain/provenance.

Dependencies inspected: .NET 10.0.401; Windows App SDK 2.5.1; Windows SDK compile build 26100,
minimum build 20348; locked/audited NuGet graph; pinned Inno Setup 7.0.2; pinned llama.cpp
7fe450e19305b828c199d602c23a8337aaa1f03b. No dependency bump is proposed.

## Executable release checklist for the integrating task

1. Integrate fixes and freeze a candidate revision. Clear the actual AI-selection and native-crash
   blockers with retained evidence. Do not convert fallback, diagnosis or skipped tests into passes.
2. Prepare coherent Beta11 RELEASE_VERSION/README/ROADMAP/release notes only once behavior is
   established; notes must name `v0.3.1-beta.10 is the immediate upgrade baseline`. Recompute
   `node scripts/test-ai-release-approval.mjs --print-scope`; retain the exact ordered source hashes.
3. Bind current release approval/review/runtime manifest/archive inventory and producer identity
   to the final source and actual qualified runtime. Run `node scripts/test-ai-release-approval.mjs`.
   Existing Beta6–10 exact-version CI waivers exclude Beta11; do not extend them. Do not dispatch
   ordinary packaging while its runtime-consumption gate is known to fail.
4. Run one ordinary Windows CI at the final candidate (PR or nonpublishing workflow_dispatch).
   It restores locked dependencies, retrieves exact reviewed runtime, runs policy/App tests,
   builds WinUI/portable/Inno/MSIX/identity, runs installer install/upgrade/uninstall lifecycle,
   and portable en-US/zh-CN smoke. Require executed step outcomes and real diagnosis for failures.
   Inspect runtime inventories in installer/portable/MSIX and retain that attempt's promotion bundle.
5. On these exact artifacts run Test-ReleaseArtifactVersion.ps1, New-UpdateManifest.ps1 and
   Test-UpdateManifest.ps1. Check semantic/file/package versions, summary/channel/VersionCode,
   minimum Windows build, final EXE size/SHA256 and the exact three SHA256SUMS entries. Installer
   lifecycle must actually install Beta10 and upgrade its state/data to Beta11, then test both
   uninstall modes on an isolated Windows runner. This cannot be established on Linux.
6. Root release coordination may later integrate into main and review the exact commit/tree.
   CI promotion is reusable only for an identical checkout tree, successful run/attempt, exact
   artifact/receipt and unchanged payload hashes; its bundle lasts seven days. Metadata, notes,
   fingerprints and approval must already be in that tree, so no post-CI edits before promotion.
   The eventual publication action must use `publish=true` with that exact expected_commit,
   remain on main and pass the release gate. This lane does not authorize or execute it.
7. After publication is separately authorized and performed, authoritative website sync must
   succeed; then run `node website/_source/scripts/verify-published-release.mjs v0.3.1-beta.11 1200`
   against actual public assets/APIs/homepage. Stable latest must remain unchanged for this Beta.

Outstanding: final AI/runtime qualification and approval, crash resolution, coherent Beta11
metadata, final Windows build/PE/install/upgrade/UI smoke, actual GPU coverage, final artifact
manifest/hash inspection, and future Beta11 public-feed validation. None is claimed passed here.
