# Beta13 implementation and evidence

Source requirements: `E:/Codex/下载/DropSpace_Beta13_Codex开发包/DropSpace_Beta13_开发规划.md`
(planning version 2), its six reference images and `delivery/manifest.json`.
The owner explicitly requested implementation through publication on 2026-10-06;
this supersedes the document's older stop-before-publication sentence.

Baseline: `f75e1e60e9cbba523fd678879b076ce7c2929277`, branch
`codex/v0.3.1-beta.13`. Existing untracked `.codex/` evidence and other worktrees
are preserved. Published Beta12 assets must not be replaced.

## Required work (completion requires evidence, not this checklist)

- F01: Host-side English identification, same-language/unknown admission and versioned
  translation caches. On 2026-10-06 the owner removed model-side language checking and
  the skip marker. Keep the original official translation prompt and response contract.
- F02/F03: isolated task recovery and normalized directory identities.
- F04/F05/F06: bounded probe retries, real merge-space peak, committed-output reconciliation.
- F07/F08: current network binding and truthful database undo outcomes.
- F09: separate queue and transfer deadlines, cancellation and visible phases.
- F10: distinguish QQ request rejection, rate limiting and authentication failure.
- F11/F13: virtualized incremental task history and consistent accessible action layout.
- F12: structured model progress and pinned official GitHub mirror, resumable ordered
  transport parts with per-part and final integrity checks, one logical model task.
- F14: actual Die For You recording and Sakanaction Kaiju, placeholder rejection,
  bounded evidence-backed aliases and correct fallback/cache semantics.
- Island: shared display reasons and global hide scheduler; gray idle/pending view;
  manual dismissal generations; fullscreen permissions independent of display reasons.
- Fullscreen: owner narrowed scope to an on/off force-display toggle on 2026-10-06.
  Game/non-game classification, Steam scanning and manual application rules are removed.
  Shared foreground snapshot still detects same-window F11 changes.
- Settings: schema migration, resident toggle, fullscreen toggle and three native sliders;
  accurate old 1.25 scales, all compact branches and expanded layout scaled correctly.
- Branding: transparent official logo for empty wake, one-file and multiple-file variants;
  no logo in idle; preserve activation, drag/drop and accessibility.
- Delivery: focused verification only, honest remaining hardware/upstream limits,
  necessary builds, immutable Beta13 release and working public update metadata.
- Owner addition: custom downloads, task history and download settings live in a separate
  Downloads settings tab between DLC and System Activities. DLC only manages components;
  navigation does not change application-owned transfers or persisted settings.

## Evidence

Implementation is in progress; Beta13 is not published. Development App builds completed
with zero warnings/errors. The development app starts with its compiled XBF resources.
Latest host-language/fullscreen changes passed the four focused Core checks on 2026-10-06.
Focused later builds and checks are recorded below; final release packaging is pending.

Local model-probe-02 through model-probe-05 recorded successful CUDA Chinese translation
through the App service call chain, with matching worker GPU activity and memory evidence.
The experimental same-language marker was unreliable; the owner removed that requirement.
Its extra prompt, parser and typed skip-cache changes were reverted to the baseline.
The strengthened host admission policy remains and versions derived caches independently.

### Live recording checks, 2026-10-06

- Apple Music `Die For You`, artist `Abel Tesfaye — Starboy`, duration 260 seconds:
  reproduced no lyrics with AI disabled. NetEase returned matching The Weeknd candidates
  but the host rejected their artist credit. Added exact whole-credit Abel Tesfaye/The
  Weeknd equivalence and bounded search alias; title/version/duration safeguards remain.
  Official identity evidence: https://music.apple.com/qa/song/1677006158.
  Fixed App received NetEase ID 442867526, 73 original lines and 71 Chinese translations;
  both the music page and compact island displayed them. Local trace:
  `.codex/beta13/live-lyrics-fixed.jsonl` request 32960-2.
- Apple Music `Kaiju`, artist `鱼韵 — Kaiju - Single`, duration 257 seconds:
  identified missing Chinese artist alias and failure to use expanded performer candidates
  for the existing bounded title alias. Added 鱼韵/魚韻 and expanded-credit lookup.
  The actual fixed App displayed Japanese originals and Chinese translations on both
  surfaces; ordinary diagnostic log recorded NetEase 50 lines / 46 translations at
  04:31:41 UTC. Screenshot `.codex/beta13/evidence/kaiju-fixed.png`.
- Both additional matching checks passed independently; no full test suite run.
  Portable development App build passed with zero warnings/errors. The latest Kaiju-only
  Core assembly was compiled by its targeted check and loaded into that development App.
  A first non-Portable development launch failed before App code due to missing Windows
  App SDK registration; corrected by using the repository Portable build configuration.
- Playback initiated for these checks is paused. The temporary AI-disabled preference
  was restored to true and confirmed on disk; no other preferences or caches were reset.
- Kaiju's persisted verified candidate is NetEase 2676706396, artist サカナクション,
  duration 256.99 seconds (Apple publisher timeline rounds to 256 seconds).
- Native UI: changed resident/force-fullscreen to true, delay to 3 seconds, compact to
  0.5 and expanded to 1.5; all five survived a graceful App restart and remained visible
  in their native controls. With Apple Music closed, empty residence displayed only the
  gray capsule. Turning residence off displayed the gray pending state then hid it.
  Music compact content visibly scaled to 0.5. Restored all five original values through
  the UI (false/false/2000ms/1/1) and confirmed the persisted fields. Expanded scale
  rendering still has no separate live measurement; file/download observations follow.
