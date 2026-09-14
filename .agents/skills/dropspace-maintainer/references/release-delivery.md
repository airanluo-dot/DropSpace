# Release and delivery boundaries

Read this file only for updater, versioning, packaging, signing, GitHub Releases, CI/CD, website release metadata, GitHub Pages, or live delivery work.

The current workflows/scripts/tests are the implementation truth. This document keeps the durable release contract and completion boundaries.

## Authoritative release state

GitHub Releases in `airanluo-dot/DropSpace` are the public shipping record and the authoritative source for published binaries.

For a release task, discover current state rather than assuming it:

- current intended version: `RELEASE_VERSION`
- existing public versions: Git tags and GitHub Releases
- release notes: `.github/release-notes/<tag>.md`
- packaging/version derivation: current scripts and workflows
- minimum Windows build: shared build/compatibility source, not this document

Never hard-code a remembered Preview number in the Skill.

## Version and tag integrity

- Use the repository's current version source and version helper scripts; do not create a second independent derivation path.
- A public release needs matching release notes according to the current repository convention.
- Never reuse, retag, or silently overwrite an already published version to fit new bits under an old number.
- Resolve disagreements among version source, binary/package metadata, tag, Release, manifest, and website data before publishing.
- Preserve Stable/Preview semantics and semantic version ordering. Preview enrollment must not cause a downgrade or ignore a newer Stable merely because it is not a prerelease.

## Public asset contract

Unless the current workflow intentionally changes the contract, the public GitHub Release is expected to contain:

- `DropSpaceSetup.exe`
- `DropSpace.exe`
- `DropSpace-x64.msix`
- `SHA256SUMS.txt`
- `update-manifest.json`

GitHub Actions artifacts are build intermediates, not substitutes for public Release assets or updater URLs.

If the asset contract changes, inspect every consumer before changing this reference: updater, website, scripts, tests, release workflow, and documentation.

## Updater trust boundary

Update metadata delivers executable code. Preserve or strengthen the current trust model:

- HTTPS only
- official host/repository identity validation
- release/asset URLs constrained to the official repository and intended release
- malformed/duplicate/ambiguous metadata rejected
- unsupported schema versions rejected safely
- checksums verified
- signing/publisher verification preserved when enabled
- no arbitrary third-party executable redirect introduced for convenience

The app currently uses an official website release-data source with an official GitHub Releases fallback. Do not remove fallback or weaken validation as a side effect of website work.

Read the current updater implementation, manifest generator, selectors, trust validators, and tests before changing exact schema fields or URL rules.

## Website release data

Production website release synchronization is fail-closed.

- GitHub Releases is the authoritative production release-data source.
- Production build/deploy must fail when authoritative release data cannot be fetched or validated.
- Do not silently fall back to a stale committed fixture and deploy it as current production metadata.
- Explicit fixtures/mocks are fine for local development and deterministic tests.
- Production workflows must not hide sync failures with `continue-on-error`, `|| true`, swallowed exceptions, or unconditional fallback builds.

Browser runtime behavior is different: if an after-load refresh fails, preserve already built valid content instead of blanking a usable page.

When release/site architecture changes, verify the current workflow triggers and ensure release publication can cause the site/API to synchronize through the intended path.

## Packaging and signing

Keep installer, portable, and MSIX outputs on one coherent version/minimum-OS/product identity contract.

- Verify the produced artifact's metadata when the current scripts/tests support it.
- Installer changes must preserve sensible upgrade/uninstall behavior.
- Portable builds must not acquire an accidental developer-runtime dependency.
- MSIX identity/version requirements must remain explicit.
- If signing is configured, preserve signing and verification without exposing secrets.
- If signing is unavailable, do not fake a signed result or disable security checks to get green output.

## CI artifacts and cleanup

Separate disposable Actions data from public release history.

- It is reasonable to shorten retention for disposable workflow artifacts when downstream jobs still have time to consume them.
- Caches are regenerable but clearing them may slow the next run.
- Do not delete GitHub Releases, tags, or public Release assets as ordinary CI storage cleanup.
- Do not report storage reduction without measuring it when measurement is available and part of the task.

## Release/deployment workflow

When the user explicitly asks to ship/release/deploy, continue far enough to prove the requested public state. A typical sequence is:

1. discover current version/tags/Releases and relevant workflow state
2. prepare the unique version and matching release notes
3. run affected tests/build/release validation
4. commit/push/PR/merge according to repository policy
5. run or observe the release workflow
6. verify the GitHub Release, flags, public assets, manifest, and checksums
7. run/observe website synchronization when release data changed
8. verify the live release-data endpoint and live download links

This is a completion outline, not a command-by-command recipe. Use current workflows rather than assuming historical filenames or jobs still exist.

## Live verification

Only report the layers actually verified. For a full release, verify as applicable:

### GitHub Release

- intended tag and release exist
- prerelease/stable flag is correct
- release notes/body are correct
- required public assets are present

### Update metadata

- manifest/checksum assets exist
- version and asset names match the Release
- metadata remains acceptable to the app's validator

### Official website/API

- live endpoint succeeds with the expected schema
- Stable/Preview classification and ordering are correct
- release and asset URLs point to official DropSpace resources
- live download/changelog links reflect the intended release

### App compatibility

- website metadata path remains valid
- GitHub fallback remains valid
- channel/version selection remains compatible

If a requested live layer could not be verified, report exactly which layer is unverified and why.

## Release-specific testing

Use validation proportional to what changed. Release-data/security changes should cover failure paths such as network/HTTP/JSON/contract failure and prove production does not deploy a stale fixture. Updater changes should cover malformed/malicious metadata, channel ordering, fallback behavior, and same-release asset trust.

Do not publish merely because compilation succeeded.
