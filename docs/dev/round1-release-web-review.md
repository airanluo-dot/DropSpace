# Round 1: release, web, Worker and root configuration review

Reviewed 2026-10-01 against the active working tree (including pre-existing uncommitted website work). Read AGENTS.md, the canonical dropspace-maintainer skill and website API guide. This pass made no implementation, visual, release, deployment, remote, or commit changes. Test output and reproductions went to /tmp; the existing website Node tests also regenerated ignored dist-static output.

## Confirmed findings

### R1-WEB-01 — P2: expired upload can permanently block a valid retry

Location: `share-worker/src/index.js:125-146, 694-709`; related test: `share-worker/test/upload-ownership.test.mjs` final test.

Reproduced sequence:
1. Upload A owns a reservation. Its storage write is delayed past RESERVATION_TTL_MS.
2. Retry B reserves the same object and passes its R2 head check.
3. A's delayed storage write completes, retaining A's uploadReservationId.
4. A rollback sees B pending and skips cleanup to protect B.
5. B's conditional create fails because A's bytes exist. B rollback only deletes a matching B-owned object, so it leaves A's bytes behind.
6. The next valid retry also receives 409. The object remains in storage without any committed coordinator entry.

Deterministic local reproduction `/tmp/round1-worker-orphan.mjs` printed `retry 409`, `next retry 409`, `orphan persists true committed undefined`. It uses the production Worker/coordinator with serialized in-memory storage and simulated R2 create-only writes; it performs no remote requests. Existing regression coverage manually deletes the stale object as a lifecycle event before releasing B, so it never exercises the no-lifecycle-cleanup case.

Suggested repair: coordinator-owned stale-object reconciliation or reservation-specific storage keys, preserving both pending and committed retry ownership. Add the above interleaving without external lifecycle deletion and require a later retry to succeed. This is an availability/recovery defect, not evidence of ciphertext confidentiality compromise. Worker is not automatically deployed by this repository.

### R1-WEB-02 — P3: static webmanifest exposes an unresolved release token

Location: `website/_source/scripts/build.mjs:279-283` and `src/site.webmanifest`.

The source contains `version: "{{STABLE_TAG}}"`; normal build replaces it, but static mode retains it instead of removing it. Confirmed in the approved candidate's `site.webmanifest` and in regenerated local static output. This contradicts the static artifact's no-version-stamps contract, although browsers ignore this nonstandard manifest field and it does not cause the reported 404. Remove the field in static mode and add a manifest/token regression assertion. No visual change is needed.

## GPT Site path verification

The approved candidate inspected was `/tmp/dropspace-website-sync-031/gpt-site/dist`. Its current root redirect executes to `/en/#ai-lyrics` for en-US and `/zh-cn/#ai-lyrics` for zh-CN. Both generated language-switch hrefs point to root language routes. Every root-relative href/src/poster in both language pages exists in that exact candidate tree. The candidate no longer contains the erroneous `/DropSpace/` root redirect.

This is VM execution plus filesystem/DOM validation, not a live deployed Site or browser pass. A strict static HTTP server + Playwright Chromium attempt was made, but Chromium terminated before navigation with sandbox `socket() failed: Operation not permitted` (also read-only Crash Reports settings). No bypass was attempted. Root coordination must still perform live GPT Site root, language switch, hash/deep links, asset responses and unknown-route verification after publication. The repository preview server intentionally strips `/DropSpace/` even in static mode; the new static tests explicitly check requests for that prefix, which helps avoid this server compatibility concealing a regression.

## Checks actually run

- `node --test share-worker/test/*.test.mjs`: 21 passed, 0 failed
- `node --test website/_source/scripts/*.test.mjs`: 37 passed, 0 failed (against existing Pages dist, with static build performed by its test)
- `node scripts/test-release-metadata.mjs`: passed current metadata
- `node --test scripts/test-release-metadata.test.mjs`: 12 passed, 0 failed
- CSSOM structural parse of current styles.css: 511 rules parsed, 56,626 bytes
- Website lockfile parse: 44 package records; root devDependencies match package.json
- Approved candidate root redirect execution and local reference existence: passed both locales
- Deterministic Worker orphan-race reproduction: defect confirmed
- Browser attempt: blocked before page load; no browser claims
- No Windows executable/build/install/signing/native runtime checks were run: this Linux task has no pwsh or Windows runtime
- No live GitHub synchronization, remote release verification, deployment or remote mutation was performed

