# Five rounds of bug discovery and repair — 2026-09-30

Five additional find → reproduce → fix → verify rounds were completed after the
initial audit commit `81407aa`. Each round added regression coverage for confirmed
failures. The work remains on local branch `agent/full-source-audit-20260930`;
no push, PR, deployment or release was performed.

The remote main branch was checked again after implementation and remained
`1f238dd8ffe88929b95bab6bff6eb902789095de`. The
[initial audit](source-audit.md) describes the environment setup and earlier fixes.

## Round 1 — share authentication, upload accounting and revoke recovery

Commit: `6ed057a`.

- Valid upload tokens failed authentication because the Worker had no global
  base64 decoder. Added the decoder and exercised actual signed authorization,
  including tampering rejection.
- Quota accounting trusted the declared body length. Read a bounded upload body
  before reservation or storage, reject mismatched lengths without consuming
  quota, and allow a corrected retry.
- Partial revoke failures could delete metadata needed for retry. Retain metadata
  until all objects are removed, and reject reads of revoked shares even while
  retry metadata remains.

Worker tests increased from 10 to **13 passing**. The failed-before and
passed-after outputs are `/workspace/scratch/round1-before.log` and
`/workspace/scratch/round1-after.log`.

## Round 2 — clipboard retries and external previews

Commit: `2bf06c1`.

- Historical successful clipboard content could suppress a retry after a later
  failure or rejection. Reset the historical persistence fingerprint when a new
  capture attempt starts, while keeping consecutive duplicate suppression.
- A malformed BMP height of `int.MinValue` escaped the preview registry as an
  overflow. Reject it as invalid image data; preserve valid top-down dimensions.
- A PDF that grew after capture could exceed its stale captured size and lose
  preview support. Use the fixed bounded preview budget when reading its current
  contents.

Four failing regressions were reproduced before repair. Related capture tests
passed **12/12**, and preview tests **10/10**. Logs are
`/workspace/scratch/round2-{capture,preview}-{before,after}.log`.

## Round 3 — lyrics and update reliability

Commit: `f73cee3`.

- Lyrics cache keys omitted album artist and could reuse another recording's
  lyrics. Include normalized album artist in the cache identity.
- Nested TTML relative end times were added to the child's start instead of its
  parent's start. Resolve both relative endpoints against the parent timing base.
- Null entries in official/GitHub release metadata escaped resilient source
  fallback. Validate malformed entries explicitly and preserve backup-source use.
- Update shutdown could pass a queued operation through the gate before linked
  cancellation propagated. Check lifetime and caller cancellation after acquiring
  the gate and before invoking the operation.

Seven new failing regressions were reproduced. Related Core tests passed
**36/36**, Infrastructure tests **80 passed / 1 skipped**, and the previously
intermittent shutdown regression passed **8 repeated runs** after repair. Evidence
is in `/workspace/scratch/round3-*.log`.

## Round 4 — live releases and concurrent settings changes

Commit: `fb7c4da`.

- A republished older Stable release could replace a higher version in downloads.
  Select Stable by semantic version rather than publication date.
- Invalid release fields or mismatched artifact names/types could partially
  update the page or point an installer link at checksums. Validate the complete
  response and required official artifacts before changing the page.
- Optional artifact `kind` fields could be absent in valid responses, leaving old
  download links beside a new version label. Resolve artifacts from their
  validated official filenames.
- Background update checks could overwrite preferences changed during the check
  or while waiting for UI dispatch. Return the latest persisted settings and merge
  only a monotonically increasing UTC check timestamp into current UI settings.

Four browser regressions and the settings-coordinator regression failed before
repair. Website tests passed **29 Node / 12 Chromium**, including all four new
browser cases. Core timestamp tests passed **2/2**. A Linux harness linking the
actual coordinator, real JSON settings service and committed regression passed
**1/1**; unused Windows constructor dependencies had compilation stubs. This does
not validate the complete Windows App assembly or WinUI dispatch behavior.

Exact commands and observed output are recorded in
`/workspace/scratch/round4-verification-summary.md`.

## Round 5 — share lifecycle, file relinking and BMP compatibility

- Invalid `PUBLIC_ORIGIN` configuration claimed the share ID before returning an
  error. Validate the origin before initialization and metadata storage so a
  corrected retry can use that ID.
- Public object responses trusted uploader-supplied active MIME types. Store and
  serve encrypted objects as `application/octet-stream` with `nosniff`, including
  existing objects with unsafe stored metadata.
