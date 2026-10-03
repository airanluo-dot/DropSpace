# Round 2: release, website, Worker and root configuration review

## Scope and verdict

Independent second source pass on 2026-10-01. Starting commit `316f05ec1325384241083e903924cf22e7398bcf`, tree `248321174a405d823a7dcb2a0e40f231c02a8514` (the parent identifies equivalent remote tree at `4d8d0bb1a714ebfb9a91fd62f9e8b19f8f4086a8`; this review did not contact that remote). The source was reread, rather than treating the first report or a diff as the review. Root-owned corrections made during the pass were separately checked below.

**Two concrete defects found: a Worker retry recovery hole and an unbound publication-dispatch commit. The Worker correction passes its regression; the dispatch correction was subsequently verified through source/YAML binding checks, with PowerShell execution still pending. A missing first-run fixture in the native diagnostic probe was also identified and corrected in source. Windows execution remains required.**

No implementation, visual, deployment, online-site, GitHub, signing, tagging or access changes were made by this reviewer. Only this report and temporary offline test evidence were written. Node tests regenerated ignored website build outputs. The approved website appearance remains unchanged. This is round 2 only; it does not complete round 3 or authorize publication.

## R2-REL-01 — P2: publication bridge does not bind the dispatched build to the validated commit

Location: `.github/workflows/publish-release-bridge.yml:30-59`; `.github/workflows/release.yml` workflow-dispatch inputs and publication-policy step.

The bridge checks out `main`, verifies that the publication branch event's `GITHUB_SHA` equals that checkout, and then separately dispatches `release.yml --ref main`. The release workflow validates the actor and the `refs/heads/main` ref, but does not validate a requested commit hash.

Interleaving: request branch and main both point to A; bridge validates A; main advances to B; dispatch resolves main to B; release.yml builds and can publish B. If B keeps the same release tag, this silently publishes different source bytes from the immutable request the bridge says it validated. This is a source-level TOCTOU finding, not a claim that an actual unauthorized release occurred.

Minimal fix: bind publication requests to an `expected_commit` (and preferably expected tag), pass the validated event SHA from the bridge, and fail the release workflow before building/publishing when `github.sha` differs. Define a deliberate explicit-dispatch behavior rather than weakening the SHA assertion to make the bridge succeed. Add a regression proving that a requested A dispatched against B is rejected. No remote test was run.

**Correction reviewed at 21:35 UTC:** the bridge now supplies `expected_commit="${GITHUB_SHA}"`. The release workflow passes that input and its event `github.sha` through environment variables to `Assert-DropSpacePublicationCommit` before build/restore. The function rejects missing/symbolic/non-lowercase-40-hex inputs and any unequal SHA. The existing main/actor gates and publish dependency on successful validation remain intact. `Test-ReleaseVersion.ps1` includes matching-hash acceptance plus empty/symbolic/mismatch rejection cases; both CI/release validation invoke that script. Parsed both workflow YAML files and asserted the complete bridge-input/environment/assertion/dependency wiring offline. This closes the identified race by failing when main advances; it does not auto-publish the old commit. Direct publication dispatches must now supply the reviewed SHA; publish=false remains usable without it. **PowerShell tests and actual workflow execution were not run here.**

## R2-WORKER-01 — P2: a transient cleanup failure leaves uploads permanently conflicted

Original location: `share-worker/src/index.js:125-148` and coordinator rollback. The existing R1 correction handles an expired predecessor when a retry reaches the R2 put; it did not cover an already-present orphan detected by the initial R2 head.

Reproduction, using the production Worker and coordinator with serialized in-memory storage:
1. R2 writes the initial object's bytes, then reports an error.
2. Rollback removes the pending reservation, but its R2 delete fails once.
3. Every later retry reserves successfully and sees the orphan in `head`.
4. It throws 409 before `putStarted=true`, so rollback receives `cleanupStoredObject=false` forever, even after storage has recovered.

Offline reproduction: `/tmp/round2-worker-cleanup-failure.test.mjs`, derived from the existing partial-write fixture with a single injected R2 delete failure. Before correction, output was `retry after temporary cleanup failure 409 next retry 409 orphan true`.

The parent changed rollback after a successful reservation to always request cleanup, retaining coordinator protection for both committed and in-flight owners. I reread the correction and reran all Worker tests: **23 passed, 0 failed**. The independent reproduction now reaches a subsequent 201. The added repository regression explicitly asserts initial 500, first reconciliation retry 409, orphan removal, then next retry 201. This closes the reproduced permanent conflict; it intentionally does not turn the first conflict response into a successful upload.

