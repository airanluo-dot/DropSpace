# Beta 26 release-candidate audit

Date: 2026-09-21. Baseline: Beta 25 (`32f055a`), incorporated through main
`53dff85`. Target: `v0.3.0-beta.26`. The user authorized actual Apple Music and
NetEase testing and publication after validation. This audit covers product code,
native execution, real provider requests, regression tests and release delivery.

## Findings and repair scope

No S0 defect was confirmed. The following are confirmed defects, not hypothetical
claims that every reviewed path is faulty. Detailed triggers, implementation and
evidence are in the [native/data](beta26-native-data-audit.md),
[media](beta26-media-audit.md) and [lyrics](beta26-lyrics-audit.md) reports.

| Severity | Defect family | Root cause and repair |
| --- | --- | --- |
| S1 | Virtual-file native crash | Correct the HRESULT/out-pointer ABI of `CoGetInterfaceAndReleaseStream`; execute real native stream regressions. |
| S1 | Payload cleanup could cross ownership boundary | Textual containment admitted existing directory links; apply the reparse-safe policy to every payload-store operation and journal replay. |
| S1 | Current music metadata lost behind artwork failure | Optional thumbnail I/O failed the entire candidate; isolate bounded artwork failure from metadata and prevent unrelated old-session fallback. |
| S2 | Online lyrics rejected or misclassified | LRCLIB requires search for missing duration; Kugou duration is seconds; NetEase search needs artist discrimination. Preserve verified identity and reject empty search results. |
| S2 | Incorrect lyric timing/translation | Preserve explicit YRC/TTML line ends; prevent a single translation being duplicated onto timestamp-adjacent credit lines. |
| S2 | Playback clock discontinuities | Use frame observations for sparse timelines, and exclude paused wall time when a resumed player republishes an old native timestamp. |
| S2 | Session lifecycle | Remove stale control targets, while preserving recovery from transient candidate reads without resurrecting unrelated metadata. |
| S2 | Virtual-file batch failures/leaks | Reserve duplicate names before writing; release unused marshalled interface references on rollback. |
| S2 | Cleanup starvation | Rotate bounded outbox retries by last attempt so permanently failing old entries cannot block later payloads. |
| S2 | Clipboard commit/shutdown races | Finish runtime resume after durable settings commit; serialize disposal with asynchronous initialization. |
| S2 | ZIP structure corruption | Preserve sanitized directory components, empty directories and distinct roots; include directories in enumeration budget. |
| S2 | Updater cancellation and malformed state | Restore durable and visible ReadyToInstall after pre-launch cancellation; validate persisted nullable integrity fields and version/channel/platform consistency. |
| S2 | Release validation could certify stale bytes | Check real PE numeric/text identity before packaging and manifests; test upgrade from the genuine public Beta 25 installer, never relabel current bytes. |
| S3 | UI capability/artwork inconsistencies | Respect state-specific Play/Pause, changed timeline bounds and ShowArtwork on every music surface. |
| S3 | Resource/privacy defects | Dispose shell/updater Process wrappers; avoid exception strings containing private paths in shell/preview diagnostics. |
| Test infrastructure | Native test host activation | Initialize the unpackaged Windows App SDK test host; avoid duplicate ProjectReference builds. Native tests execute instead of being skipped. |

The changes retain C#/.NET/WinUI 3, the schema, existing dependencies, local-first
ownership, bounded event-driven clipboard capture and current product scope.
No new ADR or migration is needed. Network redirects are bounded to same-origin
HTTPS; no redirect was observed in provider smoke, so redirects are not claimed
as the demonstrated cause of the original failure.

## Audit coverage

- Read the Beta 24 to Beta 25 history and reviewed actual media-session, identity,
  provider matching/fallback/cache, cancellation and UI publication paths.
- Reviewed OLE/COM ownership, drag observer disposal, overlay creation/topology,
  DPI/negative-coordinate policy, clipboard sequence/pause/commit, SQLite,
  settings serialization, payload reconciliation, retention and shutdown.
- Reviewed exports/ZIP, shell actions/preview, DropLink authentication/pinning,
  nearby/private-network sharing and encrypted upload/Worker boundaries.
- Reviewed updater channels, version parsing, download integrity, trust,
  persisted state, launch rollback and genuine old-to-new installer lifecycle.
- Searched related call sites for COM marshaling, payload path resolution,
  Process handles, cancellation-after-commit and duplicated presentation logic.

