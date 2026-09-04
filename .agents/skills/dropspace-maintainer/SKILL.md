---
name: dropspace-maintainer
description: Use for any Codex task involving airanluo-dot/DropSpace, including app features, bug fixes, drag-and-drop behavior, Dynamic Island UI, updater behavior, Stable/Preview releases, GitHub Actions, GitHub Releases, website/API synchronization, GitHub Pages deployment, release cleanup, CI artifact retention, or production verification.
---

# DropSpace Codex Execution Guide

Startup window invariant: `--startup` must create the main HWND for backend services without an initial `Show`/`Activate`; only normal and redirected activation may show it. Keep this covered by the portable smoke test.

**Mandatory shipping rule:** Every user-visible DropSpace update, including bug fixes, must finish with a new Preview release and the normal website/API synchronization workflow. This is not optional unless the user explicitly requests investigation-only work or explicitly says not to publish.

## 1. Purpose

This skill is the authoritative execution guide for Codex when working on DropSpace.

It is intentionally self-contained. Do not assume that the current conversation contains project history, release architecture, deployment details, or user preferences. Start from this guide, then verify the current repository state before changing anything.

The goal is not merely to edit files. The goal is to leave DropSpace in a verified, releasable, internally consistent state across:

- Windows application code
- build metadata
- Stable/Preview update behavior
- installers and portable builds
- GitHub Releases
- official website
- website release API
- GitHub Pages deployment
- GitHub Actions
- tests and release validation
- historical release integrity

When a task asks for implementation, perform the implementation. Do not stop at a proposal unless the user explicitly requested analysis only.

---

## 2. Canonical project identity

Primary repository:

- GitHub: `airanluo-dot/DropSpace`

Official website:

- `https://airanluo-dot.github.io/DropSpace/`

Official website release metadata endpoint used by the app:

- `https://airanluo-dot.github.io/DropSpace/api/v1/releases.json`

GitHub Releases REST fallback used by the app:

- `https://api.github.com/repos/airanluo-dot/DropSpace/releases?per_page=20&page=1`

Canonical public release host:

- GitHub Releases in `airanluo-dot/DropSpace`

There may be historical or experimental website repositories or deployment code. Do not assume they are active. Determine active production ownership from current workflows and deployed URLs before touching them.

---

## 3. Ground-truth hierarchy

Use this hierarchy whenever sources disagree.

### 3.1 Shipping release truth

For publicly shipped versions and downloadable binaries:

1. GitHub Release and its immutable public assets
2. current repository release/version source files
3. generated website release metadata
4. browser-rendered website state
5. CI artifacts

GitHub Actions artifacts are not the public release system and are not the updater's canonical download source.

### 3.2 Source truth

For implementation behavior:

1. current source on the branch being modified
2. current tests
3. current workflows/scripts
4. this skill's architectural guidance
5. historical assumptions

If this skill describes a path or implementation that has since moved, adapt to the current repository while preserving the invariant described here.

### 3.3 Never trust stale version assumptions

Do not hardcode a remembered current version.

Before release work, inspect at minimum:

- `RELEASE_VERSION`
- existing Git tags
- existing GitHub Releases
- matching release-note files
- the current main branch

If the local version, remote tag, and latest release disagree, resolve that inconsistency before publishing another release.

---

## 4. Mandatory orientation before modifying the repo

Before non-trivial changes, inspect the smallest relevant set of current files instead of blindly applying remembered architecture.

For general app work, inspect:

- repository status and current branch
- `RELEASE_VERSION`
- relevant project files under `src/`
- relevant tests
- build scripts under `scripts/`
- `.github/workflows/ci.yml` if CI may be affected
- `.github/workflows/release.yml` if packaging/release may be affected

For updater work, inspect:

- updater source implementations
- update selectors/channel logic
- `App.xaml.cs` or current service-registration equivalent
- `ReleaseBuildInfo.cs` or current runtime-version equivalent
- `scripts/New-UpdateManifest.ps1`
- updater tests

For website/release metadata work, inspect:

- `website/_source/scripts/sync-releases.mjs`
- `website/_source/scripts/release-contract.mjs`
- `website/_source/scripts/build.mjs`
- `website/_source/src/script.js`
- `website/_source/data/releases.json` if present
- `.github/workflows/deploy-website.yml`
- website tests

For release work, inspect:

- `RELEASE_VERSION`
- `.github/release-notes/`
- `scripts/ReleaseVersion.ps1`
- packaging/build scripts
- `.github/workflows/release.yml`
- existing GitHub Releases and tags

Do not modify unrelated subsystems merely because they are nearby.

---

## 5. Execution behavior

### 5.1 Default behavior

When the user asks Codex to change DropSpace:

- inspect
- implement
- test
- fix regressions caused by the change
- commit/push through the repository's established workflow when requested or implied by the task
- publish a Preview when the task is a shipping product/site change and project policy requires a test release
- deploy/synchronize the website when release data changes
- verify the live result
- report concrete identifiers and residual risks

Do not claim completion from source inspection alone when the task includes publishing or deployment.

### 5.2 Avoid unnecessary questions

Prefer resolving ambiguity from:

- repository state
- current workflows
- tests
- existing project conventions
- GitHub metadata

Only ask a question when a required choice cannot be inferred safely and materially changes the result.

If a task is large, make the best safe effort in the current run instead of stopping after planning.

### 5.3 No fake success

Never say that a release, deployment, PR, CI run, or live API is successful unless it has actually been verified.

Distinguish clearly among:

- source changed
- local tests passed
- commit pushed
- PR created
- PR merged
- workflow triggered
- workflow succeeded
- GitHub Release published
- GitHub Pages deployed
- live endpoint verified

---

## 6. Product invariants

These are high-value product constraints. Preserve them unless the current task explicitly changes them.

### 6.1 Platform and distribution

DropSpace is a 64-bit Windows 10 version 1809 (Build 17763) and Windows 11 application. Windows 11-only visuals and optional APIs are capability-gated; Windows 10 uses the documented base-visual and feature fallbacks.

Primary user experience goal:

- users should be able to download a shipping build and run/install it without needing Visual Studio, a .NET SDK, Windows App SDK setup, PowerShell setup, certificates, or developer tooling

Shipping architecture should continue to support the repository's intended x64 distribution path.

Public release artifacts currently revolve around:

- `DropSpaceSetup.exe` — recommended installer
- `DropSpace.exe` — portable build
- `DropSpace-x64.msix` — MSIX package
- `SHA256SUMS.txt` — checksums
- `update-manifest.json` — updater metadata

If filenames have intentionally changed in the current repo, update this guide's assumptions only after verifying all consumers. Otherwise preserve these names because updater and website contracts may depend on them.

### 6.2 Dynamic Island only

The old Notch/刘海 product mode has been removed.

Do not reintroduce:

- a user-selectable Notch mode
- old Notch settings
- old Notch UI branches
- stale documentation implying that both Notch and Dynamic Island remain available

Unless the user explicitly asks to restore it, the product is Dynamic Island only.

### 6.3 Window behavior

Preserve these behaviors when working on UI/windowing unless explicitly changing them:

- do not steal focus unnecessarily
- do not appear as a normal taskbar application window when that is not intended
- avoid unwanted Alt+Tab presence
- handle multi-monitor and DPI correctly
- hidden/empty state should not visibly occupy screen space unnecessarily
- topmost behavior must not create user-hostile focus or interaction regressions

### 6.4 Drag-and-drop behavior is regression-sensitive

Drag-to-wake / smart detection is a core feature and has historically been difficult to stabilize.

Do not casually rewrite it as part of unrelated refactors.

If a task touches drag detection, validate at least the relevant combinations of:

