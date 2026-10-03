# Cloud Beta 1 repair candidate — 2026-10-02

This work starts at `a31d0092dc2b3fb5c4c437bb100429d32cbf0bfc` (tree
`1cf13129f84f948cc1653ed67b819ef6fada5e44`) on the separate
`agent/cloud-beta1-fixes-20261002` branch. Target version remains `v0.3.1-beta.1`.
The user's Windows checkout, model directories and GPU were not accessed. No release,
tag, merge or deployment is authorized by this candidate.

## Repairs

- Lyrics requests retain cancellation callbacks, provider slots and timeout ownership until
  actual completion. Cancelled late results cannot populate the cache. Explicit music refresh
  bypasses source cache and remains pending across worker/cache-maintenance contention;
  AI cleanup no longer occupies both source-fetch slots. Translation errors preserve fetched
  source lyrics. The existing soft restart refreshes music, artwork and lyrics.
- Real mocked NetEase/QQ payloads and old source-v2 cache entries receive conservative
  translation-language evidence. A valid source translation matching the target bypasses
  the entire AI path before cache/resolver/inference/progress calls; partial blanks remain.
- Original-language admission excludes credits and confidently same-target lines before
  AI activity. Explicit TTML span languages and bounded lexical/context rules preserve
  foreign verses and unknown text. Original IDs, cache v2 eligibility, progress and final
  results use the same policy; previous AI caches cannot restore excluded translations.
  The fixed model prompt and sampler are unchanged. These rules are conservative heuristics,
  not a general calibrated language detector.
- Glow mode uses a standard untemplated WinUI ComboBox. Refresh has a full system refresh
  glyph and localized text. The default-on Show AI lyrics label setting changes rendering
  only in main, compact and expanded surfaces.
- Compact translations have their own measured viewport and marquee, including fractional
  12–28 DIP original fonts and 0.875 translation scaling. Main/expanded text wraps without
  ellipsis or a two-line cap inside a focusable ScrollViewer. Track/text/font/width/DPI and
  label changes remeasure; scrolling and reduced-motion preferences remain effective.
- The lighter, smaller halo uses the existing six real FFT bands for contour and intensity.
  Its independent click-through HWND sits below the body; finite signed-distance overlap
  extends inward beneath it while deep interior and outside bitmap edges stay transparent.
  Render padding does not expand body hit testing. Actual visible LocalAi text drives AI
  mode across retained gaps, including expanded viewport intersection. Both forms share
  smoothed intensity and frozen contour motion on pause/Off/reduced motion.
- Native cleanup now observes the Windows kernel process signal via a retained duplicated
  process handle. The original cancellation/deletion regression remains, with repeated
  immediate deletion and caller-timeout coverage. This addresses a cleanup race found while
  investigating old CI `37003028144`; the new tests passed on Windows for the first candidate
  as recorded below. Later source revisions require their own Windows run.
- Optional 7B is documented separately in [its integration record](hy-mt2-7b-optional-profile.md).
  Default 1.8B identity/prompt/sampler are retained. A bounded, separately recorded
  [cloud CPU diagnostic](hy-cpu-cloud-diagnostic-2026-10-02.md) produced no 7B translation.

## Repairs following independent review of the first candidate

The first candidate, `49a9ac13dd3d596972d5ac8cabc8d7304171b584` (tree
`37f16c4f44de95daca66dc4c3b23d5c0324341d7`), was not accepted as final. Review
identified the following gaps; this revision addresses them without changing the model prompt
or sampler:

- A private presentation cancellation token is signaled before asynchronous transport
  cancellation. An HTTP callback blocking its own cancellation chain can no longer prevent
  the presentation waiter from retiring. Actual provider slots and cancellation ownership
  remain retained until the transport and callbacks finish. Four real mocked-HTTP regressions
  fail against the first candidate and pass with this repair, including two retired App
  workers, cache maintenance and a subsequent explicit refresh.
- Provider translation language evidence is assigned per line. A retained name, unknown
  phrase or different language cannot erase another line's valid matching evidence. Any
  matching source translation preserves the existing whole-document bypass, including
  partial blanks and old source-v2 cache reads ahead of poisoned AI caches. Untimed provider
  blocks evaluate physical segments independently; a multi-language block retains its
  separate positive matches without inventing one language tag. Display uses that same
  match so the bypassed source translation remains visible.