- Owner-added Downloads tab compiled and verified in the running portable development App:
  DLC -> Downloads -> System Activities. DLC no longer contains the download form/settings.
  Existing task history and download preferences remain visible in the new tab.
- Fixed blank virtualized task cards by populating the item template root instead of
  replacing the ListView container Content. Live UI now shows both restored completed
  tasks, byte totals, progress and actions, including the local 8 MiB fixture.
  Latest focused App build: zero warnings/errors (`dev-downloads-page-build.log`).
  Active/paused button observations follow.
- Active download UI: local 8 MiB file showed real bytes, speed and 1–3 active
  connections; paused at 7.66 MiB with zero active connections, resumed the same
  task and completed at exactly 8,388,608 bytes. Buttons stayed aligned in the
  771-pixel window. Evidence: `download-paused.png`, `download-resumed-completed.png`.
- Focused additional checks passed: queue wait longer than the transfer budget,
  independent queue/transfer cancellation and timeout types; QQ request rejection
  versus explicit authentication expiry; two undo cases covering committed-delete
  cleanup failure and zero affected rows. No full regression suite was run.
  The first undo harness launch failed at Windows App SDK initialization; rerun
  with DeploymentManager auto-initialization disabled for the unpackaged test host
  passed both actual cases. This is a harness issue, not an undo pass on first run.
- LRCLIB explicit instrumental evidence is distinguished from placeholder text;
  its matched empty candidate still allows a later usable candidate. One focused
  transport check passed. Empty/failure documents now carry truthful quality labels.
- Live file branding: one imported test file displayed the transparent logo and
  filename, two displayed the logo and item count. Both omitted the old subtitle;
  clicking expanded the file view and collapse returned correctly. Only the two
  test references were removed afterward; user files were not changed.
  Evidence: `.codex/beta13/evidence/island-expanded.png` and `island-multiple.png`.
- Publication checks refreshed for Beta13: the actual host computed the unchanged
  48-line fixture under admission v9 without loading a model. Ten focused admission
  guards and five updated fullscreen integration guards passed. Historical Beta12
  approval/evidence records remain unchanged; Beta13 records retain incomplete model
  qualification rather than claiming semantic approval.

## Remaining observation limits

Physical network A-to-B handoff, expanded scale measurement and hardware-specific
fullscreen behavior have not been fully exercised live. Source/state checks do not
prove these hardware outcomes. The original roughly one-minute model download stall
has not been reproduced; no claim is made that its historical cause was established.
No full regression, fresh model download or multi-hardware performance matrix was run.

## F01–F14 closure audit

The reattached ZIP contains the identical planning file, SHA256
`8382685051fa1295f02d0426b1f55968ab4bd6bd7cb9e1b9c11c11bfe51fac06`.
Owner overrides remove model-side skip prompts and game classification; all other
fixes remain in scope. This table distinguishes code/controlled checks from hardware
claims. The package expressly permits the original one-minute stall to remain an
identified live-observation limit, rather than claiming an invented cause.

| Item | Implemented path and evidence |
| --- | --- |
| F01 | Host language v9 admission, cache version; focused Core checks and actual minimal CUDA Chinese translation. Model skip layer removed at owner's direction. |
| F02 | Per-record repository recovery and per-task manager exception isolation. Corrupt journal check passed; safe-path InvalidDataException is now also isolated. |
| F03 | Exact normalized directory identity including trailing separators. Additional real journal restoration exposed and fixed trailing-separator handling in DownloadStorage.Safe. |
| F04 | Probe HTTP status is inside bounded retry; 503 then success and non-retried 403 checks passed. Cancellation uses the same retry token. |
| F05 | Own previous assembly reclaimed before measuring space. Controlled 4 MiB retained-parts merge passed at the exact free-space threshold, rejected one byte less, and preserved unrelated files. |
| F06 | Commit remains successful on marker cleanup failure; hash/size reconciliation covers Failed and Finalizing. Real journal restart reconciled a Failed delivered file to Completed; retry/removing history left exactly one correct output. |
| F07 | Rebind before publishing new links; event and publication share a lock. Controlled resolver switched loopback A to actual private address B; real HTTP receiver read the correct three bytes, old token was revoked, offline resolver produced no URL. This is not a physical Wi-Fi handoff claim. |
| F08 | Two focused actual-database undo checks passed: committed cleanup failure and already-restored record. |
| F09 | Queued time excluded from transfer budget; both cancellation and timeout phases passed focused checks. |
| F10 | Request 403 does not expire QQ session; explicit auth failure does; renewal and bounded 429 cooldown passed. |
| F11 | Stable task view, incremental event updates, bounded 50-row history, virtualized 600-pixel ListView. A 77-record fixture checked pagination, paused-task retention and no idle progress notifications; removing history preserved final file bytes. Live task rendering/scrollable layout and pause/resume evidence above. |
| F12 | Bound delivery manifest, four-part retry/corrupt-part replacement/final hash check passed with a small fixture; live ordinary byte progress and installed model reuse verified. Original user's one-minute stall remains unobserved, as permitted by the plan. |
| F13 | Shared measured action rows; live active/pause/resume/completed download checked at a narrow window width. |
| F14 | Both requested actual recordings successfully displayed original and NetEase Chinese lyrics; placeholder/instrumental/next-candidate checks passed. |

The additional history fixture first exposed a real trailing-root bug and passed after
its fix (77 records, 728 ms). A later attempt to inject an out-of-root journal entry was
rejected by the repository before manager restoration; that invalid extra fixture was
removed, preserving the already-passed history scenario. It is not reported as a
manager fault-injection pass. Remaining physical-device limits above stay explicit.