## Real network and target-machine evidence

Production provider implementations and the actual registry/fallback service
were used with real network responses, not mocked success responses. NetEase,
QQ Music, Kugou, LRCLIB and AMLL all returned parsed lyrics for at least one
sample. Samples included Chinese title/artist with absent album and zero duration,
the same title with album/duration, and Color Your Night. A provider catalog miss
did not stop successful fallback. Local LRC loaded two fixture lines with zero
network calls. Sample counts/timings are in the lyrics report; ignored local
evidence is under `artifacts/beta26/lyrics-smoke/`.

Both installed players were opened and operated through their real Windows UI.
The running candidate's executable path and embedded version were verified as
the newly built Beta 26, distinct from the user's installed Beta 25. Observations
included NetEase Vagrant, date night, Sunroof and If The Sun Burns Out Tonight,
and Apple Music Color Your Night/Deep Breath Deep Breath -Reload-.

- NetEase consecutive track changes returned current lyrics/artwork; pause
  stopped estimated progress and resume/next controls changed the actual player.
- Apple Music and NetEase handoff changed source, title, artwork and lyric query;
  main Music, compact and expanded Island showed the same current lyric.
- Apple Music pause held position; expanded-Island controls operated the player.
  Longer pause/resume exposed a stale-timestamp jump, and next-track testing
  exposed a transient-read recovery defect. These were fixed before final gate.
- Real-provider timing exposed translation reuse on zero-time credits; regression
  coverage uses the observed timestamp shape without embedding copyrighted text.
- Native observations were recorded without raw lyrics in `artifacts/media-live.jsonl`
  and `artifacts/media-live-extended.jsonl`; query generations and identity hashes
  distinguish returned results from canceled or superseded queries.

NetEase on this host publishes zero native position/duration and a 1601 timestamp.
Its progress is explicitly estimated; seeking is disabled. Starting DropSpace
mid-song cannot recover the elapsed position from this missing data. Apple Music
publishes position/duration here but does not advertise seek capability. These
are observed platform/player limits, not a claim of exact sync or supported seek.

## Verification and delivery record

The initial complete local gate passed: clean Release solution, Core 231,
Infrastructure 180 and App 52 tests, all without failures or skips. Website unit
tests 27, browser tests 8 and share-worker tests 5 passed. Version, consistency,
localization (563 keys), Windows build 20348 baseline, hardcoding and secret
hygiene checks passed. Real OLE streams, HGLOBAL, Windows directory links and
10,000-directory ZIP rollback fixtures executed.

Initial fresh portable/installer/MSIX/identity packages passed embedded-version,
brand, symbol-policy, manifest and SHA256 gates; the genuine Beta 25 installer
was correctly rejected as a Beta 26 artifact. These initial bytes are superseded
by rebuilding after the additional real-player fixes. Final test counts, smoke,
CI, upgrade and publication results are recorded below when executed.

Warnings reviewed: PRI257/PRI263 concern MSTest localized satellite resources in
the test host, not missing app translations; unsigned MSIX symbol generation is
covered by the explicit symbol-policy gate. No build errors are accepted.

## Remaining evidence limits

- NOT VERIFIED — physical mixed-DPI/negative-coordinate monitor arrangements,
  hotplug, full high-contrast/reduced-motion/text-scale and keyboard/Alt+Tab matrix;
  this run does not have a controlled hardware/accessibility matrix.
- NOT VERIFIED — interactive drag gestures from every external virtual-file
  provider (including Outlook), UNC/locked-file permutations and all Windows
  build 20348 installations. Native fixtures and baseline checks cover specific
  contracts, not all third-party integrations or OS images.
- NOT VERIFIED — multi-day resource soak and two-device DropLink/remote-sharing
  acceptance. Local tests and code inspection do not establish these outcomes.
- Reparse checks reject existing links; they are not an atomic guarantee against
  an adversary replacing directory components concurrently.
- Unsigned Beta distribution retains its documented manual trust boundaries.

## Documentation and skills

PRODUCT, UX, ARCHITECTURE, FEATURES, ROADMAP, CONTRIBUTING, README and release
notes reflect the repairs and their verification boundary. The repository
maintainer skill and installed `dropspace-codex` counterpart are synchronized;
both entry points and native/release references are validated. Publication of
the repository skill follows the same protected product change.