- Untimed multi-line display rows retain their original ID, text, timing and word metadata.
  Physical lyric segments independently exclude credits and confidently same-target text
  before inference; each eligible segment uses the unchanged per-line prompt. Progress and
  cache publication occur only after a complete original display row. Copies remain neutral,
  including surrounding whitespace. Timed rows and untimed segments share the same bounded
  neighboring-context rule; blanks, unknown/foreign text and weak neighboring evidence
  prevent propagation. The parser retains internal untimed blank section boundaries.
  Eligibility version v3 fences older AI caches.
- A small, auditable English vocabulary and grammar rule recognizes short cases such as
  `I love you`, `I need you` and `Let it be`. Unknown words and mixed Latin-language tails
  remain unknown; this intentionally leaves many longer English sentences eligible.
- CPU startup now checks available physical RAM and commit under the shared inference gate,
  after the prior process exits. Both must meet the selected process ceiling plus 1 GiB
  (4 GiB for 1.8B, 13 GiB for 7B). Unknown or insufficient resources preserve source lyrics
  with a recoverable status, without incrementing the model-failure circuit. These are
  conservative admission limits, not measured model peaks or installed-RAM guarantees.
- A still-invisible island anchors to its resolved position before entrance opacity/size
  animation, closing transient top-edge halo clipping without changing the generic hidden
  pose, custom positions or visible reversal continuity.
- Native evidence capture and the publication gate bind an explicitly selected model,
  its own memory/argument profile, frozen independently reviewable eligibility mapping,
  actual segment calls, progress and cache replay. Old 1.8B captures cannot be relabeled as
  7B; a missing genuine capture leaves publication blocked. Release notes distinguish the
  default 1.8B and optional, unqualified 7B profiles.

## English admission repair following review of the second candidate

The second candidate, `1eee12095d240ce9605a58cde46c80e477f6a8b5` (tree
`1a0842ad27cfa1203d0573b22fa6179bbc731a37`), fixed the first review findings
but introduced a confirmed English admission regression. Its closed content-word
vocabulary rejected ordinary sentences such as `Your blue cup waits beside the
window.` and `You said the northern road was closed.` Unknown classification
allowed same-target AI rewriting and prevented matching English provider bypass.

Policy v4 retains positive English function-word evidence with open content
vocabulary. Bounded positive foreign-phrase evidence vetoes mixed Latin clauses;
unrecognized words alone are not foreign-language evidence. Short English cases,
mixed scripts, romanization and credits retain their prior admission rules. This
remains a conservative heuristic, not universal language recognition. Version v4
also fences prior AI cache keys; the fixed prompt and sampler are unchanged.

An isolated identical probe compiled the old policy and current policy into
separate scratch outputs: the two original sentences plus NetEase/QQ translations
failed all six expectations on `1eee12095` and passed all six after repair.
Actual App service tests cover fresh mocked payloads, legacy source-v2 null tags
and poisoned AI caches, asserting zero AI cache/resolver/inference/progress calls
and unchanged original/provider text. Coordinator tests separately reject poisoned
cache admission before invoking inference or progress.

The unchanged source48 fixture was reviewed against the actual policy: English
IDs 0, 1, 2, 3 and 7 now have positive evidence, leaving 43 English-target calls;
Chinese-target calls remain 40. Other English IDs remain unknown. Human language
labels are never injected into prompts or production metadata. Frozen expectations
and the publication gate reject AI text on excluded English IDs 1 and 3 as well
as Chinese ID 37. This does not create genuine native model evidence or approval.

## Cloud verification of the second candidate (historical)

| Check | Actual result | Limit |
| --- | --- | --- |
| Complete Core suite after review repairs | 480 passed, 0 failed, 0 skipped | Pure managed policies |
| Complete Infrastructure suite after review repairs | 615 passed, **8 failed**, 26 skipped | Linux platform/environment failures below |
| Linked actual App services and five regression files after review repairs | 64 passed, 0 failed, 0 skipped | Temporary net10.0 harness; not the full WinUI App suite |
| Linked actual App glow rasterizer tests after anchor repair | 18 passed, 0 failed, 0 skipped | 32 entrance/fade sequences at four DPIs; native window tests not run on Linux |
| Node scripts after review repairs | 357 passed, 0 failed, 0 skipped | Includes model, capture, eligibility and cache-evidence tampering regressions |
| Localization, release version/consistency, Windows compatibility, hardcoding, secret hygiene after review repairs | Passed | Static PowerShell policies; 693 synchronized resource keys |
| Native resource policy | C++17 compile and execution passed | Small policy fixture; no model/runtime loading |
| Actual AI publication approval | Blocked at `pending` as expected | No new quality approval was produced |

