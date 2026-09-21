# Releases and updater

Repository: airanluo-dot/DropSpace. Website:
https://airanluo-dot.github.io/DropSpace/. Canonical assets live in GitHub Releases.
Website updater feed: /DropSpace/api/v1/releases.json; GitHub Releases is fallback.

Current Beta26 authorization: D-064 resumes PR67 delivery and final publication
after real one-click NetEase acceptance and all release gates. Shuffle/repeat
expansion and mode acceptance were explicitly removed; the 12 core-capability
gate remains mandatory. The earlier pause is superseded. Preserve historical
audit evidence, then complete protected merge, release publication and live
website/API checks. Authorization is not evidence of native success.

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

`scripts/ReleaseArtifactVersion.ps1` validates executable numeric/text versions
and product identity against RELEASE_VERSION. Installer creation and manifest
creation/validation reject stale payloads. The lifecycle baseline must be the
real historical installer, with downloaded release checksums verified, never a
current binary relabelled as the old version. Verify the installed PE before
and after upgrade in addition to install/update markers. Run destructive
installer lifecycle checks only in an isolated account/runner; existing user
data, installation state and running instances must be protected. Portable smoke
uses a unique test-data root and restores the user's startup registry value.

Cancellation before installer launch restores durable and visible ReadyToInstall
state without reusing the canceled token; failed rollback remains visible.
Restored update state must agree on version/tag, channel/prerelease, version code,
minimum Windows contract, official selected asset and integrity metadata.
App regression tests require their unpackaged Windows App SDK test bootstrap.
On clean Windows runners, run `scripts/Install-TestWindowsAppRuntime.ps1` after
locked restore; it uses the runtime MSIX files from that exact locked NuGet
package and verifies registration. `-InspectOnly` reports without installing.
Test bootstrap uses the None option so a missing runtime fails with an HRESULT
instead of opening an invisible download dialog. Keep prerequisite and App-test
steps bounded; do not skip native tests or change production initialization.
Native COM/clipboard fixtures, ZIP directory-budget tests, live lyric-provider
smoke and actual target-machine player behavior cover different release risks;
record their results separately rather than substituting one for another.

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

Homepage feature stories live in website/_source/src/index.html with shared
styles.css and bilingual scripts/i18n.mjs. Native Island tabs use local,
keyboard-accessible interactions; widget/music illustrations must label sample
content and preserve documented player limitations. Validate the website unit
and browser suites, including native-showcase.spec.mjs. Website-only changes
publish via deploy-website.yml after protected merge without an App version bump.

Keep homepage additions in the original visual language: reuse the existing
showcase/island tokens and popover duration/easing, rather than introducing a
parallel palette or motion system. Browser regression compares new and existing
computed backgrounds, radius and motion, including reduced-motion handling.

Homepage section indexes use a continuous NN / NAME sequence in both languages.
Keep feature copy evergreen: release-specific changes belong in API-driven news
and immutable release notes, not feature stories or hard-coded Beta download
buttons. Use the shared i18n map and existing data-download/data-release-url
hooks for download links; preserve release API routes and historical metadata.
The Windows compatibility gate checks release-specific website guidance and the synchronized release-notes link; concrete OS minimums remain enforced in app, installer, manifest and baseline sources.