## R2-PROBE-01 — P2 diagnostic coverage: clean native probe stops at the first-run privacy choice

Original location: `tools/NativeCrashProbe/Program.cs:7-13`, `.github/workflows/ci.yml` isolated native probe invocation; call chain `App.xaml.cs:179-190` before smoke initialization.

CI gives the probe a new isolated data root, then runs `--test-mode --smoke-test` without creating settings. The real application correctly waits for the first-run privacy choice before initializing the smoke path. The probe supplies no UI response and therefore can time out without exercising the intended native startup/clipboard/overlay paths. This finding follows the code path; no Windows timeout was reproduced here.

The parent added explicit privacy/capture/startup fixture values in `<DROPSPACE_TEST_DATA_ROOT>/data/settings.json`. That is the correct location: `AppStoragePaths` adds `data` to its root once. Existing settings are merged. The app itself gained no privacy-bypass flag. Source correction reviewed; **native probe execution is not verified**.

The parent also added the same explicit settings setup before the first baseline launch in the isolated installer lifecycle test. `Test-InstallerLifecycle.ps1`'s `$dataRoot` is `%LOCALAPPDATA%/DropSpace`, so appending `data` is correct, not double nesting. Important qualification: existing legacy settings with explicit `ClipboardPaused` and `StartWithWindows` are migrated to completed privacy choices by `JsonSettingsService.MigratePrivacyChoice`; consequently the original installer path was a fixture-dependence risk, not a proven unavoidable timeout on every Beta31 upgrade. The explicit fixture removes that dependence. The full Windows upgrade/restart/uninstall sequence still needs execution.

## Packaging and runtime checks

- Reread all release/build/bootstrap/signing workflow source, version arithmetic and manifest generators, installer maintenance handshake and downgrade guard, uninstall preserve/purge behavior, identity registration scripts and manifests, diagnostic retention and NativeCrashProbe source.
- The license collector now reads the pinned source tree directly, checks three required notices, includes all matched license/notice files and no longer depends on generated `license.cpp`. Its fixture checks collection with no build tree and fail-closed missing required notice. **PowerShell fixture not run here**; this Linux environment has no `pwsh`.
- The new baseline `llama-tokenize.exe` is built from the same fixed source commit, recorded with bytes/SHA-256 in the manifest, required by the payload validator and embedded in the App project. The native smoke test explicitly invokes tokenizer counting/cancellation for both configured models. It still needs actual Windows builds and model execution; source presence and hashes cannot establish usable Windows runtime behavior.
- Read the existing package identity, resources/PRI generation, PE identity checks, pinned Inno bootstrap and final artifact checks. No additional confirmed package blocker was established in this partition.

## Website route and release validation

- Both full Pages and GPT static builders share source but now derive separate base paths. Offline execution of the root script with English and Chinese navigator locales gives `/DropSpace/en/#ai-lyrics`, `/DropSpace/zh-cn/#ai-lyrics` for Pages, and `/en/#ai-lyrics`, `/zh-cn/#ai-lyrics` for GPT.
- Root script content matches the generated CSP SHA-256. All generated root-relative href/src/poster references checked against actual files: 18 per locale for Pages and 16 per locale for static. The static manifest no longer retains `{{STABLE_TAG}}`; R1-WEB-02 is corrected and covered by the Node regression.
- Reread runtime release selection, fail-closed release sync, official URL/asset matching, locale switching and independent HTML rendering, static runtime stripping, original-audio demonstration, browser tests and Node tests. CSS received full-byte structural parsing (511 rules), not visual reapproval or a redesign.
- Release verification compares public metadata, asset naming, manifest/checksum agreement and English homepage markers. It does **not** download and hash the three public executable/package bodies; therefore its success must not be described as independent byte-level verification of those remote binaries. This is an explicit coverage boundary, not a newly proven corruption.
- `deploy-website.yml` still deploys for matching main-branch pushes. Because this user requires both websites and the new App to publish together, the parent must coordinate that push/deploy window. A local pass does not authorize an early Pages or GPT deployment.
- No request to the current live GPT Site was made in this second review. The previously established public 404 stays open until the final deployment is followed by root entry, both language pages/switches, deep links, assets and unknown-route verification. No browser test was run in this pass.

## Tests actually executed