- Explorer file drags
- Desktop file drags
- normal left-button file drag behavior
- rapid drag movement
- dropping into the island
- cancelling a drag
- multi-monitor transitions
- DPI scaling boundaries
- classic/top-edge compatibility mode if still present
- third-party or virtual-file drags if supported by the current implementation
- Windows native Drop Tray / Share UI interactions if applicable

Avoid designs that trigger simply from a held mouse button when the intended behavior is specifically file dragging.

For the v0.3 Smart Drag Detection v2 architecture, preserve these durable boundaries:

- Smart idle owns no permanent top-edge or full-screen target; Classic alone may create the disclosed bounded edge host after explicit user selection, and Smart never switches to it implicitly.
- Explorer/Desktop exact-item and documented accessibility drag-start evidence are fast paths without third-party process-name rules. An unknown/non-exact press becomes only a generic threshold candidate and requires real OLE file-format verification.
- Generic verification uses at most one 144-pixel hollow local Region with a 12-pixel center hole and 60 ms hard lifetime. It is layered, no-activate, tool-window and topmost; it returns `DROPEFFECT_NONE`, never requests focus/taskbar/Alt+Tab, and queues revoke/destroy outside `DragEnter`.
- One bounded classifier serves the probe, Classic host and visible Island. Query only during verification. Keep `IsFileLikeEvidence`, `CanAcceptNow`, and `CanMaterialize` separate; accessibility proves intent, never file payload. Prefer `SHCreateShellItemArrayFromDataObject`, keep bounded CIDA only as fallback, and materialize virtual descriptors only after real Drop through confined, cancellable, rollback-safe streaming.
- Split lossy pointer movement from an unbounded reliable critical lane for press/release/cancel/completion/probe signals, while preserving timestamp order across both lanes. PointerMoved is the only lossy signal; critical write failures are shutdown diagnostics, not normal capacity pressure. Pointer release enters completion grace, a fresh press supersedes immediately, and all visual/probe callbacks are session-gated.
- Preserve three-layer probe cleanup: owner `PostMessage`, owner work queue, then forced watchdog. Every path revokes OLE, destroys the HWND, removes registry ownership, and disposes timers.
- The existing Dynamic Island is also the Quick Panel. Preserve configurable default `Win+Shift+Space`, file/image/text/URL drag-out, manual/Share intake, Drop Batch metadata/actions, explicit best-effort exclusions, and schema-9 per-monitor `Automatic`/`Custom` DIP coordinates keyed by stable DisplayConfig-derived monitor identity. Runtime clamping must never overwrite saved coordinates. The settings-page Adjust Position action is transient and no-activate: it converts physical pointer deltas to monitor-local DIP, suppresses Smart Drag candidate creation, commits once on release, and rolls back on Escape.
- `MonitorLayoutService` exposes a persistent `MonitorDescriptor.Id` from `DisplayIdentityService`; the HMONITOR `Handle` is runtime-only, and an explicitly marked fallback is used if DisplayConfig identity resolution is unavailable. Legacy schema-8 placement keys may be mapped only when they match a currently enumerated runtime handle; unresolved keys must not block startup.
- Session IDs gate probe/timeout/release/OLE/mode/display/shutdown races. Never log a dragged path, filename or payload.

### 6.5 Clipboard history

Clipboard-related changes must not break drag-and-drop state, island lifetime, or persistence behavior.

Treat file space state and clipboard state as separate concerns unless the architecture intentionally unifies them.

### 6.6 Branding

When existing official brand assets are used:

- do not alter the intrinsic logo geometry
- do not arbitrarily recolor the logo
- do not remove intentional logo glow/effects
- do not distort proportions
- do not redraw the mark from scratch unless explicitly asked

Background/layout treatment may change when the task permits it.

---

## 7. Versioning and release invariants

### 7.1 Single version source

Treat `RELEASE_VERSION` as the repository's manual version source unless the current repo has intentionally migrated to another authoritative mechanism.

Expected formats include:

- Stable: `vMAJOR.MINOR.PATCH`
- Preview: `vMAJOR.MINOR.PATCH-preview.N`

Do not invent an incompatible version format.

### 7.2 Matching release notes

Every public release must have matching release notes using the repository convention, currently:

- `.github/release-notes/<tag>.md`

If the release workflow requires this file, a release is not ready until it exists and matches the exact tag.

### 7.3 Never reuse tags

Never overwrite, retag, delete, or republish an existing public version merely to make a new build fit the old number.

For a new Preview:

1. inspect `RELEASE_VERSION`
2. inspect remote tags
3. inspect GitHub Releases
4. choose the next unused Preview sequence
5. update `RELEASE_VERSION`
6. create matching release notes

If `vX.Y.Z-preview.N` already exists anywhere publicly, use a later unused `N`.

### 7.4 Stable versus Preview update semantics

Preserve project policy unless explicitly changed:

- Stable channel receives Stable releases only
- Preview channel may receive both Preview and Stable releases
- Preview-channel selection must be based on correct SemVer/version ordering, so a newer Stable release is not ignored merely because the user opted into Preview

Do not implement naive lexical string comparison for versions.

### 7.5 Generated version metadata

Current scripts may derive:

- semantic version
- file version
- package version
- version code
- informational version
- prerelease flag
- `make_latest` behavior

Do not duplicate version derivation independently across multiple scripts. Prefer the repository's shared release-version helper.

### 7.6 Runtime version must match shipped binary

The app should derive its running version from actual built assembly/package metadata rather than a hand-maintained UI string.

After packaging, verify version metadata on the produced binaries/packages where the existing scripts/tests support it.

---

## 8. Release architecture

The intended production flow is conceptually:

`RELEASE_VERSION + release notes`

→ build/version scripts

→ app binaries/packages

→ checksums and update manifest

→ GitHub Release

→ website release synchronization

→ GitHub Pages

→ app/website consumers

The exact workflow files may evolve, but preserve this separation of responsibilities.

---

## 9. Public GitHub Release contract

A normal public release should expose the expected public assets from the GitHub Release itself.

Expected asset set unless current release policy intentionally changes:

- `DropSpaceSetup.exe`
- `DropSpace.exe`
- `DropSpace-x64.msix`
- `SHA256SUMS.txt`
- `update-manifest.json`

The release should also have:

- correct tag
- correct release title/name
- correct release notes
- correct prerelease status
- correct latest-release behavior for Stable versus Preview

Do not treat Actions artifacts as a substitute for missing GitHub Release assets.

---

## 10. `update-manifest.json`

The updater manifest is security-sensitive.

Current schema expectations may include:

- `schemaVersion`
- `channel`
- `version`
- `versionCode`
- `publishedAt`
- `minimumWindowsBuild`
- `mandatory`
- `summary`
- installer metadata
- portable metadata
- asset sizes
- SHA-256 values

Before changing the manifest schema:

- inspect all app consumers
- inspect website/release consumers
- inspect manifest tests
- preserve backwards compatibility unless the user explicitly approves a breaking updater change

### 10.1 Release summary quality

Do not leave release-specific update text accidentally hardcoded to an old preview family.

If `New-UpdateManifest.ps1` or equivalent generates a generic/hardcoded summary, prefer a single release-specific source such as:

- release-note metadata/front matter
- a dedicated release summary field/file
- an explicit workflow input derived from the matching release notes

Do not create another per-release manual edit if the same information can safely derive from the existing release notes.

The ideal release process should need only the version and its matching release notes as manual release metadata.

---

## 11. App update sources

The app should retain resilient dual-source update metadata unless explicitly redesigned.

Primary source:

- official website API: `https://airanluo-dot.github.io/DropSpace/api/v1/releases.json`

Fallback source:

- official GitHub Releases REST API for `airanluo-dot/DropSpace`

The updater may then retrieve the selected release's `update-manifest.json` and public binary assets from the same GitHub Release.

Do not remove fallback behavior as part of website-only work.

Do not silently change update trust boundaries.

---

## 12. Updater security invariants