## Coverage and boundaries

All scripts, workflows, Worker implementation/tests, website build/runtime/tests, installer/identity source and NativeCrashProbe source listed below were read for behavior. No independent Updater project exists in this checkout: update implementation lives in already-reviewed Core/Infrastructure and was not redundantly rereviewed. Release scripts, update-manifest generation/validation and public updater metadata were covered here.

The approved website visuals were preserved. HTML and translation/build/runtime behavior were read; CSS was parsed structurally, not re-designed or visually re-approved. Binary assets were inventoried/hashed only, not visually reassessed. Dependency lockfiles and release fixture received structural/schema checks, not manual line-by-line dependency audits. Historical `.github/release-notes` and root product/legal documentation are outside this executable/configuration partition.

A green Node suite does not close the two findings above, certify native Windows behavior, or certify the deployed GPT Site. Remaining release coordination should repair/retest findings, build from final committed bytes, run the Windows matrix/native lifecycle gates, and verify the exact published artifacts and sites.

## Actual per-file coverage ledger

Status meanings: **reviewed** = behavior/source read; **structural** = full bytes parsed/validated without semantic/visual rereview; **binary** = existence/hash inventory only. Hashes identify this pass's working-tree snapshot; concurrent later edits may differ.

| File | Coverage | Bytes | SHA-256 (prefix) |
|---|---|---:|---|
| `.gitattributes` | reviewed | 479 | `ee225bc61215f23a` |
| `.github/workflows/ci.yml` | reviewed | 7810 | `c17b982fde3259b3` |
| `.github/workflows/deploy-website.yml` | reviewed | 3981 | `d032e79438a72995` |
| `.github/workflows/publish-release-bridge.yml` | reviewed | 1885 | `784d250ab161c7cf` |
| `.github/workflows/release.yml` | reviewed | 23241 | `ac98a73f7658b1d0` |
| `.github/workflows/secret-scan.yml` | reviewed | 806 | `951a7adce05ba77c` |
| `.gitignore` | reviewed | 885 | `c52933e854277737` |
| `Directory.Build.props` | reviewed | 2377 | `4a4923e63c8cbe71` |
| `Directory.Packages.props` | reviewed | 1192 | `90c8b3443f14ab7d` |
| `DropSpace.sln` | reviewed | 4449 | `e22820e7e0b486c9` |
| `RELEASE_VERSION` | reviewed | 15 | `55affeda4ac99d0e` |
| `global.json` | reviewed | 109 | `ce61f5ae207d33d0` |
| `identity/AppxManifest.xml.template` | reviewed | 2115 | `9ae8a09aa8a557fb` |
| `installer/DropSpace.Identity.ps1` | reviewed | 985 | `b7d19eda7b3ade46` |
| `installer/DropSpace.iss` | reviewed | 13719 | `17cb9077f0e8db32` |
| `scripts/Build-AiLyricsRuntime.ps1` | reviewed | 5921 | `104b903dd11fc430` |
| `scripts/Build-IdentityPackage.ps1` | reviewed | 3876 | `ffc63d712e968415` |
| `scripts/Build-Installer.ps1` | reviewed | 4770 | `3ff8136680e7f112` |
| `scripts/Build-PortableExe.ps1` | reviewed | 3431 | `6434cf41f3c5efb0` |
| `scripts/Build-UnsignedPackage.ps1` | reviewed | 3628 | `6dd4fc1b679ddd63` |
| `scripts/Compile-Win32Resource.ps1` | reviewed | 2990 | `d2d24f278b82e26e` |
| `scripts/Generate-BrandAssets.ps1` | reviewed | 11865 | `fba2752416ef6d86` |
| `scripts/Generate-PortableResourcesPri.ps1` | reviewed | 5456 | `eeda73552dabc89d` |
| `scripts/Get-AiLyricsSmokeModel.ps1` | reviewed | 1992 | `854e143862f282e3` |
| `scripts/Install-InnoSetup.ps1` | reviewed | 3721 | `389c6c4a885e1385` |
| `scripts/Install-TestWindowsAppRuntime.ps1` | reviewed | 3756 | `9c8f4373ead2a8a3` |
| `scripts/New-UpdateManifest.ps1` | reviewed | 2960 | `9b3fcdd3ddb0de48` |
| `scripts/ReleaseArtifactVersion.ps1` | reviewed | 1498 | `bce6de2dfa553e3d` |
| `scripts/ReleaseNotes.ps1` | reviewed | 1278 | `7d20db1c342751e1` |
| `scripts/ReleaseVersion.ps1` | reviewed | 4021 | `a1f3191e8d9c7b6f` |
| `scripts/Test-AiLyricsRuntime.ps1` | reviewed | 5006 | `c08f3e876ca9a16e` |
| `scripts/Test-BrandAssets.ps1` | reviewed | 10140 | `e549a18cf5ca0a1b` |
| `scripts/Test-HardcodingGovernance.ps1` | reviewed | 5082 | `ac9837330ac7abdf` |
| `scripts/Test-InstallerLifecycle.ps1` | reviewed | 21727 | `83d0fe957588ff90` |
| `scripts/Test-Localization.ps1` | reviewed | 8930 | `a4e9f0847b83fd16` |
| `scripts/Test-MsixSymbolPolicy.ps1` | reviewed | 1357 | `ceeaf3f0489b97b5` |
| `scripts/Test-PortableSmoke.ps1` | reviewed | 17979 | `b5f166013f815652` |
| `scripts/Test-ReleaseArtifactVersion.ps1` | reviewed | 1436 | `063c85998b7cb246` |
| `scripts/Test-ReleaseConsistency.ps1` | reviewed | 5957 | `bce7cf3b81e9096a` |
| `scripts/Test-ReleaseVersion.ps1` | reviewed | 8270 | `7cd59ddd9f9f2a10` |
| `scripts/Test-SecretHygiene.ps1` | reviewed | 4573 | `3df73ef5e560f8a8` |
| `scripts/Test-UpdateManifest.ps1` | reviewed | 3201 | `ad748c8a63aec102` |
| `scripts/Test-WindowsCompatibility.ps1` | reviewed | 6853 | `9103df1315b2ecf1` |
| `scripts/TestDiagnostics.ps1` | reviewed | 11765 | `93cae92be9b887cb` |
| `scripts/WindowsCompatibility.ps1` | reviewed | 687 | `db22db50f1a98577` |
| `scripts/audit-repository.py` | reviewed | 4203 | `a88ef111d5e61a15` |
| `scripts/test-release-metadata.mjs` | reviewed | 3158 | `41bfb3b08d690169` |
| `scripts/test-release-metadata.test.mjs` | reviewed | 2627 | `f121af2b01566822` |
| `share-worker/README.md` | reviewed | 2799 | `7ed16cb3f6ed7abf` |
| `share-worker/package.json` | reviewed | 124 | `d9e81c4bb4bc9820` |
| `share-worker/src/index.js` | reviewed | 37298 | `749dc5cc95f6779c` |
| `share-worker/src/policy.js` | reviewed | 585 | `c001f229752f642b` |
| `share-worker/src/protocol.js` | reviewed | 305 | `dc993e138598bfe9` |
| `share-worker/test/lifecycle-regression.test.mjs` | reviewed | 4364 | `15c08d42775e48f4` |
| `share-worker/test/upload-ownership.test.mjs` | reviewed | 13685 | `27b6205b979cc72e` |
| `share-worker/test/worker.test.mjs` | reviewed | 17260 | `6e5984b09e8f69a2` |
| `share-worker/wrangler.toml.example` | reviewed | 728 | `45a1e576f6fc9a80` |
| `tools/NativeCrashProbe/NativeCrashProbe.csproj` | reviewed | 310 | `7dfbc62cc1af0f1d` |
| `tools/NativeCrashProbe/Program.cs` | reviewed | 7357 | `2bd026085cacf359` |
| `tools/NativeCrashProbe/packages.lock.json` | structural | 61 | `03eeadc5ef377c17` |
| `website/_source/.gitignore` | reviewed | 13 | `28e19bf7ad1307d7` |
| `website/_source/README.md` | reviewed | 5679 | `d42500f7affbee72` |
| `website/_source/data/releases.json` | structural | 44661 | `80e5d6ae94bdfcf0` |
| `website/_source/package-lock.json` | structural | 20615 | `0656a3fabd9f337c` |
| `website/_source/package.json` | reviewed | 456 | `5298e8cbcdc1d4d0` |
| `website/_source/playwright.config.mjs` | reviewed | 979 | `e7aa2c9e1bf6837d` |
| `website/_source/scripts/ai-lyrics.test.mjs` | reviewed | 4368 | `ca2187bb7cb57076` |
| `website/_source/scripts/build-static.mjs` | reviewed | 66 | `42c596266148defa` |
| `website/_source/scripts/build.mjs` | reviewed | 18963 | `c062255bf5d5ec7f` |
| `website/_source/scripts/generate-demo-audio.mjs` | reviewed | 1122 | `40028494d23edba1` |
| `website/_source/scripts/i18n.mjs` | reviewed | 22989 | `f76530bca62a7b2b` |
| `website/_source/scripts/release-contract.mjs` | reviewed | 14056 | `c7ffb2f1a7d48909` |
| `website/_source/scripts/release-contract.test.mjs` | reviewed | 6872 | `c1ee0c947e66af20` |
| `website/_source/scripts/serve.mjs` | reviewed | 1455 | `f82f0a9ba462f410` |
| `website/_source/scripts/site.test.mjs` | reviewed | 10560 | `f75c92b1000a2367` |
| `website/_source/scripts/static-path.mjs` | reviewed | 424 | `ac8400de2a5325fa` |
| `website/_source/scripts/static-path.test.mjs` | reviewed | 873 | `6c8d5cef8d7b8e34` |
| `website/_source/scripts/sync-releases.mjs` | reviewed | 2676 | `07d73d4241a5daad` |
| `website/_source/scripts/sync-releases.test.mjs` | reviewed | 7031 | `e543a4ca7be6f28a` |
| `website/_source/scripts/verify-published-release.mjs` | reviewed | 7632 | `d604c8f36e38bf12` |
| `website/_source/scripts/verify-published-release.test.mjs` | reviewed | 4070 | `351ace6163f2d80b` |
| `website/_source/src/assets/drag-demo.webm` | binary | 45444 | `bb31de57adc2eb85` |
| `website/_source/src/assets/dropspace-logo.png` | binary | 106799 | `32e28e0675f8d1c8` |
| `website/_source/src/assets/favicon.png` | binary | 32556 | `17eab182ebf11052` |
| `website/_source/src/assets/lyrics-demo.wav` | binary | 384044 | `6c4333702ee981b0` |
| `website/_source/src/assets/og-image.png` | binary | 86883 | `38baea41de865a54` |
| `website/_source/src/assets/product-overview.webp` | binary | 8562 | `3e2f363da985d6b5` |
| `website/_source/src/changelog/index.html` | reviewed | 3448 | `8027d255616cd267` |
| `website/_source/src/index.html` | reviewed | 30807 | `18fd004382e8006e` |
| `website/_source/src/lyrics-demo.js` | reviewed | 8321 | `23b8f1373729eba8` |
| `website/_source/src/script.js` | reviewed | 13281 | `2769e1a78e8ac7b3` |
| `website/_source/src/site.webmanifest` | reviewed | 241 | `a9ffc5facb4b98bd` |
| `website/_source/src/styles.css` | structural | 56626 | `822924f21bf25358` |
| `website/_source/tests/ai-lyrics.spec.mjs` | reviewed | 9742 | `abc210deb6b832d3` |
| `website/_source/tests/editorial-layout.spec.mjs` | reviewed | 1589 | `54b16661096a1b41` |
| `website/_source/tests/language-switch.spec.mjs` | reviewed | 6348 | `76a4bc0ee148d66d` |
| `website/_source/tests/native-showcase.spec.mjs` | reviewed | 3200 | `2407e4807782e092` |
| `website/_source/tests/release-runtime.spec.mjs` | reviewed | 3525 | `675c721f0f795222` |
| `website/_source/tests/static-routing.spec.mjs` | reviewed | 1440 | `06bc910115f364c8` |
