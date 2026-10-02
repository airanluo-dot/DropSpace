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

## Actual cloud verification

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