Update metadata is a code-delivery security boundary.

Preserve or strengthen all of the following:

- HTTPS only
- exact trusted host checks where applicable
- exact official repository owner/name checks
- exact release URL shape validation
- asset URLs must belong to the same official repository/release
- duplicate asset names should be rejected
- malformed metadata should fail validation
- unsupported schema versions should fail safely
- do not follow metadata that redirects executable downloads to arbitrary third-party hosts
- preserve checksum verification
- preserve signing verification when signing is enabled

Website convenience is never a reason to weaken updater validation.

---

## 13. Website release data architecture

The official website is deployed through GitHub Pages unless current production workflows prove otherwise.

The intended production data path is:

GitHub Releases API

→ release synchronization script

→ strict release contract normalization/validation

→ build-generated release JSON

→ website pages/download links/changelog

→ GitHub Pages

The public API consumed by the app is:

- `/DropSpace/api/v1/releases.json`

A build may also expose a more complete release data JSON for website use.

---

## 14. Mandatory production Fail-Closed rule

This is a non-negotiable production invariant.

### 14.1 GitHub Releases is the authoritative production release source

When deploying the official website, the deployment must obtain current release metadata from GitHub Releases and validate it successfully.

If the production synchronization step encounters any of the following:

- network failure
- timeout
- HTTP 4xx/5xx that prevents authoritative retrieval
- rate limit with no valid authoritative response
- invalid JSON
- invalid release contract
- invalid release URL
- invalid asset URL
- duplicate/ambiguous asset metadata
- missing required release data
- missing required Stable release when current policy requires one
- other validation failure

then the deployment must fail.

### 14.2 Forbidden production fallback

Production deployment must not silently read an old committed file such as:

- `website/_source/data/releases.json`

and then deploy it as though it were current release metadata.

The correct behavior is:

- fail the new deployment
- keep the previously successful GitHub Pages deployment live
- surface the error in CI

A previous live site that is slightly older but known-good is preferable to a newly deployed site containing stale or regressed release metadata.

### 14.3 Fixture exception

A committed `releases.json` may remain if it is useful for:

- local development
- deterministic tests
- browser tests
- offline fixtures

But fixture use must be explicit.

Production must never reach it through an implicit catch/fallback path.

### 14.4 Workflow enforcement

The production website workflow must not mask release-sync failure with constructs such as:

- `continue-on-error: true`
- `|| true`
- swallowed exceptions
- unconditional fallback builds

The sync step must fail the job when authoritative production metadata cannot be obtained.

---

## 15. Website release API contract

Preserve the current schema unless intentionally versioning it.

Current contract is conceptually:

```json
{
  "schemaVersion": 1,
  "generatedAt": "...",
  "source": "github-releases",
  "releases": [
    {
      "tagName": "...",
      "name": "...",
      "body": "...",
      "isDraft": false,
      "isPrerelease": true,
      "publishedAt": "...",
      "htmlUrl": "...",
      "assets": [
        {
          "name": "...",
          "size": 0,
          "downloadUrl": "..."
        }
      ]
    }
  ]
}
```

Key invariants:

- schema remains versioned
- only official DropSpace GitHub release URLs are accepted
- only official same-release GitHub asset URLs are accepted
- draft releases must not accidentally become normal update candidates
- release count limits and duplicate checks should remain bounded

If a schema change is necessary, design migration/backward compatibility explicitly rather than changing fields in place.

---

## 16. Obsolete Cloudflare release API

The project previously contained a Cloudflare Pages Function implementation for the release API, historically at or near:

- `website/_source/functions/api/v1/releases.js`

The official site is GitHub Pages, so obsolete Cloudflare-only release API code should not remain unless current production configuration proves it is intentionally used.

When cleaning it:

1. search all workflows/config/tests/docs for references
2. verify no active deployment depends on it
3. delete the obsolete implementation
4. delete empty parent directories
5. remove stale Cloudflare-only comments/config related solely to that implementation
6. do not delete unrelated Cloudflare functionality without evidence
7. do not create a new Cloudflare deployment unless the user explicitly asks

Never resurrect the old Cloudflare function as a workaround for GitHub Pages synchronization problems.

---

## 17. Website browser runtime behavior

The browser-side site may fetch `/api/v1/releases.json` after load to refresh:

- latest Stable
- latest Preview
- installer link
- portable link
- MSIX link
- checksums link
- release link
- changelog

Preserve graceful browser behavior if the runtime fetch fails:

- the already built page should remain usable
- do not erase valid built content merely because a client-side refresh request failed

This is different from production deployment Fail-Closed behavior.

Production build/deploy must be authoritative and fail closed.
Browser runtime refresh should fail gracefully and preserve the last built valid page.

---

## 18. Website deployment triggers

When release architecture changes, verify that the website workflow still deploys on the events that project policy requires, such as:

- relevant main-branch website changes
- manual dispatch
- release publication/edit/release/prerelease events as appropriate
- explicit post-release trigger from the release workflow if that is the repository's chosen mechanism

Avoid accidental duplicate deployments where practical, but correctness is more important than minimizing a harmless duplicate run.

For a release-driven deploy, verify that the newly created GitHub Release is visible to the synchronization step before expecting the site to expose it.

---

## 19. Release body and official website link

If the repository workflow appends the official website URL to release bodies, preserve that behavior.

Do not repeatedly append duplicate website URLs when a release is edited or the workflow reruns.

Use idempotent release-body modification.

---

## 20. GitHub Actions artifacts versus GitHub Release assets

This distinction is critical.

### 20.1 GitHub Release assets

These are public shipping files and historical release records.

Do not delete them during CI cleanup unless the user explicitly requests removal of a specific public release and understands the consequence.

### 20.2 GitHub Actions artifacts

These are CI/workflow intermediates such as:

- portable build artifacts
- unsigned MSIX artifacts
- installer build artifacts
- identity package artifacts
- test result bundles
- release staging bundles
- Pages build artifacts

They are not the updater's long-term download source.

### 20.3 Retention policy

When storage growth becomes a problem, prefer short explicit `retention-days` for disposable Actions artifacts.

Typical safe candidates may use 1–3 days if they are no longer needed after successful release publication.

Before shortening retention:

- identify any downstream job that downloads the artifact
- ensure retention lasts long enough for that workflow/run
- preserve artifacts intentionally needed for debugging or compliance

Do not shorten public GitHub Release asset lifetime as a storage optimization.

### 20.4 Existing artifact cleanup

If asked to delete historical Actions artifacts:

- preserve all GitHub Releases, tags, and public Release assets unless explicitly told otherwise
- prefer deleting expired/redundant CI artifacts
- caches can generally be regenerated, but clearing them may slow the next build
- report exactly what categories were deleted
- do not claim storage reduction without measuring or retrieving post-cleanup state when possible

---

## 21. Build/package responsibilities

Current build scripts may include separate logic for:

- portable EXE
- installer
- unsigned MSIX
- identity package
- release metadata
- checksums

When modifying one packaging path, verify the others still consume the same version source and produce consistent product metadata.

### 21.1 Portable build

Verify:

- correct architecture
- correct runtime behavior on a clean target where practical
- correct version metadata
- no unexpected development-runtime dependency

### 21.2 Installer

Verify:

- correct bundled app
- correct version
- upgrade behavior does not unnecessarily break existing installs
- uninstall behavior remains valid
- installer filename matches release/update expectations

### 21.3 MSIX

Verify:

- package version matches release derivation
- identity/certificate requirements are understood
- unsigned Preview behavior is not confused with production signing

### 21.4 Signing

If signing secrets are configured:

- preserve signing steps
- verify signatures after signing
- do not print private keys, certificates, tokens, or secrets

If signing is not configured:

- do not fake a signed result
- allow existing unsigned Preview policy if that is the current repository behavior
- clearly report that signing was not performed