The eight Infrastructure failures were preserved and investigated: seven call Windows DPAPI
on Linux (four authentication, one pairing admission, two revoke-store tests). One existing
receive-route test attempts to create `/home/agent/Downloads`; the cloud's read-only home
returns `EROFS`, which the endpoint maps to HTTP 400. That final failure was reproduced from
the already-built test DLL with syscall tracing. The affected production/tests and their
storage/transfer dependencies have no diff from the a31 baseline. These failures are not
reported as passes or removed/skipped to obtain a green result.

Cloud TRX files are under the corresponding test project's ignored `TestResults` directory.
The review-repair suite records are in `/workspace/scratch/dropspace-cloud-checkpoint/audit-repair-validation`.
Temporary linked App evidence is in `/workspace/scratch/app-managed-regression` and
`/workspace/scratch/glow-regression-harness`; Node/PowerShell logs and exact executor setup
are in `/workspace/scratch/node-static-triage`. None is a path on the user's Windows computer.
NuGet restore succeeded through the configured network; dependency locks and sources were
not changed. No runtime, model, build cache or large test payload is added to Git.

## Cloud verification after the English admission repair

| Check | Actual result |
| --- | --- |
| Full Core | 490 passed, 0 failed, 0 skipped |
| Full Infrastructure | 619 passed, **8 failed**, 26 skipped; same Linux limitations detailed above |
| Linked actual App services | 76 passed, 0 failed, 0 skipped |
| Isolated old/current parser and admission probe | Old 0/6 (expected RED), current 6/6 |
| Node scripts | 359 passed, 0 failed, 0 skipped |
| Six static PowerShell policies | Passed; 693 synchronized resource keys |
| Evidence tool Release build, fake contract and frozen fixture audit | Passed; build 0 warnings/errors |
| PowerShell evidence contracts | Passed, no native execution |
| Actual publication gate | Still blocked at pending |

These records are in `/workspace/scratch/dropspace-cloud-checkpoint/english-regression-validation`;
the isolated probe is in `/workspace/scratch/english-admission-red-green`. An initial Node
invocation omitted the installed PowerShell directory from PATH (13 `pwsh ENOENT` failures).
After correcting only the process environment, the complete rerun passed; both logs remain.
No real model or user-machine workload was run for this repair. Same-commit Windows CI and
independent review remain separate requirements. Glow appearance and motion are also a
separate iterative acceptance item; policy tests and single frames do not establish it.

## Mixed-clause admission and source-cache provenance repair

Independent review of `111b33b496463bc19e86a1498cb84c973e6151c8` found that
`I love you, kimi ga suki` and `I love you, wo hen xiang ni` borrowed the English
prefix's evidence. This incorrectly skipped English-target original translation and,
when supplied as the only provider translation, suppressed translation of other rows.
Policy v5 requires every nonempty punctuated clause to provide its own English evidence.
Unknown clauses remain unknown. Bounded ordered romanization components also contradict
English in unpunctuated mixed units; open English content vocabulary remains supported.
This is still a conservative bounded policy, not a general language detector.

Physical lyric rows retain their original IDs/text/timing. Provider matching across
separate physical/timed rows still uses the existing any-matching-row whole-document
bypass. New original/provider App regressions cover clause order, punctuation, unknown
tails, Chinese credits and a missing second translation. Fake coordinator inference
checks the same original IDs in prompts, progress, final results and cache replay.

`TranslationLanguageIsExplicit` distinguishes current explicit TTML tags (`true`) from
inference (`false`). Inferred tags are re-evaluated, including clearing a stale wrong tag.
Legacy source-v2 entries with a nonempty tag and unspecified provenance cannot safely
distinguish those cases: they require one successful provider refetch. A failed refetch
does not bless the old tag; later recovery retries normally. Legacy null-tag entries
remain usable without a network request. New explicit short TTML translations survive
cache round trips and still bypass every AI boundary. The ordinary cache fixture now
marks its deliberately supplied language tag explicit; its cache assertions are unchanged.
AI eligibility version v5 fences prior AI results. The actual source48 decisions are
byte-for-byte equal to the v4 diagnostic dump: 43 English-target and 40 Chinese-target
calls, with unchanged per-row audit expectations. Only the reviewed policy version changes.

