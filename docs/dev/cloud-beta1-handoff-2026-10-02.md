# Cloud WIP handoff — 2026-10-02

This is a recoverable work checkpoint, **not visual acceptance or release approval**.
The user explicitly prohibited quota-card use, billing changes and quota recovery.
No such operation was attempted. Do not retry model calls to recover exhausted quota.

## Repository and authority

- Repository: `airanluo-dot/DropSpace`.
- Branch: `agent/cloud-beta1-fixes-20261002`; normal push only.
- Required baseline: `a31d0092dc2b3fb5c4c437bb100429d32cbf0bfc`, tree
  `1cf13129f84f948cc1653ed67b819ef6fada5e44`.
- Previously pushed checkpoints: `49a9ac13dd3d596972d5ac8cabc8d7304171b584`,
  `1eee12095d240ce9605a58cde46c80e477f6a8b5`,
  `111b33b496463bc19e86a1498cb84c973e6151c8` (tree
  `408d44591fbf53addef365b92c23c2e519f80b4e`). The commit containing this handoff
  is the next WIP checkpoint; obtain its exact SHA/tree with `git rev-parse HEAD HEAD^{tree}`.
- Root was the only repository writer. Other agents only reviewed or wrote isolated scratch.
- Modify/test/commit/push are authorized. No release, tag, merge or deployment.
  A needed PR must remain draft. Independent review is coordinated by the parent.
- Do not touch the user's Windows RazerBlade, restart local testing, alter drivers/services,
  or run new GPU stress. Reuse existing models/directories when later explicitly returning
  to Windows; remove only our obsolete products, never user data.

## What this checkpoint contains

The full implementation history is in `cloud-beta1-fixes-2026-10-02.md` beside this file.
All ten originally requested repair areas are represented in the branch: rapid-song
cancellation/refresh recovery; provider-before-AI bypass; same-language/credit admission;
native glow selection; labeled refresh; live frequency-driven light; inner overlap and
under-body layering; visible-AI glow/fades; independent translation marquee; persistent
AI-label visibility. Default Hy model, fixed prompt and sampler are unchanged.

The latest additions are:

1. `LyricsLanguagePolicy` v5 takes evidence per clause, retaining unknown/foreign clauses
   beside English. It supports open English vocabulary, bounded romanization structures,
   varied punctuation and word-internal apostrophes. It keeps cross-physical-row provider
   any-matching whole-document bypass and unchanged display IDs.
2. `LyricsLine.TranslationLanguageIsExplicit`: true for explicit TTML, false for inferred,
   null for unspecified legacy provenance. Legacy source-v2 nonempty tags/null provenance
   require one successful provider refetch; null-language legacy cache still reclassifies
   locally. Inferred tags reclassify and clear wrong matches. App tests cover outage during
   migration, recovery and cache reuse. Eligibility v5 fences old AI cache.
3. `IslandGlowRasterizer` remains **real-time C# rendering**, not embedded video. It consumes
   the same six real selected-process FFT bands as the meter. Selected candidate parameters:
   attack 60 ms/release 120 ms; palette rates .065/.085/.047; quiet autonomous contour scale
   .25; outer ribbon weight .12. A stronger band-relief experiment was rejected: little
   visible benefit and slightly greater outward spread. No beat-detection claim.
4. Native persistent `SimplifiedGlow` defaults false. Full surround stays default. Enabled
   coverage uses true center rays down-left 45° through bottom to down-right 45°, with an
   inward 8° smooth feather. The wide capsule's normalized color angle is not reused for
   this physical-angle mask. Continuous shape crossfade; no body-hit-region expansion.
5. `SpectrumFreshnessPolicy`: no new packet for 150 ms starts a smooth shared decay, zero
   bands by 600 ms. Eligible glow retains a quiet baseline; reduced motion uses static light.
6. `LyricsGlowHandoff` transfers only deep numerical state across same-monitor/same-track
   DPI rebuilds. Read-only review found and root fixed pre-show clearing, initial fullscreen
   invalidation, and stale track-frame capture. A fresh SetTarget **and** subsequent frame
   advance are required after invalidation; an old target timer tick cannot reopen capture.
   Native lifecycle smoke now exercises the real window/control paths; it is not run on Linux.