---

## 22. Testing philosophy

Run the smallest high-signal tests first, then broader validation before publishing.

A shipping change should generally progress through:

1. targeted unit tests
2. targeted regression tests
3. build of affected project
4. broader test suite
5. website tests if release/site behavior changed
6. release/build validation if shipping artifacts changed
7. CI validation
8. live post-deployment verification

Do not publish merely because compilation succeeded.

---

## 23. Mandatory regression tests for release synchronization

Maintain tests that prove production website sync is fail-closed.

At minimum cover:

1. successful GitHub Releases response syncs current Stable/Preview data
2. network failure causes production sync to exit non-zero
3. HTTP 500 causes production sync to fail
4. invalid JSON causes production sync to fail
5. invalid release URL causes validation failure
6. invalid asset URL causes validation failure
7. duplicate asset names cause validation failure if contract forbids them
8. stale committed fixture exists but production fetch fails → production still fails rather than using fixture
9. explicit local/test fixture mode works only when intentionally selected
10. built `dist/api/v1/releases.json` conforms to the existing schema contract
11. browser code still resolves Stable/Preview/download/changelog correctly

A future refactor must not remove the stale-fallback regression test without replacing it with equivalent coverage.

---

## 24. Task playbook: app feature

For a normal app feature:

1. inspect relevant architecture and tests
2. identify product invariants affected
3. implement minimally
4. add/update tests
5. build relevant app targets
6. verify no drag/window/update regressions if adjacent
7. if this is intended to ship, prepare the next Preview according to project policy
8. publish through normal workflow
9. verify release assets and update visibility
10. summarize user-visible change and technical risk

Do not bump a release for a read-only investigation.

---

## 25. Task playbook: bug fix

For a bug fix:

1. reproduce from code/test/log evidence when possible
2. identify root cause, not only symptom
3. write or update a regression test before/with the fix when practical
4. make focused changes
5. run targeted tests
6. run adjacent regression tests
7. build/package if shipping behavior changed
8. publish a Preview if the fix is meant to ship now
9. verify live release/update path

Do not rewrite a stable subsystem merely because a localized fix is available.

---

## 26. Task playbook: drag-and-drop / smart detection

For drag behavior:

1. inspect current state machine and HWND/XAML/OLE interaction path
2. separate source evidence, exact-item evidence, generic threshold evidence and authoritative OLE data evidence
3. preserve working Explorer/Desktop and strong accessibility fast paths while extending compatibility without provider-name rules
4. keep observing unknown press origins until threshold/release; never treat a simple held button as a verified file drag
5. verify the typed probe options, real hollow Region, no-activate/tool-window/topmost styles, single ownership, 60 ms hard cleanup, `DROPEFFECT_NONE`, callback-posted disposal and double-dispose
6. verify shared query-only classification for `CF_HDROP`, Shell IDList, virtual descriptors and unsupported data; do not read virtual content during verification; simulate `PostMessage` failure and prove fallback/watchdog cleanup
7. verify virtual-file materialization only after Drop: indexed bounded streams, confined staging, duplicate-safe names, cancellation, async-capability completion, and whole-batch rollback
8. test speculative reveal commit/reject/timeout, cancellation, rapid movement and visible Overlay handoff/drop acceptance
9. test stale-session races, mode/display changes, shutdown during a probe, repeated drags and 1,000-session/probe resource cleanup
10. test multi-monitor/DPI, focus, taskbar/Alt+Tab absence, cursor feedback and false-reveal latency on real Windows
11. test actual Explorer/Desktop plus available third-party/virtual-file providers and record versions/results without claiming automation proves cross-process compatibility
12. test Windows Drop Tray interaction if relevant
13. keep Classic as an explicit compatibility fallback only; never add a permanent Smart edge/full-screen target, polling, input suppression, injection or elevation

Do not modify smart-detection behavior during unrelated website/release tasks.

---

## 27. Task playbook: updater change

For updater work:

1. inspect both metadata sources
2. inspect selector/channel ordering
3. inspect manifest parsing
4. inspect URL/host/repository validation
5. inspect checksum/signature path
6. add tests for malformed and malicious metadata
7. preserve Stable/Preview semantics
8. test website-source failure → GitHub fallback
9. test empty/invalid source handling
10. test same-release asset trust validation
11. test version comparison edge cases
12. build and publish only after compatibility validation

Security validation must not be weakened to make a test fixture easier to use.

---

## 28. Task playbook: website-only change

For a website design/content change that does not change release metadata:

1. inspect current site build structure
2. preserve official download/API URLs
3. run site tests
4. build the site
5. verify responsive/browser behavior where supported
6. deploy through GitHub Pages workflow if requested/shipping
7. verify live page

Do not modify app updater code unless the website change actually requires it.

---

## 29. Task playbook: website release API change

For release metadata/site API work:

1. inspect sync/contract/build/runtime/workflow files
2. preserve GitHub Releases as the only production release truth
3. enforce production Fail-Closed behavior
4. keep fixture behavior explicitly local/test only
5. preserve strict URL validation
6. preserve schema compatibility
7. remove obsolete Cloudflare release API code if still present and unused
8. run sync contract tests
9. run website tests
10. build site
11. publish a new Preview if the repository policy treats this as a shipping release-system change
12. deploy Pages after release publication
13. verify the live API and site

---

## 30. Task playbook: new Preview release

When publishing the next Preview:

1. synchronize with current `main`
2. verify working tree is intentional
3. read `RELEASE_VERSION`
4. list relevant existing tags/releases
5. choose next unused Preview tag
6. update `RELEASE_VERSION`
7. create `.github/release-notes/<tag>.md`
8. ensure release notes describe actual user-visible/technical changes
9. run relevant tests/builds
10. commit and push through repository policy
11. merge to `main` if a PR flow is required
12. let/trigger the release workflow
13. inspect Actions failures if any
14. verify GitHub Release exists
15. verify prerelease flag is correct
16. verify all expected public assets exist
17. verify `update-manifest.json`
18. verify checksums
19. trigger/verify website deploy
20. verify live `/api/v1/releases.json` contains the new Preview
21. verify live site latest Preview/download links
22. verify app update-source compatibility from metadata
23. report commit, PR, tag, release, workflow, and site result

Never reuse a failed public tag. Fix forward with a new version if the old release has already been published publicly and immutability policy applies.

---

## 31. Task playbook: Stable release

A Stable release requires more caution than Preview.

Before Stable:

- verify the exact target version is intended
- verify the release notes are complete
- verify Preview testing status from available evidence
- run the full relevant test/build matrix
- verify update-channel selection semantics
- verify installer/portable/MSIX assets
- verify checksum/manifest consistency
- verify stable prerelease flag is false
- verify `make_latest` behavior follows repository policy
- verify website latest Stable state

Do not silently promote a Preview to Stable simply by renaming a tag.

Stable should be built from the intended committed source/version and published as its own release according to workflow policy.

---

## 32. Task playbook: CI failure

If GitHub Actions fails:

1. inspect the failing workflow/run/job
2. read the relevant log rather than guessing
3. separate infrastructure/transient failures from source failures
4. fix source only when source is the cause
5. rerun targeted local tests if possible
6. push focused fix
7. recheck CI

Examples of transient/external failures:

- GitHub outage
- package registry outage
- rate limit
- runner provisioning problem
- transient network failure

Do not change production logic merely to conceal a transient CI problem.

---

## 33. Task playbook: production website sync failure

If official website deployment fails because GitHub Releases metadata cannot be fetched/validated:

- do not restore stale production fallback
- confirm the previous successful Pages deployment remains live
- identify whether failure is transient API/network versus contract/data corruption
- if transient, rerun the deployment when appropriate
- if contract/data problem, fix the invalid release metadata or validator incompatibility
- do not deploy a stale fixture to make the workflow green