## Glow and compact presentation iteration

The actual C# renderer was replayed against the same synthetic six-band input, geometry,
phase clock and brightness schedule, with a neutral body mask on dark/light backgrounds.
The first set compares the frozen baseline with four individual changes: contour attack
120 to 60 ms (release remains 120 ms), color travel rates `.045/.060/.033` to
`.065/.085/.047`, quiet autonomous shape amplitude reduced to 25%, and outer ribbon
weight `.18` to `.12`. The combined candidate retains those changes. It does not raise
overall brightness or claim beat detection. Full surround remains the default.

The persistent native **Simplified glow / 简化光效** switch defaults off and is independent
of Off/AI/Music selection and model/cache admission. The final requested range is the
physical center-ray interval from down-left 45 degrees through the bottom to down-right
45 degrees. Each raster sample uses real coordinates; the prior normalized angle still
drives color and six-band travel. The endpoints feather inward over eight degrees;
this is neither half a rectangular mask nor a fixed percentage of the perimeter. Mode
changes crossfade in the existing envelope. Settings persistence, old JSON defaults,
rapid reversal, reduced motion, four DPIs and premultiplied transparent bounds are tested.

The meter and glow now share expiry of stale process-loopback input: unchanged for the
first 150 ms, smoothly reduced to zero bands by 600 ms without a packet. The glow settles
to its quiet baseline while eligible; reduced motion uses static low intensity. Paused
and disabled states retain the common fade-out. A DPI/topology rebuild can transfer a
deep value snapshot once for the same monitor and actual bound track; current visibility,
mode, playback, theme and layout still govern restoration. No old HWND, timer or geometry
is transferred, and stale/reversed state changes cannot revive the snapshot.
Read-only follow-up found three real window-order gaps beyond the initial Core tests:
pre-show transparent geometry discarded the pending snapshot, initial fullscreen
suppression retained it, and a new track target could relabel the preceding frame.
Pre-show geometry now defers the glow decision, suppression immediately clears the pending
state, and capture requires a fresh target followed by an actual frame advancement after
track invalidation. An old timer tick alone cannot reopen that fence. The existing native
lifecycle smoke now exercises the actual first-show/suppression paths and controller frame
ordering; these added Windows paths remain unexecuted in Linux.

Compact untimed paragraphs are flattened only for rendering, preserving original document
text and IDs. Both rows retain independent full-text measurement and marquee. Actual
secondary visibility intersects its viewport and island body. Measured text height uses
the same bounded body geometry, including large accessibility text; the radius is bounded
by both dimensions. New native smoke cases cover long paragraphs at 12/16/17.375/28 DIP
and an off-body translation. Those native smoke cases have not run in this Linux executor.

Synthetic evidence is retained under `/workspace/scratch/glow-motion-preview`,
`/workspace/scratch/glow-motion-combined-111b33b-uncommitted`, and
`/workspace/scratch/glow-motion-physical45-111b33b-uncommitted`. These are cloud paths,
not paths on the user's computer. The latter names record the base commit, not a claim
that its uncommitted candidate was the old committed renderer. The clean MP4s, synchronized
HTML player, per-frame signal/geometry records and source hashes distinguish the snapshots.
The physical-angle player includes optional center/endpoint diagnostic marks; they are
not present in the clean renderer or alpha measurements.

Browser playback/seek and sampled original-size frame sequences were inspected. Color
positions travel more visibly and the outer tail is lighter. The expanded contour remains
subtle and harder to judge on a light background. This is a visual candidate, not a claim
of WWDC2024-equivalent appearance or Windows acceptance. No new continuous Windows
playback, native material/composition or live selected-player audio was observed.

## Current cloud checks and previous Windows failure

