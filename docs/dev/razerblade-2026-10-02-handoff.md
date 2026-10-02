# Windows validation handoff, 2026-10-02

This change continues the isolated handoff from commit
`75fdb35cd3c67d0ceb7d52f913c18ed3acaaeb4b`. It does not authorize publication,
a release tag, merging, or changing any user's installed app, models, or caches.

## Repairs

- Prepare pinned Vulkan dependencies in a local workspace, including the
  SPIRV-Headers CMake package and complete upstream notices. CI and the
  non-publishing release producer now prepare these before native compilation.
- Build with Ninja Multi-Config; allow an explicitly fresh, guarded temporary
  build directory for deep Windows checkouts. Verify resident helper inputs
  again before producing the trust manifest. Preserve the frozen four-thread
  model profile.
- Make the approval validator's resident digest agree with the PowerShell
  producer's normalized LF/newline joining. Synthetic regressions cannot approve
  the real release manifest.
- Permit only the exact three model-facing localization constants. Modified
  prompts, duplicate definitions, and UI copies still fail the policy guard.
- Use extended native paths when safely opening long Windows payload paths,
  retaining reparse-point checks. Normalize the app resource output when MSBuild
  supplies an absolute intermediate directory.
- Replace POSIX-only resident, Plain/completion/tokenizer, and CT2 fixtures with
  actual Windows apphost children. The production process owner, Job, pipes,
  prompt sharing, cancellation, and cleanup remain under test. These synthetic
  workers do not load models or exercise a GPU.
- Actually probe Windows file/directory symlink creation rather than assuming
  lack of privileges. Preserve original protection assertions and add Windows
  NTFS junction counterparts. No permission settings are changed.
- Record a Vulkan worker's selected physical adapter in readiness metadata.
  Native GPU gates can explicitly require Vulkan instead of passing by fallback.

## Observed validation

- Core: 397 passed, no failures or skips.
- Infrastructure: 581 passed, no failures or skips with `TestCategory!=NativeAiRuntime`.
  The real model rows are deliberately excluded from this final run because of
  the GPU pause below. All 16 old POSIX chain cases and six original link cases
  executed on Windows; five additional junction cases executed too.
- App: 360 passed, one opt-in real Apple Music test unexecuted. That test requires
  `DROPSPACE_APPLE_MUSIC_NATIVE=1` with a lyric-bearing track playing.
- Prior native CPU/GPU plus completion/tokenizer lifetime gate: four passed.
  Production GPU execution reported `backend=vulkan` and `cpuFallback=False`.
- A real public-song LRCLIB query returned HTTP 200 and 92 timed lines. Real
  provider-selection requests also ran; their recorded QQ HTTP 500 and LRCLIB
  HTTP 503 responses must not be described as every provider being healthy.
- Node: 335 passed, no failures or skips, including actual Windows file-symlink containment.
- Localization, Windows compatibility, hardcoding governance, secret hygiene,
  and runtime-notice checks passed. Node results and exact TRX/binary hashes are
  retained in the isolated lab handoff; release approval remains pending.

## Native performance evidence and open work

CPU AVX2, NVIDIA RTX 5080 Laptop and AMD 880M completed the same invented 20-line
sample with the same pinned Q8 model, prompt/sampler, and four threads. The
interrupted comparison retained one CPU run and two runs per GPU (100 completed
lines total), with exact input/runtime/model/source-snapshot identities and
per-line timings. Fresh process/model loading was measured; OS file caches and
driver shader caches were not cleared. This is a limited sample, not a
stability or semantic-quality approval. AMD was selected through a test-worker
environment filter; normal selection on this dual-GPU machine preferred NVIDIA.

The user observed an NVDisplay.Container.exe native-debugger prompt. All new
GPU work was paused, the dispatcher was cancelled, and no test inference worker
remained. Application and System queries for 11:10-11:25 UTC succeeded but
returned no matching NVIDIA fault/display event. The exception timestamp,
fault module, code and relationship to the tests remain unknown. No administrator
debugging, service/driver changes, or security changes were performed. Closing
the prompt does not resolve this risk.

The isolated Release EXE and Apple Music were launched, but this delegated task
has no callable `node_repl`/Computer Use entry. The official MCP query returns
`unknown MCP server 'node_repl'`. No playback, volume, mouse or keyboard action
was performed. Automated app tests and process launch are not interactive UI
acceptance. A directly launched local task with supported Computer Use tools is
still needed for natural/manual track switches, seeks, progressive translation,
GPU toggle/fallback, music refresh, expanded-island layout, fullscreen and
window interaction. A comprehensive web Codex audit and the remaining regression
must review the same final source commit. Do not treat the older PR head as the
reviewed final candidate.