Fail-Closed is working as designed when an invalid production release sync stops deployment.

---

## 34. Task playbook: release published but website is stale

If GitHub Release is correct but the website/API is stale:

1. verify the new Release is publicly visible
2. inspect website deploy trigger
3. inspect sync logs
4. verify production fetched GitHub data rather than fixture
5. inspect generated `dist/api/v1/releases.json`
6. verify Pages deployment succeeded
7. inspect live endpoint without relying only on browser cache
8. verify runtime script behavior separately from built HTML

Do not edit the live JSON manually.

Fix the pipeline/source and redeploy.

---

## 35. Task playbook: release workflow succeeded but asset is missing

Treat missing public assets as a release integrity problem.

1. compare expected public asset set with actual GitHub Release
2. inspect publish job logs
3. inspect staging artifact contents
4. inspect file-generation/signing/checksum steps
5. determine whether release can be completed safely without violating immutability/project policy
6. if the project treats releases as immutable, publish a fixed next Preview rather than silently mutating historical artifacts

Never point updater metadata at an asset that does not exist.

---

## 36. Task playbook: version mismatch

If `RELEASE_VERSION`, binary metadata, Git tag, Release tag, manifest version, or website metadata disagree:

Stop publication until the mismatch is understood.

Do not "pick one" arbitrarily.

Trace derivation from:

- `RELEASE_VERSION`
- release helper scripts
- build arguments
- generated package metadata
- manifest generation
- release workflow inputs
- site sync output

Fix the earliest incorrect source in the chain, then regenerate downstream artifacts.

---

## 37. Task playbook: obsolete code cleanup

For cleanup:

1. prove code/config is unused through search + workflow references + build/test paths
2. remove it
3. remove dead imports/config/comments
4. remove empty directories
5. run tests/builds that could have referenced it
6. do not combine broad unrelated refactors with cleanup unless requested

Historical code being old is not enough evidence that it is unused.

---

## 38. Task playbook: GitHub Actions storage cleanup

When asked to reduce GitHub storage:

1. inventory Actions artifacts by name/count/age/size
2. inventory caches
3. separate CI artifacts from GitHub Release assets
4. identify which artifacts are safe to delete
5. preserve public releases and assets
6. delete redundant historical CI artifacts if explicitly authorized
7. set future artifact retention to a short explicit period where safe
8. ensure downstream jobs still have enough time to consume artifacts
9. optionally clear regenerable caches if requested
10. measure/report post-cleanup state when available

Do not delete source history, tags, Releases, or public historical installers as a side effect of storage cleanup.

---

## 39. Branch, commit, PR, and merge behavior

Follow repository protections and existing workflow.

Preferred pattern for non-trivial changes:

- create/update a focused branch
- commit logically grouped changes
- push
- open/update PR when repository policy uses PRs
- run checks
- merge after checks pass if the user has asked for end-to-end execution and permissions allow it

Do not bypass branch protection.

Do not force-push shared branches unless explicitly necessary and safe.

Do not rewrite public release tags.

Commit messages should describe the actual change rather than generic "update" wording.

---

## 40. Secrets and credentials

Never print or commit:

- GitHub tokens
- signing keys
- certificates/private keys
- Actions secrets
- API credentials
- deployment credentials

If a required secret is unavailable:

- complete all work that does not require it
- leave the repo buildable where possible
- report the exact blocked stage
- do not invent success

Do not disable security checks simply because a secret is unavailable.

---

## 41. Destructive action boundaries

Project maintenance may involve deletion, but preserve irreversible public history by default.

Safe to consider deleting when proven unused or explicitly requested:

- obsolete source files
- empty directories
- disposable Actions artifacts
- regenerable caches
- dead CI configuration

Do not delete without explicit task justification:

- GitHub Releases
- Git tags
- public Release assets
- source branches with active work
- user data
- signing material

If the user explicitly requests destructive public-history removal, state the exact target and consequence before executing when the environment requires confirmation.

---

## 42. Rollback and recovery philosophy

Prefer fix-forward for published application releases.

For website deployments:

- failed new deployment should leave last successful Pages deployment live
- do not deploy stale metadata as a rollback mechanism
- if a bad website commit is live, revert the source commit or deploy a known-good source commit through the normal pipeline

For app releases:

- do not rewrite a published binary under the same version if immutability is expected
- publish a new corrected Preview version
- if a Stable release is critically broken, follow explicit project emergency-release policy and preserve historical transparency

---

## 43. Idempotency requirements

Automation should tolerate reruns.

Examples:

- appending website link to Release body must not duplicate it
- site deployment rerun must regenerate the same metadata from the same GitHub Releases state
- checksum generation must be deterministic for unchanged files
- release selection must not depend on API return order when semantic ordering is required
- cleanup scripts should safely ignore already-removed targets

A workflow retry should not corrupt release state.

---

## 44. API/network robustness

When consuming GitHub APIs:

- use bounded timeouts
- handle non-success HTTP status explicitly
- validate parsed JSON before use
- avoid unbounded response sizes
- preserve rate-limit error visibility
- do not silently convert network failures into stale-success behavior
- set a clear User-Agent where GitHub API conventions require it

Retries may be appropriate for transient failures, but after bounded retries the production release sync must still fail closed.

Do not retry indefinitely.

---

## 45. Release ordering robustness

Do not assume API list order is always the desired update order.

Use explicit release/version semantics for selecting:

- latest Stable
- latest Preview
- candidate update for current channel

Account for:

- prerelease flags
- drafts
- duplicate/malformed tags
- releases published out of chronological order
- Stable versions newer than Preview versions

Reject or ignore malformed versions according to existing contract/tests rather than accidentally sorting them lexically.

---

## 46. Live verification requirements

For a release task, verify the live system after workflows complete.

Minimum verification matrix:

### GitHub Release

- tag exists
- release exists
- prerelease/stable flag correct
- release body correct
- expected assets present

### Update assets

- `update-manifest.json` exists
- checksum file exists
- manifest references expected asset names
- manifest version matches Release
- checksum metadata is internally consistent where testable

### Official website API

- endpoint returns successfully
- schema version is expected
- new release appears
- Stable/Preview classification is correct
- release URLs are official
- asset URLs are official

### Official website

- latest Stable label/link correct
- latest Preview label/link correct
- installer link correct
- portable link correct
- MSIX link correct
- checksum link correct
- changelog/release information updated

### App compatibility

- website metadata remains acceptable to app validator
- GitHub fallback remains present
- version/channel selection remains compatible

Do not mark the task fully complete if live verification was part of the request and was not possible. Report the exact unverified item.

---

## 47. Release notes quality

Release notes should describe what actually shipped.

Prefer:

- concise user-visible summary
- important behavioral changes
- known compatibility caveats
- release/update infrastructure changes when relevant

Avoid:

- claiming a bug is fixed without test evidence
- copying internal implementation chatter verbatim
- stale references to previous Preview behavior
- mentioning removed Notch mode as still available

For infrastructure-only Preview releases, explain why the release matters to reliability/security rather than pretending there is a new end-user feature.

---

## 48. Documentation consistency

When a change affects user-facing behavior, update all relevant documentation in the same task.

Search for stale statements in:

- README
- website copy
- release notes
- settings descriptions
- in-app help/about text
- screenshots/alt text where applicable

Examples of high-risk stale claims:

- Notch mode still exists
- old update channel semantics
- obsolete download filename
- obsolete website endpoint
- Cloudflare Pages as current production API

Do not edit unrelated prose just for style.

---

## 49. Dependency and environment policy

Use the Codex/cloud development environment to perform required builds/tests when possible.

Do not require the end user to install development dependencies on their personal computer merely to complete repository work.

It is acceptable for Codex's own environment to install normal project dependencies needed for verification, subject to repository/security policy.

Avoid introducing new runtime dependencies unless justified.