1. Initial `node --test share-worker/test/*.test.mjs`: 22 passed, 0 failed; did not cover the new failure injection.
2. Independent transient-cleanup reproduction: permanent 409 confirmed before parent correction; recovery to 201 observed after it.
3. Corrected Worker suite: 23 passed, 0 failed. Log `/tmp/round2-worker-tests-final.txt`.
4. `npm test` in `website/_source`: builder plus 37 Node tests passed, 0 failed. Log `/tmp/round2-website-tests.txt`.
5. `node scripts/test-release-metadata.mjs`: passed.
6. `node --test scripts/test-release-metadata.test.mjs`: 12 passed, 0 failed.
7. CSS full-byte structural parse: 511 rules. Website lockfile full JSON parse: 44 package records, root devDependencies equal package.json.
8. Root redirect VM tests, CSP hash verification and generated local reference checks: passed both variants/locales.
9. Publication bridge/release workflow YAML parse and binding assertions: passed. No PowerShell execution.

Not run: Windows builds, PowerShell tests, PE execution, App UI, real installer lifecycle, MSIX registration/signing, native AI/tokenizer inference, browser interaction, remote CI, remote artifacts or live site verification. No results here should be substituted for those gates.

## Per-file coverage ledger

`source reviewed` means the implementation/config/test source was read during this pass, not only diffed. `structural full-byte validation` means parsed/schema/test validation without a manual semantic/visual audit of every data entry. `binary inventory only` means existence/size/hash, not pixel/audio/video review. Generated ignored outputs, node_modules/vendor runtime internals and historical release-note archives are not represented as reviewed application source. Supporting call-chain reads (App project, App startup, AppStoragePaths, privacy migration, native AI smoke test) supplement the partition and are not whole-App reaudits.

Hashes below capture the working-tree files after the parent's Worker/fixture corrections at approximately 21:33 UTC, with the four publication-binding files refreshed at 21:35 UTC; subsequent root edits require separate reconciliation.

