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

Actual functional executions so far: **17**, all passed. The only planned remainder is the existing final-main producer's one isolated installer payload extraction scenario (18 total before any recovery), leaving two executions for a necessary recovery. No repeats of the successful App or website cases were run.

The App/settings probe compiles the real Core project and links the actual JsonSettingsService, SettingsIoPolicy and AppStoragePaths; it exercises disposable settings files and runs no lyric/AI inference. The separate Merge probe compiles and invokes the actual SettingsChangePolicy. Native Windows UI and install/upgrade lifecycle are outside these local probes.

## Lyric and AI boundary review

Review against the intake main is required before merge. The only allowed behavioral binding changes are reading/storing the independent two-value target, passing its existing en/zh representation, and replacing the existing reload comparison. Display and settings labels can localize. Models, prompt/protocol, provider/matching/admission/cache/inference and acceleration/component payloads must remain identical. Release-source approval records bind the new settings/UI/package inputs without relabeling historical model evidence as new validation.

## Publication

Pending actual PR compilation, final-main package production, release publication and website deployment. Workflow dispatch or a commit is not publication evidence. Known verification limits and actual links/identities will be added only from completed producer and public readback results.

Production integrity checks passed for all ten language resources and both official/static-showcase build modes. The website cases use one desktop and one mobile representative layout, old-link/storage behavior and no-script English; no page/language matrix or repeated screenshots. Two copied-tree negative cases changed one original and one Japanese placeholder; the gate correctly blocked each without altering the repository.