---

## 50. Performance and UX regressions

For UI/window/drag changes, consider:

- CPU while idle
- polling frequency
- unnecessary global hooks
- hidden-window lifetime
- excessive topmost windows
- animation interruption
- repeated state transitions
- memory leaks
- input latency

A fix that makes drag detection reliable by permanently burning CPU or continuously obstructing the screen is not acceptable.

Prefer event-driven or bounded detection where feasible.

---

## 51. Accessibility and localization

When modifying user-facing UI/site text:

- preserve existing localization structure
- do not hardcode one language into shared runtime logic if localization already exists
- ensure new controls have meaningful labels where the framework supports accessibility
- preserve keyboard/focus behavior even though DropSpace avoids stealing focus

For DropSpace's shipped App resources:

- treat `src/DropSpace.App/Strings/en-US/Resources.resw` as the complete base and keep `zh-CN` resource keys exactly synchronized;
- keep user-facing Chinese out of production `.cs` and `.xaml`; resource files are the translation boundary;
- trace main window, Dynamic Island, native tray, errors, automation names, and update feedback together rather than localizing only page labels;
- preserve the startup-scoped `System`/`English`/`SimplifiedChinese` preference: System maps a Chinese Windows display language to `zh-CN` and falls back to English for not-yet-shipped languages;
- for portable single-file builds, stage `Strings`, XAML, and assets while excluding `Package.appxmanifest` before MakePri so the unpackaged PRI keeps the `Application` root; remove the config `packaging` section, bundle a non-default `DropSpace.resources.pri` with the EXE, and open it only through an explicit resource context so it cannot replace WinUI's default `resources.pri` or starve framework XAML/theme lookup;
- for scripts invoked through `powershell.exe`, use Windows PowerShell/.NET Framework-compatible APIs; do not use newer `System.IO.Path.GetRelativePath` there;
- do not use `ApplicationLanguages.PrimaryLanguageOverride` or implicit `x:Uid` lookup in the unpackaged app; use the app-owned XAML resource override for dependency-object resource identifiers so the selected explicit resource context applies properties and automation names after XAML construction, and apply `Window` roots directly because they are not dependency objects;
- apply a `TitleBar`'s resource values only from `Loaded`: it has no `XamlRoot` during the window constructor even though the `Window` title can be set after `InitializeComponent`;
- preserve the original exception in executable smoke failures: never open a recovery `ContentDialog` before its page has a `XamlRoot`, and let smoke mode report/exit rather than masking the root cause with a second UI exception;
- when resolving MRT map paths for XAML automation resources, convert property-separator dots to slashes but preserve dots inside `[using:…]` type qualifiers; otherwise accessibility names silently miss their resources;
- register the custom XAML attached property from the `App` constructor before any XAML page that uses it is parsed; late registration causes a runtime `XamlParseException` even when compilation succeeds;
- keep the attached-property provider as a constructible `DependencyObject` service type with static `Get`/`Set` accessors, matching the WinUI custom-attached-property pattern;
- run resource parity and CJK-hardcoding guards plus `en-US`/`zh-CN` resource-context CI. Do not equate that with changing the hosted runner's OS display language; retain real English and Simplified Chinese Windows 11 evidence as a manual release gate.

For the website, preserve independent localized routes if the current site architecture uses them.

---

## 52. Common mistakes to avoid

Do not:

- publish from an outdated branch
- assume remembered Preview number is current
- reuse an existing tag
- change `RELEASE_VERSION` without matching release notes
- publish only CI artifacts and forget GitHub Release assets
- use Actions artifacts as updater URLs
- deploy stale `releases.json` after GitHub API failure
- weaken update URL validation
- remove GitHub API fallback while fixing website API
- reintroduce obsolete Cloudflare release function
- reintroduce Notch mode accidentally
- forget website deployment after a release
- trust only built HTML without checking live API
- trust only API without checking actual asset URLs
- declare success before Actions/Pages finish
- hide CI errors with `|| true`
- mutate old public release files to avoid bumping a Preview
- delete historical releases during artifact cleanup
- make broad unrelated refactors inside a release hotfix

---

## 53. Decision table for ambiguous situations

### GitHub API temporarily unavailable during production site deploy

Action: fail deployment, preserve previous live Pages site.

### Local development without network

Action: explicit fixture/mock mode is acceptable.

### Existing fixture is older than latest Release

Action: do not use it in production. Update or keep it only as a clearly labeled test fixture.

### Release already exists but workflow failed after publication

Action: inspect immutability policy. Prefer fix-forward with next Preview for binary/content defects; rerun idempotent post-release website deployment if only deployment failed.

### New Stable is newer than latest Preview and user is on Preview channel

Action: selector should consider the newer Stable as an update according to project policy.

### New Preview is older than user's installed Stable

Action: do not downgrade merely because Preview channel is enabled.

### Third-party drag source behaves differently from Explorer

Action: preserve working native drag path; add compatibility carefully rather than replacing stable detection wholesale.

### Signing unavailable

Action: follow existing unsigned Preview policy if allowed; do not claim signed build.

### GitHub Pages deploy fails after Release succeeds

Action: release remains canonical; fix/retry site deployment without altering the release unless release content itself is wrong.

### Website runtime API request fails in end-user browser

Action: retain usable built content; do not blank the page.

### Production build-time API request fails

Action: fail closed; do not use stale committed release data.

---

## 54. Recommended release-system quality improvements

When relevant and not already implemented, prefer the following architecture improvements:

- make version + matching release notes the only manual per-release metadata
- derive update-manifest summary from release-specific notes/metadata rather than hardcoded release-family logic
- enforce short Actions artifact retention for disposable artifacts
- keep public GitHub Release assets indefinitely according to project policy
- keep production website release sync fail-closed
- keep local fixtures explicit
- remove obsolete Cloudflare release API code
- add schema/URL/security regression tests
- preserve dual update sources
- keep post-release website synchronization automatic

Do not force these changes into unrelated tasks if they would create unnecessary risk.

---

## 55. Final reporting format

At the end of an implementation task, report only what is actually known.

Include, as applicable:

### Changes

- files modified
- files added
- files deleted
- key behavior changed

### Validation

- targeted tests run and result
- full tests/builds run and result
- CI workflow/run result

### Git

- branch
- commit SHA
- PR number/URL
- merge status

### Release

- version/tag
- GitHub Release status
- prerelease/stable status
- public asset completeness

### Website

- GitHub Pages deployment status
- live API status
- latest Stable shown by API
- latest Preview shown by API
- live download-link verification

### Cleanup

- obsolete Cloudflare code removed or reason retained
- Actions artifact retention changes
- historical artifacts/caches deleted if requested

### Residual issues

- exact blocked/unverified item
- why it remains
- whether it affects users, release integrity, or only developer convenience

Do not end with vague wording such as "should work" if stronger verification was available.

---

## 56. Mandatory Skill synchronization gate

Treat Skill freshness as part of every mutating DropSpace workflow, not as optional follow-up documentation.

Before declaring an App, website, API, updater, packaging, CI/CD, release, documentation, or repository change complete:

1. Compare the completed change with both DropSpace maintenance Skills:
   - the repository copy at `.agents/skills/dropspace-maintainer/SKILL.md` in `airanluo-dot/DropSpace`;
   - this installed personal Skill, resolved by its `dropspace-codex` frontmatter name rather than by a remembered internal directory name.