| File | Coverage | Bytes | SHA-256 prefix |
|---|---|---:|---|
| `.editorconfig` | source reviewed | 288 | `10d7a6dfae75ae1d` |
| `.gitattributes` | source reviewed | 479 | `ee225bc61215f23a` |
| `.github/workflows/ci.yml` | source reviewed | 7810 | `c17b982fde3259b3` |
| `.github/workflows/deploy-website.yml` | source reviewed | 3981 | `d032e79438a72995` |
| `.github/workflows/publish-release-bridge.yml` | source reviewed | 1920 | `b948a78c64571b21` |
| `.github/workflows/release.yml` | source reviewed | 23608 | `18592ff4d505d133` |
| `.github/workflows/secret-scan.yml` | source reviewed | 806 | `951a7adce05ba77c` |
| `.gitignore` | source reviewed | 885 | `c52933e854277737` |
| `Directory.Build.props` | source reviewed | 2377 | `4a4923e63c8cbe71` |
| `Directory.Packages.props` | source reviewed | 1192 | `90c8b3443f14ab7d` |
| `DropSpace.sln` | source reviewed | 4449 | `e22820e7e0b486c9` |
| `RELEASE_VERSION` | source reviewed | 15 | `55affeda4ac99d0e` |
| `global.json` | source reviewed | 109 | `ce61f5ae207d33d0` |
| `identity/AppxManifest.xml.template` | source reviewed | 2115 | `9ae8a09aa8a557fb` |
| `installer/DropSpace.Identity.ps1` | source reviewed | 985 | `b7d19eda7b3ade46` |
| `installer/DropSpace.iss` | source reviewed | 13719 | `17cb9077f0e8db32` |
| `scripts/Build-AiLyricsRuntime.ps1` | source reviewed | 5803 | `3ba7e7f6b8b9bd8f` |
| `scripts/Build-IdentityPackage.ps1` | source reviewed | 3876 | `ffc63d712e968415` |
| `scripts/Build-Installer.ps1` | source reviewed | 4770 | `3ff8136680e7f112` |
| `scripts/Build-PortableExe.ps1` | source reviewed | 3431 | `6434cf41f3c5efb0` |
| `scripts/Build-UnsignedPackage.ps1` | source reviewed | 3628 | `6dd4fc1b679ddd63` |
| `scripts/Collect-AiRuntimeNotices.ps1` | source reviewed | 1006 | `9cd871f9c6b33e06` |
| `scripts/Compile-Win32Resource.ps1` | source reviewed | 2990 | `d2d24f278b82e26e` |
| `scripts/Generate-BrandAssets.ps1` | source reviewed | 11865 | `fba2752416ef6d86` |
| `scripts/Generate-PortableResourcesPri.ps1` | source reviewed | 5456 | `eeda73552dabc89d` |
| `scripts/Get-AiLyricsSmokeModel.ps1` | source reviewed | 1992 | `854e143862f282e3` |
| `scripts/Install-InnoSetup.ps1` | source reviewed | 3721 | `389c6c4a885e1385` |
| `scripts/Install-TestWindowsAppRuntime.ps1` | source reviewed | 3756 | `9c8f4373ead2a8a3` |
| `scripts/New-UpdateManifest.ps1` | source reviewed | 2960 | `9b3fcdd3ddb0de48` |
| `scripts/ReleaseArtifactVersion.ps1` | source reviewed | 1498 | `bce6de2dfa553e3d` |
| `scripts/ReleaseNotes.ps1` | source reviewed | 1278 | `7d20db1c342751e1` |
| `scripts/ReleaseVersion.ps1` | source reviewed | 4339 | `d4be8061e521f007` |
| `scripts/Test-AiLyricsRuntime.ps1` | source reviewed | 5156 | `7a1b6c572bba3fe2` |
| `scripts/Test-AiRuntimeNotices.ps1` | source reviewed | 1655 | `e966258b2dc115c7` |
| `scripts/Test-BrandAssets.ps1` | source reviewed | 10140 | `e549a18cf5ca0a1b` |
| `scripts/Test-HardcodingGovernance.ps1` | source reviewed | 5082 | `ac9837330ac7abdf` |
| `scripts/Test-InstallerLifecycle.ps1` | source reviewed | 22080 | `ae73cb7c80add8f0` |
| `scripts/Test-Localization.ps1` | source reviewed | 8930 | `a4e9f0847b83fd16` |
| `scripts/Test-MsixSymbolPolicy.ps1` | source reviewed | 1357 | `ceeaf3f0489b97b5` |
| `scripts/Test-PortableSmoke.ps1` | source reviewed | 18184 | `269a191385ac506d` |
| `scripts/Test-ReleaseArtifactVersion.ps1` | source reviewed | 1436 | `063c85998b7cb246` |
| `scripts/Test-ReleaseConsistency.ps1` | source reviewed | 5957 | `bce7cf3b81e9096a` |
| `scripts/Test-ReleaseVersion.ps1` | source reviewed | 8641 | `d4a71be3258bef47` |
| `scripts/Test-SecretHygiene.ps1` | source reviewed | 4573 | `3df73ef5e560f8a8` |
| `scripts/Test-UpdateManifest.ps1` | source reviewed | 3201 | `ad748c8a63aec102` |
| `scripts/Test-WindowsCompatibility.ps1` | source reviewed | 6853 | `9103df1315b2ecf1` |
| `scripts/TestDiagnostics.ps1` | source reviewed | 11765 | `93cae92be9b887cb` |
| `scripts/WindowsCompatibility.ps1` | source reviewed | 687 | `db22db50f1a98577` |
| `scripts/audit-repository.py` | source reviewed | 4203 | `a88ef111d5e61a15` |
| `scripts/test-release-metadata.mjs` | source reviewed | 3158 | `41bfb3b08d690169` |
| `scripts/test-release-metadata.test.mjs` | source reviewed | 2627 | `f121af2b01566822` |
| `share-worker/README.md` | source reviewed | 2799 | `7ed16cb3f6ed7abf` |
| `share-worker/package.json` | source reviewed | 124 | `d9e81c4bb4bc9820` |
| `share-worker/src/index.js` | source reviewed | 37820 | `07eb44c91d7fa1cd` |
| `share-worker/src/policy.js` | source reviewed | 585 | `c001f229752f642b` |
| `share-worker/src/protocol.js` | source reviewed | 305 | `dc993e138598bfe9` |
| `share-worker/test/lifecycle-regression.test.mjs` | source reviewed | 4364 | `15c08d42775e48f4` |
| `share-worker/test/upload-ownership.test.mjs` | source reviewed | 16792 | `c2d270a2acbc7678` |
| `share-worker/test/worker.test.mjs` | source reviewed | 17260 | `6e5984b09e8f69a2` |
| `share-worker/wrangler.toml.example` | source reviewed | 728 | `45a1e576f6fc9a80` |
| `tools/NativeCrashProbe/NativeCrashProbe.csproj` | source reviewed | 310 | `7dfbc62cc1af0f1d` |
| `tools/NativeCrashProbe/Program.cs` | source reviewed | 7951 | `897cfcd2ec09516c` |
| `tools/NativeCrashProbe/packages.lock.json` | structural full-byte validation | 61 | `03eeadc5ef377c17` |
| `website/_source/.gitignore` | source reviewed | 13 | `28e19bf7ad1307d7` |
| `website/_source/README.md` | source reviewed | 5679 | `d42500f7affbee72` |
| `website/_source/data/releases.json` | structural full-byte validation | 44661 | `80e5d6ae94bdfcf0` |
| `website/_source/package-lock.json` | structural full-byte validation | 20615 | `0656a3fabd9f337c` |
| `website/_source/package.json` | source reviewed | 456 | `5298e8cbcdc1d4d0` |
| `website/_source/playwright.config.mjs` | source reviewed | 979 | `e7aa2c9e1bf6837d` |
| `website/_source/scripts/ai-lyrics.test.mjs` | source reviewed | 4457 | `571e4e6d85b312a8` |
| `website/_source/scripts/build-static.mjs` | source reviewed | 66 | `42c596266148defa` |
| `website/_source/scripts/build.mjs` | source reviewed | 18993 | `dab420bca783e210` |
| `website/_source/scripts/generate-demo-audio.mjs` | source reviewed | 1122 | `40028494d23edba1` |
| `website/_source/scripts/i18n.mjs` | source reviewed | 22989 | `f76530bca62a7b2b` |
| `website/_source/scripts/release-contract.mjs` | source reviewed | 14056 | `c7ffb2f1a7d48909` |
| `website/_source/scripts/release-contract.test.mjs` | source reviewed | 6872 | `c1ee0c947e66af20` |
| `website/_source/scripts/serve.mjs` | source reviewed | 1455 | `f82f0a9ba462f410` |
| `website/_source/scripts/site.test.mjs` | source reviewed | 10560 | `f75c92b1000a2367` |
| `website/_source/scripts/static-path.mjs` | source reviewed | 424 | `ac8400de2a5325fa` |
| `website/_source/scripts/static-path.test.mjs` | source reviewed | 873 | `6c8d5cef8d7b8e34` |
| `website/_source/scripts/sync-releases.mjs` | source reviewed | 2676 | `07d73d4241a5daad` |
| `website/_source/scripts/sync-releases.test.mjs` | source reviewed | 7031 | `e543a4ca7be6f28a` |
| `website/_source/scripts/verify-published-release.mjs` | source reviewed | 7632 | `d604c8f36e38bf12` |
| `website/_source/scripts/verify-published-release.test.mjs` | source reviewed | 4070 | `351ace6163f2d80b` |
| `website/_source/src/assets/drag-demo.webm` | binary inventory only | 45444 | `bb31de57adc2eb85` |
| `website/_source/src/assets/dropspace-logo.png` | binary inventory only | 106799 | `32e28e0675f8d1c8` |
| `website/_source/src/assets/favicon.png` | binary inventory only | 32556 | `17eab182ebf11052` |
| `website/_source/src/assets/lyrics-demo.wav` | binary inventory only | 384044 | `6c4333702ee981b0` |
| `website/_source/src/assets/og-image.png` | binary inventory only | 86883 | `38baea41de865a54` |
| `website/_source/src/assets/product-overview.webp` | binary inventory only | 8562 | `3e2f363da985d6b5` |
| `website/_source/src/changelog/index.html` | source reviewed | 3448 | `8027d255616cd267` |
| `website/_source/src/index.html` | source reviewed | 30807 | `18fd004382e8006e` |
| `website/_source/src/lyrics-demo.js` | source reviewed | 8321 | `23b8f1373729eba8` |
| `website/_source/src/script.js` | source reviewed | 13281 | `2769e1a78e8ac7b3` |
| `website/_source/src/site.webmanifest` | source reviewed | 241 | `a9ffc5facb4b98bd` |
| `website/_source/src/styles.css` | structural full-byte validation | 56626 | `822924f21bf25358` |
| `website/_source/tests/ai-lyrics.spec.mjs` | source reviewed | 9742 | `abc210deb6b832d3` |
| `website/_source/tests/editorial-layout.spec.mjs` | source reviewed | 1589 | `54b16661096a1b41` |
| `website/_source/tests/language-switch.spec.mjs` | source reviewed | 6348 | `76a4bc0ee148d66d` |
| `website/_source/tests/native-showcase.spec.mjs` | source reviewed | 3200 | `2407e4807782e092` |
| `website/_source/tests/release-runtime.spec.mjs` | source reviewed | 3525 | `675c721f0f795222` |
| `website/_source/tests/static-routing.spec.mjs` | source reviewed | 1440 | `06bc910115f364c8` |