7. Compact paragraphs flatten only for rendering. Both rows retain independent full text,
   measurement and scrolling. Visibility intersects both viewport and actual island body.
   Shared height/radius bounds cover accessibility sizing; new native diagnostics cover
   12/16/17.375/28 DIP and off-body text.
8. Windows test fixture PID publication now closes a sibling temporary file before atomic
   move. No cancellation/actual-exit/deletion assertion was weakened.

## Actual verification and failures

Latest cloud results, under `/workspace/scratch/dropspace-cloud-checkpoint/mixed-glow-validation`:

| Check | Result |
| --- | --- |
| Core | 522 passed, 0 failed/skipped (`core-final.trx`) |
| Actual App services in temporary managed harness | 91 passed, 0 failed/skipped (`app-managed-final.trx`) |
| Full Infrastructure | 622 passed, **8 failed**, 26 skipped (`infrastructure-exact.trx`) |
| Actual rasterizer, Windows HWND tests deliberately outside this Linux run | 22 passed, 0 failed/skipped (`raster.trx`) |
| Node | 361 passed, 0 failed/skipped (`node-final.log`) |
| Six PowerShell policies | Passed, 695 synchronized resource keys (`*-final.log`) |
| Evidence Release build, fake contract and frozen fixture audit | Passed, 0 build warnings/errors |
| Actual publication gate | Exit 1: semantic approval still pending |

Infrastructure's eight retained failures: seven Windows DPAPI calls on Linux and one
existing receive-route write to read-only `/home/agent/Downloads`. Do not relabel them passes.
The first new Infra run additionally failed three fixtures whose intentionally supplied
language tags lacked the new explicit marker; their metadata was corrected, assertions kept,
and full reruns returned to the same eight environment failures. Original failing logs remain.
An earlier unfiltered glow harness failed three user32 tests on Linux; that run also remains.
Source48's actual v5 dump equals the v4 dump: English-target 43 calls, Chinese-target 40.
Human language annotations never enter production metadata or the fixed prompt.

Windows run `37026974099` for **111b33b only** ended **failure**:
en-US Core490/Infra649+4skip/App414+3skip all passed; zh-CN Core490 passed,
Infra648pass/1fail/4skip, App not reached. Failure:
`LlamaCompletionRunnerTests.CancellationDoesNotReturnUntilTheStartedProcessHasExited(False)`
at line78 reading PID, before cancellation. The writer-publication race is supported by source
and failure timing; the actual handle owner was not independently observed. Both matrices
passed CT2 38/38 and Windows process 7/7. No rerun/cancellation was used to erase the failure.
Raw evidence: `/workspace/scratch/windows-ci-37026974099`.

Older Windows runs `37021310139` (1eee) and `37013042584` (49a9) succeeded, but do not qualify
this WIP. A push triggers fresh CI. The new HWND lifecycle/visual smoke must compile and run
on Windows; Linux managed tests cannot establish that result.

## Retained agent evidence (not unmerged repository patches)

- `/workspace/scratch/glow-motion-preview`: baseline + four one-factor C# verification
  recordings, manifest, per-frame results and synchronized player.
- `/workspace/scratch/glow-motion-combined-111b33b-uncommitted`: historical combined full/
  lower-half prototype/switch evidence. Lower-half range is superseded, not current acceptance.
- `/workspace/scratch/glow-motion-physical45-111b33b-uncommitted`: current full/physical45/
  switch recordings, physical-angle/DPI checks, optional endpoint marks in HTML, source
  manifest, same-frame aesthetic review. These are synthetic-signal renderer recordings,
  not Windows playback and not in-product video assets. Root/agent inspected sampled pixels
  and automatic browser playback, not continuous live Windows aesthetics.
- `/workspace/scratch/glow-band-relief-111b33b-uncommitted`: rejected stronger audio-contour
  candidate; source diff, matched-input metrics and explicit rejection rationale.