2. Update both Skills in the same task whenever the change affects product facts, architecture, invariants, paths, commands, API contracts, test expectations, release/deployment procedures, live-verification rules, or the definition of done.
3. Keep durable workflow knowledge in the Skills. Rediscover volatile version numbers and live state unless a current implementation snapshot is intentionally documented and updated with the release.
4. Validate both Skill folders after editing. Publish the repository copy through the normal DropSpace branch/PR/check/merge flow, save this installed Skill through the supported personal-skills workflow, and verify both remote results.
5. If neither Skill needs a semantic edit, explicitly report `Skill sync: verified current` in the completion report. The synchronization check is mandatory even when the outcome is no file change.
6. If either Skill changes, apply this same gate recursively: inspect and synchronize its counterpart before finishing. Updating only one copy is incomplete.
7. If one destination cannot be updated or verified, report the exact blocked destination and do not describe the overall update or release as fully complete.

Skill-only maintenance does not require an App version bump or Preview release unless the user or current repository policy explicitly requests one.

---

## 57. Definition of done

A code edit is not automatically a completed DropSpace task.

For a normal source-only task, done means:

- requested behavior implemented
- relevant tests pass
- no known adjacent regression introduced
- repository state is clean/intentional
- Skill synchronization gate completed and both remote destinations verified when an edit was required

For a shipping Preview task, done additionally means:

- unique version chosen
- `RELEASE_VERSION` correct
- matching release notes exist
- release workflow passes
- GitHub Release exists
- expected public assets exist
- update manifest/checksums exist
- website deploy succeeds
- live website API contains the release
- live site points at correct assets
- results are reported with concrete identifiers

For a production website release-data task, done additionally means:

- GitHub Releases remains the sole production release truth
- production synchronization fails closed
- no stale fixture fallback can deploy old release metadata
- release contract/security tests pass
- obsolete Cloudflare API implementation is absent unless proven intentionally active

---

## 58. Prime directive

Optimize for correctness, recoverability, and verified shipping behavior.

The project should have one understandable release truth:

**GitHub Release is authoritative.**

Everything else should derive from it or safely fall back to another official DropSpace source without weakening trust.

When authoritative production release metadata cannot be verified, fail safely rather than publishing stale or guessed state.

## v0.3.0-preview.6 durable product facts

- The 3.0 Preview feature slice is one inseparable Preview release: bounded Quick Preview providers, source-safe Quick Actions, Windows-only DropLink v1, opt-in cross-device clipboard, Nearby browser Share, and client-encrypted Internet Share.
- DropLink v1 uses HTTPS/Kestrel with a DPAPI-protected ECDSA identity, pinned certificate fingerprint, ECDH/HKDF pairing, SAS confirmation, HMAC request authentication, 4 MiB chunks, staging, whole-file integrity, explicit receive approval, and schema-2 `paired_devices`/`transfer_sessions` metadata.
- DropLink transfer sessions expose explicit cancellation and retain bounded status/chunk state for reconnect-aware handoff; an accepted receiver commits only after whole-file integrity verification.
- Cross-device clipboard reuses the event-driven watcher; supported automatic modes are bounded text/URL and image modes with a 10,000-entry/24-hour content-hash loop guard. No polling watcher, stale overwrite, or unbounded payload is acceptable.
- Nearby Share is private-IPv4-only, tokenized with 24 random bytes, expiring, receiver-capped, range-capable, and revocable. Internet Share is unavailable unless an explicitly configured HTTPS backend exists; `share-worker/` is a reference Cloudflare Worker/R2 implementation, not proof of deployment.
- Full Preview renders bounded image/PDF/text content and non-autoplaying media; media playback is stopped and disposed when the preview closes.
- Native macOS/iOS/iPadOS/Android/Linux clients, accounts, WebRTC, AirDrop, Universal Clipboard, and cloud account sync remain out of scope. Never advertise a fake action, backend, firewall success, or release asset.

## v0.3.0-preview.7 durable product facts

- Preview.7 hardens the same bounded 3.0 slice: standard mDNS/DNS-SD discovery on `224.0.0.251:5353` with `_dropspace._tcp.local` PTR/SRV/TXT/A records, bilateral DropLink SAS confirmation from one canonical device-ordered transcript, typed text/URL handoff, clipboard-pause enforcement, native bounded PDF/media preview, capability-driven actions, and canonical encrypted-share framing.
- Text/URL handoff is an authenticated, replay-guarded message contract with bounded UTF-8 payloads (1 MiB text, 32 KiB URL), normalized HTTPS/HTTP URLs, sender/session binding, freshness validation, and no implicit receiver clipboard write. Clipboard capture/import remains event-driven and pause is a commit barrier.
- Pairing must keep the pending secret ephemeral until both peers explicitly confirm the same SAS; malformed, rejected, cancelled, expired, and failed sessions must clean up without persisting trust.
- PDF preview uses the Windows `Windows.Data.Pdf` renderer with bounded page dimensions/pixels/output and cancellation; media preview is non-autoplaying and releases its player when closed. Image conversion re-encodes to a DropSpace-owned output and does not mutate the source.
- Encrypted Internet Share uses fixed cross-language framing: manifest `nonce | ciphertext | tag`, chunks `ciphertext | tag`, explicit GUID wire bytes, and HKDF/AES-256-GCM test vectors. Backend sessions include an origin-bound HTTPS revoke URL and explicit DELETE revocation. `share-worker/` is still reference code, not proof of deployment.
- Preview.7 is CONDITIONAL until the Windows CI/release jobs, two real Windows devices for LAN pairing/clipboard/reconnect, and an operator-deployed Worker/browser acceptance are evidenced. Never infer those gates from source inspection or hosted Linux checks.

## v0.3.0-preview.8 durable product facts

- Preview.8 lowers the declared runtime baseline to 64-bit Windows 10 version 1809 (Build 17763) and later, including Windows 11. It keeps the pinned Windows SDK Build Tools 10.0.26100.8249, Microsoft.WindowsAppSDK 2.3.1, and the existing three-layer architecture.
- `DropSpace.Core.Compatibility` owns the shared minimum-build policy, compatibility status values, and probe contracts. `DropSpace.App` reads the real kernel build, checks the Windows App SDK XAML runtime, and blocks unsupported direct launches with a diagnostic marker/native message; installer, MSIX, identity, and update manifests declare the same minimum.
- `WindowsCompatibilityService` gates Mica and modern DWM corner/border attributes at runtime. Windows 10 keeps the opaque theme-resource base visual, borderless overlay, no-activate/topmost/empty-idle behavior, Smart Drag v2, Classic fallback, placement, clipboard, and updater contracts.
- PDF, media, and Windows Share API availability is reported as capability state. Preview UI must fall back to bounded text/metadata when optional PDF/media APIs are unavailable; an absent optional capability must never become an implicit startup or drop-path claim.
- `scripts/Test-WindowsCompatibility.ps1` is a required CI and release gate for target/minimum drift, manifest/installer/update policy drift, direct Mica XAML, guarded DWM attributes, startup wiring, and baseline documentation. The portable smoke marker records the detected build, runtime status, and optional capability outcomes.
- `compatibility-baseline.md` and `docs/test-plan/v0.3.0-preview.8.md` define the required Windows 10 1809/1909/20H2/22H2 and Windows 11 21H2/22H2/23H2/24H2, 100–200% DPI, one-to-three-monitor, Installer/Portable/MSIX, Explorer/provider, clipboard, preview, share, startup, and updater matrix. Hosted Windows CI and Linux static checks do not prove every row.
- Preview.8 remains CONDITIONAL until real Windows OS/build/DPI/multi-monitor/provider evidence and the existing two-device/network/browser gates are recorded. Never convert source inspection, a hosted runner, a stale website fixture, or a successful release upload into evidence for an unrun matrix row. Do not rewrite historical Preview.7 facts.

## v0.3.0-preview.9 durable product facts