| Check | Actual result |
| --- | --- |
| Full Core | 522 passed, 0 failed, 0 skipped |
| Full Infrastructure | 622 passed, **8 failed**, 26 skipped; same DPAPI/read-only-home limits above |
| Linked actual App services | 91 passed, 0 failed, 0 skipped |
| Linked actual rasterizer suite | 22 passed, 0 failed, 0 skipped; native HWND tests excluded from this Linux run |
| Node scripts | 361 passed, 0 failed, 0 skipped |
| Six static PowerShell policies | Passed; 695 synchronized resource keys |
| Evidence Release build, fake contract and frozen source48 audit | Passed; 0 build warnings/errors |
| PowerShell evidence contracts | Passed |
| Actual publication gate | Exit 1 at `pending`; no semantic approval created |

Records are in `/workspace/scratch/dropspace-cloud-checkpoint/mixed-glow-validation`.
Earlier failed runs remain: the first Infrastructure pass had the eight environment
failures plus three cache fixtures without the new explicit-tag marker, and the earlier
glow harness ran three Windows-only tests on Linux and failed to load user32. The Node
fingerprint gate also initially rejected the newly added production glow handoff file;
the code-owned fingerprint now includes it and spectrum freshness. These failures were
not erased or represented as passes. No model, GPU or user-machine test was run.

[Windows run 37021310139](https://github.com/airanluo-dot/DropSpace/actions/runs/37021310139)
passed for **1eee12095 only**: each language had Core 480, Infrastructure 645 with 4 skips,
and App 402 with 3 skips. The later
[run 37026974099](https://github.com/airanluo-dot/DropSpace/actions/runs/37026974099) for
**111b33b4 only** completed with **failure**. English passed Core 490, Infrastructure 649
with 4 skips, and App 414 with 3 skips. Chinese passed Core 490, then Infrastructure had
648 passed, 1 failed and 4 skipped; App was not reached. Both matrices' CT2 38 and Windows
process 7 tests passed. Raw logs/TRX/hashes are under the corresponding cloud scratch run.

The failed Chinese test was
`LlamaCompletionRunnerTests.CancellationDoesNotReturnUntilTheStartedProcessHasExited(False)`.
Reading its PID file at line 78 raised a sharing violation **before** cancellation was
invoked. The child wrote directly to the visible PID path, allowing existence to race
the writer's open handle. This is the source-supported explanation; the actual handle
owner was not independently observed. The fixture now closes a sibling temporary PID
file before atomically publishing the final path. Cancellation/actual-exit/deletion
assertions remain unchanged. The old run remains failed and was not rerun or canceled.
This repair and the current candidate still require their own Windows CI and independent
review; previous matrices cannot qualify this source revision.

## First-candidate Windows CI and remaining acceptance

[Windows CI 37013042584](https://github.com/airanluo-dot/DropSpace/actions/runs/37013042584)
completed successfully for **49a9ac13 only** at 2026-10-02 14:17:01 UTC. Both en-US and
zh-CN matrices passed Core 450/0/0, Infrastructure 610/0/4, and App 385/0/3
(passed/failed/skipped), native worker rebuild, WinUI compilation, packaging and smoke checks.
The CT2 subset passed 38/38 and WindowsInferenceProcess 7/7 without skips, including repeated
immediate cancellation/deletion and the caller-timeout kernel exit-signal test. Skipped tests
require real models/live providers or interactive Apple Music/PCM/volume input. This is
compile/test/package evidence, not observation of real playback or new dynamic UI frames.
Raw TRX, hashes and terminal workflow state are retained in
`/workspace/scratch/windows-ci-37013042584`.

The candidate must receive new Windows CI for its exact pushed commit, including both en-US
and zh-CN matrices, native cleanup tests, worker rebuild, WinUI compile and packaging. Old
a31 CI and user screenshots do not validate these repairs. Requested Library screenshots
could not be materialized by the official helper here; no actual screenshot pixels or new
dynamic Windows frames were observed. Native controls, 100/125/150/200% physical DPI, text
scale, keyboard/high-contrast behavior, halo layering at the top edge and actual playback
still need observation on Windows. Added native visual diagnostics cover these boundaries
without downloading models, but their presence is not a recorded pass.

Real 7B quality, completed loading/generation, Windows cancellation/unload, production
CPU/GPU memory and latency, and 16 GB GPU behavior remain unqualified. The cloud safety stop
is retained and does not count as a quality comparison or a new-candidate validation.
The repaired evidence adapter still needs its own genuine model-bound Windows captures.
Publication stays blocked until those captures and independent
same-commit review exist. Later user-machine acceptance should reuse installed models and
directories, retain local uncommitted changes, and clean only this task's obsolete outputs.
