# Ten-language interface and publication audit

Candidate: v0.3.1-beta.19. Original main: f2588100162176ad6740b9550bfc4058a9893b5c. Actual latest published Beta at intake: v0.3.1-beta.18. No open PR existed; PRs #112 and #113 supplied the permanent passive-test policy and were reused. The next tag was calculated from the live release list and repository version, and must be checked again before publication.

## Scope

The App and website consume localization/languages.json. App .resw and website stable semantic IDs keep their existing resource/build systems. New releases, summaries and website highlights have one English source; historical records are retained. UI language and the permanent Chinese/English translation target are independent top-level settings.

## Project-wide execution ledger

The entire task has a maximum of 20 executed functional cases/scenarios, including failures and retries across local execution and Actions. Manual dispatch is not treated as authorization for broad suites in this task. Resource entry counts are not scenario counts. Compilation, structural resource/build validation, source identity, hashes and release/API readback use the repository's existing non-functional production-check classification. No model inference, full lyric suite or ten-language page matrix is authorized or run.

| # | Executed scenario | Result |
|---|---|---|
| 1 | Legacy English enum 1 migrates once, independent Japanese UI | passed |
| 2 | Legacy Chinese enum 2 migrates once, independent Japanese UI | passed |
| 3 | Legacy System enum 0 on zh-TW preserves old Chinese direction | passed |
| 4 | Unknown persisted enums recover safely | passed |
| 5 | Ordered preferred languages skip unsupported primary | passed |
| 6 | Complete supported locale match | passed |
| 7 | Traditional Chinese explicit script overrides mainland region | passed |
| 8 | Simplified Chinese explicit script overrides HK region | passed |
| 9 | Unknown language list falls back to English | passed |
| 10 | UI-only switch does not reload; target switch uses existing reload decision | passed |
| 11 | Concurrent UI and target edits preserve both values through the existing settings Merge | passed |
| 12 | Saved language beats browser; German desktop selection persists across changelog and refresh; releases stay English | passed |
| 13 | Ordered browser preferences choose Russian and long mobile text stays within viewport | passed |
| 14 | Legacy Chinese changelog preserves page/query/fragment; storage-disabled Traditional choice survives navigation | passed |
| 15 | Without JavaScript the unified page exposes English body, navigation and working download URLs | passed |
| 16 | Changing one English source leaves translations pending and blocks the gate | passed |
| 17 | One damaged Japanese format placeholder blocks the gate with locale/key/file | passed |
| 18 | Final-main installer payload installation/uninstallation; installed App bytes equal the portable artifact | passed |
| 19 | A newly added owned English installer Messages override blocks publication until its translations are supplied/reviewed | passed |

Final actual functional executions: **19**, all passed. The successful final-main producer performed the existing isolated installer payload scenario exactly once. The post-publication maintenance review added one copied-tree negative scenario for future installer overrides; one execution remained unused. The earlier producer was cancelled during dependency preparation, before package production or installation, so it added zero scenarios. No repeats of the successful App or website cases were run. No broad diagnostics were enabled on any dispatch.

The App/settings probe compiles the real Core project and links the actual JsonSettingsService, SettingsIoPolicy and AppStoragePaths; it exercises disposable settings files and runs no lyric/AI inference. The separate Merge probe compiles and invokes the actual SettingsChangePolicy. Native Windows UI and install/upgrade lifecycle are outside these local probes.

## Lyric and AI boundary review

Review against the intake main passed before merge and publication. The only behavioral binding changes are reading/storing the independent two-value target, passing its existing en/zh representation, and replacing the existing reload comparison. Display and settings labels localize. Infrastructure/Lyrics and native worker/model code have no diff: models, prompt/protocol, provider/matching/admission/cache/inference, scheduling/cancellation and acceleration/component payloads remain identical. MainPage retirement affects only page rendering and seek UI timers; MainWindow retains its visibility owner, so UI language changes do not cancel or restart lyric translation. Release-source approval records bind the reviewed settings/UI/package inputs without relabeling historical model evidence as new validation.

## Publication

Published successfully on 2026-10-07:

- [Release v0.3.1-beta.19](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.1-beta.19), prerelease=true, draft=false.
- [Installer](https://github.com/airanluo-dot/DropSpace/releases/download/v0.3.1-beta.19/DropSpaceSetup.exe), [portable](https://github.com/airanluo-dot/DropSpace/releases/download/v0.3.1-beta.19/DropSpace.exe), [MSIX](https://github.com/airanluo-dot/DropSpace/releases/download/v0.3.1-beta.19/DropSpace-x64.msix).
- [Unified homepage](https://airanluo-dot.github.io/DropSpace/) and [changelog](https://airanluo-dot.github.io/DropSpace/changelog/).
- App source/tag commit: `20a1dded565d39003795204009f591e79b2e04e4`. PRs [#114](https://github.com/airanluo-dot/DropSpace/pull/114) and [#115](https://github.com/airanluo-dot/DropSpace/pull/115) merged after required Windows compilation. The second fixes a publication-check interpolation and adds early helper syntax parsing; it changes no App/lyric behavior.
- [Final-main producer](https://github.com/airanluo-dot/DropSpace/actions/runs/37693827453), [Release publication](https://github.com/airanluo-dot/DropSpace/actions/runs/37695046038), and [post-publication Pages deployment](https://github.com/airanluo-dot/DropSpace/actions/runs/37695310761) all completed successfully.

The real portable/MSIX PRI candidates passed the ten-language source-value check; the installer binds ten compiled wizard language inputs and contains the identical portable payload. Publication reused that exact final-main bundle and its hash-bound language receipt. The independent CUDA component retained its existing archive/manifest identities and was not rebuilt. Existing Beta signing/distribution and updater contracts were retained.

All eight public assets were anonymously read successfully, including full downloads of the three binaries and four metadata files. Their seven SHA-256 values match SHA256SUMS; the checksum list itself was also downloaded. The published updater manifest is Beta `0.3.1-beta.19`, versionCode `3010019`, with correct installer/portable sizes and hashes. Runtime publication identifies the exact source commit. The live latest-change API identifies Beta19 with identical English highlights in the retained compatibility fields. Homepage/changelog publish static English language markers and their respective unified canonicals; sitemap contains exactly those two URLs. Detailed readback is in [public-download-verification.json](evidence/ten-language/public-download-verification.json) and [publication.json](evidence/ten-language/publication.json).

Production integrity checks passed for all ten language resources and both official/static-showcase build modes. The website cases use one desktop and one mobile representative layout, old-link/storage behavior and no-script English; no page/language matrix or repeated screenshots. Two copied-tree negative cases changed one original and one Japanese placeholder; the gate correctly blocked each without altering the repository.

Standard installer wizard text uses the existing fixed Inno Setup 7.0.2 framework translations. The 12 repository-owned language-name/CustomMessage entries per language are covered by the App gate's fingerprints. Post-publication maintenance also makes future repository-owned Messages overrides inherit the same mandatory/stale review rules, including Inno `%n` markers; it changes no released resource or App binary. The third negative scenario confirms that a new English wizard override reports the missing target language, ID and ISL file and blocks publication. No official framework resources were copied or rewritten.

## Verification limits

No complete regression suite, lyric/model inference or evaluation, native Windows all-language/DPI layout matrix, or install-upgrade lifecycle was executed. The installer scenario validates extraction/bytes and uninstall in a disposable runner, not an actual user upgrade. App lower-frequency translations did not receive exhaustive native-speaker review. The automated gate establishes structure and completeness, not every sentence's semantic quality. No publication blocker remains. Documentation evidence is committed after publication without changing the released App inputs or its immutable tag.