- Preview.9 shell intake is one typed, bounded `--shell-add --source <explorer-context-menu|sendto> -- <paths>` contract. Installer owns only the per-user static Explorer verb and SendTo `.lnk`; Portable remains registration-free. Direct/redirected shell activation must reuse the `AppInstance` owner, keep the main window non-activated, and never log paths or copy/move source files.
- Preview.9 Quick Actions are a projection of the existing `IItemActionRegistry`, not a second action list. Four profiles (File, Image, Text, URL) default to Automatic. Custom profiles may select up to three unique IDs; unavailable selections are skipped without replacement and More is the remaining available registry projection. Main and Dynamic Island surfaces must use the same policy.
- Preview.9 Undo is one eight-second slot. Remove/Clear use schema-3 pending-delete token/expiry fields so ordinary queries hide rows while exact IDs/metadata/payload references remain restorable; expiry/finalization may delete only unreferenced DropSpace-owned payloads. Pin Undo stores prior booleans in memory. Startup recovers expired rows and shutdown finalizes the active operation.
- Preview.9 is CONDITIONAL until Windows CI and the real Explorer/SendTo, installer upgrade/uninstall, multi-selection, no-focus, DPI, and Undo manual matrix are evidenced. Do not infer those results from Linux checks or source inspection.

## v0.3.0-preview.11 durable product facts

- Preview.11 is the full-audit hardening Preview. It preserves the Windows 10 version 1809 (Build 17763) and Windows 11 baseline, Dynamic-Island-only product boundary, Smart Drag fail-closed behavior, source-safe item actions, and conditional release-evidence policy.
- Smart Drag verification re-reads the live cursor, re-resolves the monitor, and revalidates the active session/policy immediately before creating the ephemeral OLE probe. Probe completion and cleanup messages are owner-thread operations, including watchdog, queue-failure, timeout, and double-dispose paths; detector shutdown is asynchronous and bounded.
- Encrypted Internet Share revoke handles are current-user DPAPI protected and restored at startup. The reference `share-worker/` uses a Durable Object coordinator for serialized aggregate plaintext-byte/item accounting, counts pending first chunks toward item quota, validates manifest/chunk order and bounds, revokes before paginated R2 deletion, and uses per-response nonce CSP. It remains reference deployment code, not proof of an operator-deployed backend.
- Quick Action hash, QR, ZIP, image, and metadata exports share bounded/collision-safe output naming and incomplete-output cleanup. Encoded image output writes directly to the reserved destination file instead of creating a second full-size managed buffer.
- Startup update checks, incoming transfer approval notifications, pairing expiry, undo expiry, projection refresh, DNS-SD registration, detector tasks, and file logging have explicit ownership or awaited asynchronous shutdown paths. Incoming transfer HTTP offers return before UI approval is complete.
- Preview.11 remains CONDITIONAL until real Windows 10 1809/22H2, Windows 11, mixed-DPI multi-monitor, Explorer/provider, cursor-feedback, border-leak, accessibility, and complete Quick Action acceptance evidence is recorded. Hosted CI and static checks do not substitute for those cells.

## v0.3.0-preview.12 durable product facts

- Preview.12 preserves the Preview.11 Smart Drag/OLE, asynchronous lifecycle, output-safety, encrypted-share quota/revoke, DPAPI, release, and website/API synchronization boundaries.
- The encrypted-share receiver prefers the browser File System Access API (`showSaveFilePicker`) to decrypt and write one chunk at a time directly to a user-selected file. It uses incremental SHA-256 and aborts the destination when the final length or digest does not match.
- Browsers without file streaming support have an explicit 256 MiB in-memory fallback limit, and fallback downloads are serialized through temporary Blob URL cleanup; do not reintroduce unbounded or concurrent Blob aggregation for the 2 GiB server-side share limit.
- Receiver CSP remains nonce-based with no `unsafe-inline`, and display names remain text/download metadata rather than HTML. The Worker remains reference deployment code and still requires the Durable Object binding plus deployment-level abuse/rate-limit controls.
- Preview.12 remains CONDITIONAL until real Windows OS/DPI/monitor/native Smart Drag evidence and operator-deployed Worker/browser acceptance are recorded. Hosted CI and source-level receiver contract tests do not substitute for those cells.

## v0.3.0-preview.13 durable product facts

- Preview.13 keeps the Windows 10 version 1809 (Build 17763) and Windows 11 baseline, Dynamic-Island-only boundary, fail-closed Smart Drag, source-safe actions, dual update sources, GitHub Release authority, and conditional evidence policy.
- `IItemContentResolver` is the shared content boundary for Preview and Actions. It must resolve semantic image content from either a readable external file or a confined app-owned payload, carry normalized extension/MIME/size metadata, and fail closed when the source is missing, unavailable, or outside the payload root. Repository-shaped image files may remain `ItemKind.File`; extension/MIME semantics still reach Image Preview, image transforms, hash, ZIP, capabilities, and Quick Action profiles.
- Smart OLE visual authorization is `CanAcceptNow || CanMaterialize`, never `IsFileLikeEvidence` alone. `CF_HDROP` and Shell IDList classifications must resolve actual non-empty paths before `CanAcceptNow` is true. `EphemeralOleDragProbe` must route both completion and cleanup window messages to owner-thread handling and clear pending result, timers, OLE registration, HWND registry state, and native window only on that thread.
- Main-page Quick Actions pass the full current ListView selection only when the clicked card is selected; a click outside the current selection resolves to that card alone. Actions that declare `RequiresSingleItem` remain unavailable for multi-selection, while ZIP and other explicitly multi-item actions receive the whole selection.
- Global Quick Panel hotkey lifecycle uses asynchronous bounded readiness/exit signals. UI paths must not call `ManualResetEventSlim.Wait`, `Thread.Join`, or synchronous lifecycle wrappers; failed stop preserves ownership and prevents concurrent replacement.
- DropLink authorization looks up the stored secret before reserving replay state. Nonces are bounded by age, total entries, per-peer entries, and input length; unknown peers and invalid nonce shapes never populate the cache. Manual unsigned Preview installation remains allowed only after integrity verification and explicit user action; unattended install remains publisher-trust gated.
- Preview.13 remains CONDITIONAL until the Windows build/release jobs, real Windows OS/DPI/provider/multi-selection/OLE evidence, two-device DropLink evidence, and operator-deployed Worker/browser acceptance are recorded. Hosted Linux tests and source inspection do not substitute for those rows.

## v0.3.0-preview.14 durable product facts

- Preview.14 preserves the Windows 10 version 1809 (Build 17763) and Windows 11 baseline, Dynamic-Island-only boundary, fail-closed Smart Drag/OLE authorization, precise native hit-region ownership, local-first data, source-safe actions, and conditional evidence policy.
- `OverlayMotionProfileSet` separates geometry morph, surface opacity, content transition, interaction feedback, and shadow/elevation semantics. `OverlayMotionController` keeps a bounded interruptible frame contract, clamps exposed values, and limits accepted-drop feedback to inward `0.97` then return-to-`1.0` without outward expansion.
- `OverlayCompositionAnimator` owns visual-only opacity/content/hover/press channels through WinUI Composition. UI-thread layout, fixed-host placement, visibility/lifecycle, and exact OLE geometry remain authoritative; no compositor animation may reveal Smart candidates or change hidden input ownership.
- `OverlayNativeRegionController` uses a physical DPI-aware `OverlayRegionSignature` and skips repeated `SetWindowRgn`/empty-region calls. `SystemVisualPreferenceService` listens to Windows animation, advanced-effects, color/theme, and high-contrast changes without polling in the frame path.
- The transient overlay may use bounded `SystemBackdropElement` + `DesktopAcrylicBackdrop` only on capable Windows 11 with effects enabled. Windows 10, unsupported APIs, disabled effects, and high contrast use a solid fallback. Acrylic is never applied to the main window and never changes drag authorization.
- Preview.14 remains CONDITIONAL until Windows CI/release packaging, real Windows OS/DPI/multi-monitor/OLE/accessibility/performance evidence, and the existing two-device/Worker/browser gates are recorded. Hosted Linux tests and source inspection do not substitute for those rows.