- `/workspace/scratch/glow-45deg-geometry`: center-ray coordinate diagrams.
- `/workspace/scratch/v5-language-provenance-readonly-review.md`: reviewed v5/cache paths;
  pipe-separator finding resolved with real Core/App tests.
- `/workspace/scratch/mixed-latin-clause-review`: read-only recommendations and suggested cases.
- `/workspace/scratch/english-admission-red-green`: historical old/current six-case probe.
- `/workspace/scratch/hy-cpu-comparison/review-49a9`: prior bounded CPU diagnostic, complete
  32 planned case ledger. 17 starts/16 completed/one 7B safety stop/15 not started. Do not rerun.
  No 7B translation/quality result. Models remain outside Git in cloud cache; no GPU ran.

There are no agent-owned repository modifications waiting to merge. Raw scratch artifacts
stay in this executor. A compact optional comparison recording was requested for Library
delivery; upload completion/Library ID must be reported from the actual tool result, never
inferred from its local path. Saving this code checkpoint must not wait for that recording.

## Reproduce and continue

Fetch the branch normally, verify HEAD/tree, and read AGENTS.md plus
`.agents/skills/dropspace-maintainer/SKILL.md`. Do not start from old main/PR76/a31 again.
The exact cloud SDK is 10.0.401:

```bash
export DOTNET_ROOT=/workspace/.tools/dotnet-deb/usr/share/dotnet
export DOTNET_CLI_HOME=/workspace/.cache/dotnet
export NUGET_PACKAGES=/workspace/.cache/nuget
export DOTNET_NOLOGO=1
export PATH=/workspace/.tools/powershell-deb/opt/microsoft/powershell/7:$PATH
"$DOTNET_ROOT/dotnet" test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj --no-restore
"$DOTNET_ROOT/dotnet" test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore
"$DOTNET_ROOT/dotnet" test /workspace/scratch/app-managed-regression/AppManagedTests.csproj --no-restore
"$DOTNET_ROOT/dotnet" test /workspace/scratch/glow-regression-harness/GlowRegression.csproj --no-restore --filter FullyQualifiedName~IslandGlowRasterizerTests
node --test scripts/*.test.mjs
"$DOTNET_ROOT/dotnet" build scripts/plain-hy-production-evidence/PlainHyProductionEvidence.csproj --no-restore -c Release
"$DOTNET_ROOT/dotnet" scripts/plain-hy-production-evidence/bin/Release/net10.0/PlainHyProductionEvidence.dll --contract-self-test
"$DOTNET_ROOT/dotnet" scripts/plain-hy-production-evidence/bin/Release/net10.0/PlainHyProductionEvidence.dll --verify-fixture-admission scripts/ai-model-qa/inputs/source48.json scripts/plain-hy-production-evidence/source48-admission.json
```

Do not repeat already passed tests merely to spend time. Run new checks for new edits,
failures or unresolved issues. Shared Core/Infra builds must stay sequential. CI logs via
`gh` may return403; the official GitHub connector fetched archived job logs/artifacts
successfully. Running job-log404 is not evidence of a new failure.

Minimal regressions: English-target original `I love you, wo hen xiang ni` stays eligible;
provider source `君が好き` / `我的世界充满阳光` with only first secondary equal to that mixed
sentence cannot whole-song bypass. A source-v2 wrong `en`/null-provenance cache must refetch;
explicit `World` TTML must still zero-call bypass after migration. For glow handoff, test
first hidden geometry → show, initial fullscreen suppression, and
Invalidate → old Advance → SetTarget → current Advance; old Advance must not authorize capture.

Remaining: exact-new-SHA Windows CI, independent external review, actual Windows DPI/
accessibility/keyboard/high-contrast/real player playback and continuous visual acceptance.
The expanded live contour remains subtle on light backgrounds; a renderer recording is
not aesthetic approval. Genuine model-bound Windows captures and semantic review are still
absent; publication stays blocked. Do not run/download models to fill this gap without the
parent's later plan. No user-computer actions, quota recovery or release operations are next steps.