- A lease-record cleanup filesystem failure could replace a successful share
  result or the original upload error. Retain its durable cleanup record for
  retry; restrict recovery catches to filesystem failures and guarantee master
  key clearing in an independent `finally`.
- Concurrent reservation release could be overwritten by a non-atomic capacity
  increment. Use atomic dictionary updates alongside the existing compare-and-
  swap release logic. A bounded 32-worker regression performs 64,000 reserve/
  dispose cycles and then verifies all 128 slots are available.
- Valid OS/2 BMPs use a 12-byte header with 16-bit dimensions. Read those fields
  correctly instead of treating planes/bit depth as part of modern dimensions.
- File relinking updated only Space item metadata, left obsolete owned payloads
  attached, and could mutate a pending-deleted file reference. Update file items
  from every source atomically, protect pending deletion, detach obsolete payloads
  and publish their cleanup obligation in the same transaction. Preserve shared
  ownership and the selected file. Compare against the actual payload location
  to repair inconsistent references already persisted by older versions.

The initial Worker, cleanup, capacity, BMP and relink regressions failed before
repair. Additional relink guards cover same-file selection, transactional rollback
on an injected reference-write failure, and two legacy inconsistent-reference
states. After repair, Worker tests passed **15/15**, related sharing tests
**10/10**, and related storage/preview/cleanup tests **58/58**.

Logs: `/workspace/scratch/round5-worker-tests.log`,
`/workspace/scratch/round5-sharing-infra-tests.log`, and
`/workspace/scratch/round5-storage-related-after.log`. Initial storage failure
evidence is in `round5-bmp-before.log` and `round5-relink-before.log`.

## Final verification

| Check | Actual result |
| --- | --- |
| Full Core suite | **250 passed** |
| Full Infrastructure suite | **215 passed, 7 failed, 5 skipped**; 227 total |
| Worker suite and JavaScript syntax | **15 passed**, syntax passed |
| Website build / Node / Chromium | Passed / **29 passed / 12 passed** |
| Actual-source settings harness | **1 passed**; limited Linux coordinator path |
| Localization | Passed; 597 synchronized resource keys |
| Hardcoding, secrets, release version/consistency, Windows compatibility, MSIX symbol policy | All seven governance scripts passed |
| Whitespace check | `git diff --check` passed |

The final full .NET commands were run from the repository root:

```sh
/workspace/.dotnet/dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj --no-restore --logger 'trx;LogFileName=rounds-final-core.trx' --results-directory /workspace/scratch/five-round-test-results
/workspace/.dotnet/dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --logger 'trx;LogFileName=rounds-final-infrastructure.trx' --results-directory /workspace/scratch/five-round-test-results
```

Their raw logs are `/workspace/scratch/rounds-final-{core,infrastructure}.log`.
Every Infrastructure failure was checked in the TRX: all seven throw
`PlatformNotSupportedException` from Windows `ProtectedData` on Linux, matching
the initial audit's platform failures. They are four authentication middleware
tests, `PendingPairingsAreBoundedPerRemoteAddress`,
`EncryptedRevokeHandleSurvivesRestartRoundTrip`, and
`SaveRejectsAtCapacityAndKeepsEveryLiveHandle`. The failure classification is saved
in `/workspace/scratch/five-round-test-results/infrastructure-failure-summary.json`.
The five skipped tests were not counted as passing.

Worker validation used `npm test` in `share-worker`; website validation used
`npm test` and `npm run test:browser -- --reporter=line` in `website/_source`.
No website code changed after its successful round-four verification. Exact
governance commands and raw log paths are in
`/workspace/scratch/final-governance-summary.md`.

The initial audit's clean npm/NuGet Infrastructure dependency checks remain
applicable; these five rounds changed no dependencies. Those audits were not
rerun or extended to claim a successful App dependency audit.

After the final commit, regenerate the full tracked-blob inventory with:

```sh
python3 scripts/audit-repository.py --revision HEAD --output /workspace/scratch/dropspace-inventory-final.json
```

## Windows acceptance still required

The executor is Linux. Complete WinUI/XAML compilation and App tests, native UI,
DPAPI, System.Drawing brand checks, portable smoke, installer lifecycle, release
EXE/update-manifest validation, MSIX and identity packaging require a Windows
machine and appropriate built artifacts. Static Windows-policy checks and the
linked-source settings harness do not replace those checks. The five repair
rounds are complete and locally reviewable; full Windows release acceptance has
not been established.
