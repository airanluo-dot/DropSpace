# Releases and updater

Repository: airanluo-dot/DropSpace. Website:
https://airanluo-dot.github.io/DropSpace/. Canonical assets live in GitHub Releases.
Website updater feed: /DropSpace/api/v1/releases.json; GitHub Releases is fallback.

## Version contract

All new prereleases use vMAJOR.MINOR.PATCH-beta.N. Stable uses vMAJOR.MINOR.PATCH.
Historical Preview tags, releases, assets and release notes are immutable.
Readers accept preview.N and beta.N; compare major/minor/patch, then numeric
prerelease sequence. Stable ranks above any prerelease in the same line. Never
use lexical beta/preview ordering. Preserve the original tag when accessing
historical URLs. RELEASE_VERSION is the current publishing source of truth.

ReleaseVersion.cs and scripts/ReleaseVersion.ps1 share bounded numeric fields:
major <= 20, minor/patch <= 99, prerelease 1..9998. Windows package revision is
N for prerelease and 9999 for Stable; Stable file revision is 0. Build parsing
uses DropSpacePrereleaseNumber. New-release validation rejects -preview.N while
historical parser tests continue accepting it.

The update channel reads legacy Preview/preview and numeric 1 as Beta, and
writes Beta. Stable receives only Stable; Beta receives the highest eligible
Stable or prerelease with no downgrade. The original Preview.23 binary cannot
parse Beta tags: the user authorized a one-time manual Beta 24 installation,
preserving data/settings, followed by normal Beta updates. Do not claim that
new parser unit tests prove the old binary can automatically discover Beta.

## Publication

Inspect current RELEASE_VERSION, remote tags/releases and main before choosing
or publishing a version. A published tag or asset is never replaced. Release
notes must match the canonical tag and include an update-summary <= 500 chars.
Build and validate through .github/workflows/ci.yml and release.yml. Publication
uses the explicit main workflow_dispatch publish=true lane after required
checks and merge. No bypass of branch protection or signing policy. Stable
requires signing; unsigned Beta must retain manual install/trust boundaries.

The public bundle contains DropSpace.exe, DropSpaceSetup.exe, DropSpace-x64.msix,
SHA256SUMS.txt and update-manifest.json. Verify actual asset count/names, nonzero
sizes, checksums and manifest version/channel/versionCode. CI artifacts are not
public update assets. Never delete releases to clean Actions artifact storage.

## Website and completion

website/_source/scripts/sync-releases.mjs ingests GitHub releases;
release-contract.mjs validates metadata and build.mjs creates the website/API.
Retain old tag names and download URLs. Fail closed on missing production
metadata; do not substitute fabricated releases or stale demo data. Validate
historical Preview and Beta classification, latest ordering and download links.

After publication, verify the remote Release, workflow result and deployed
website/API all agree on the same tag and assets. Preserve historical published
release records. Report external deployment or Skill synchronization failures
precisely; a successful build alone is not a successful release.
