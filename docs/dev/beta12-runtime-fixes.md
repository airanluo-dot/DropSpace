# Beta12 AI settings and runtime observations

Stop before packaging; no publication, remote push, model download, user-cache deletion
or application restart. The owner subsequently authorized minimal real CUDA
translation and failure-specific repeats. The three runs and resulting fix are below.

## Reviewed input

Reviewed `git status`, `git worktree list`, and the handoff in
`E:/Dev/DropSpace-beta13-runtime-fixes` before editing. Reviewed commit
`4d4b642aee763d2ac6c2e2e3921a6478fb6774b0` selectively; no branch merge or wholesale
file replacement. Reused the loaded-card refresh, generation fencing, fixed-category
stderr diagnostics and settings-load/commit logging. App startup was integrated
manually around the existing Beta12 downloader initialization. The old worktree and
its uncompiled commit are not validation evidence. The version stays Beta12.

## Findings that code establishes

- The settings card is constructed before `MainViewModel.InitializeAsync` loads
  settings. While unloaded it is not subscribed, and its old Loaded handler did
  not refresh controls immediately. This permits a stale off switch on entry; it
  does not establish that the user's disk preference was lost.
- `SettingsChangePolicy.Merge` replaced the whole Lyrics record when any member
  differed from the baseline. An older snapshot changing, for example, font size
  could overwrite an independently saved AI toggle. This is a confirmed lost-update
  path, not proof that it caused the reported historical incident.
- `ActualBackend` read the previous successful worker response without current
  request/configuration identity. Cache hits, changed preferences and exited workers
  could leave a historical CPU label presented as current execution.
- GPU failure suppression lasted until the preference generation changed. Native
  stderr was discarded, and fallback/exit details were not retained in file logs.
- Model deletion saved AI off before attempting deletion. Cancellation/failure
  could therefore change user preference even though removal did not complete.

## Changes and runtime contracts

- Lyrics edits merge every persisted editable member independently against the
  latest settings. Download settings remain in the same explicit merge whitelist.
  Commit and compensation also merge only changed fields. The existing editor,
  atomic temporary-file store, runtime rollback and visible save error are retained.
- The AI card refreshes under its event suppression guard on every load. Loading
  controls never saves defaults. Missing models and unavailable runtimes do not
  change AI consent. Model removal preserves consent and explains this in its
  confirmation, while maintenance still cancels inference and waits for native exit.
- The first startup recovery report survives the second settings load. A persistent
  warning distinguishes preserved preferences from recovery to defaults. JSON null
  is treated as invalid configuration, quarantined through the existing mechanism;
  it is no longer a silent default snapshot. Startup read/commit diagnostics include
  validated flags/model ID and never the full configuration.
- Runtime observations are immutable and fenced by model hash, preference and
  generation. UI separates selected backend, worker preparation, executing backend,
  waiting, cache hit and timestamped last successful response. Both the service's
  cache lookup and the coordinator's second cache lookup identify cache use.
- An executing backend requires the launched, verified worker's validated ready
  handshake. Successful translation evidence additionally requires a matching
  complete response frame and complete bounded plaintext line. A ready handshake
  or ready translation business state alone never claims successful CUDA inference.
- Preference and managed component changes invalidate observations immediately.
  Inactive/idle/exited workers are not displayed as currently executing. Old success
  remains explicitly historical only within the same configuration generation.
- GPU failures retain the requested preference, attempted backend, UTC time,
  allowlisted native/error category, exit code when obtained, host-termination flag
  and whether CPU fallback was attempted. The UI displays a short localized reason;
  existing rotating local logs retain the record. GPU and CPU attempt failures are
  separately logged without replacing the original GPU cause in the UI.
- EOF gets at most 250 ms to observe natural process exit before cleanup. Exit codes
  are captured before Process disposal, and host termination is not confused with a
  natural native failure. Stderr is drained with bounded buffers; only fixed reason
  categories survive, never raw lyrics, prompts, paths or commands.
- CPU fallback still waits for complete GPU cleanup and still respects host-memory
  admission. Failure suppression has a one-minute monotonic cooldown; only a later
  requested inference can retry. Configuration changes and the explicit retry
  button reset it immediately. Retry retains cached lyrics and does not automatically
  download or delete anything. Invalid integrity/protocol results do not bypass checks.

## CUDA path reviewed, without inferring hardware success

Shipping DI selects `PersistentPlainLyricsRunner.CreateAutomatic`. CUDA selection
requires the existing compatible NVIDIA probe and installed component files;
`OpenWorkerLeaseAsync` verifies the complete pinned files before launch. Windows
launch uses the absolute worker path and the worker's own directory, a mandatory
job/memory limit and scrubbed GPU environment overrides. The ready frame must match
backend, component ID, protocol and selected 7B profile when applicable.

Reviewed `CudaDriverAvailability.cs`, `WindowsInferenceProcess.cs`,
`tools/plain-lyrics-helper/main.cpp`, `gpu-policy.h`, and
`tools/cuda-lyrics-helper/{cuda-device.h,adapt_worker.py,CMakeLists.txt}`. CUDA uses
the maintained main.cpp through the build overlay. The existing CUDA 13.4, compute
7.5+, discrete-device and measured free-VRAM checks remain unchanged. The native 1.8B VRAM budget
is model bytes + 1 GiB + max(1 GiB, 20% total VRAM); a compatible product name or
driver alone cannot establish free-memory admission. Native code 66 denotes GPU
admission failure broadly, not proof of insufficient VRAM. No evidence in this
session justifies changing kernel binaries, DLL bindings or native VRAM limits.

Read-only local inspection found AI=true, GPU=true, Automatic, Hy-MT2 1.8B in the
current settings file (last write 2026-10-05 13:43:39 UTC). The two retained application
logs contained no matching settings quarantine/recovery/save-failure or new runtime
failure records. These observations do not establish startup-before-save state,
historical CUDA success/failure, or absence of earlier rotated-out failures. The
user's actual settings, installed models, components and lyric caches were not changed.

## Authorized CUDA execution: reproduced and fixed

The minimal request used the actual production App service source linked into the
probe and current Core/Infrastructure, the installed App's embedded resource assembly,
and installed pinned model and CUDA files. It followed `AiLyricsService.TranslateForPublicationAsync`
through `PlainHyLyricsPackageResolver`, `PlainHyLyricsBackend`, `PlainHyLyricsCoordinator`
and `PersistentPlainLyricsRunner.CreateAutomatic`. A fresh diagnostic cache prevents
an old translation from passing. No user preferences or existing caches were changed.

Run 1 launched CUDA with cuBLAS/cuBLASLt and the NVIDIA driver loaded, allocated
about 2.08 GB on that process's GPU, then naturally exited 67 during model/context
loading. CPU fallback returned a translation but explicitly **failed CUDA acceptance**.

Run 2 repeated only that failure with a read-only Job completion-port observer.
Windows reported message 9 (`JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT`) for CUDA PID 42512.
The production cap was 3,221,225,472 bytes; sampled peak committed memory reached
3,185,717,248 bytes before the rejected allocation. This establishes a host committed
memory limit failure, not a driver version, DLL hash or native free-VRAM rejection.
The [Windows Job limits documentation](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)
distinguishes these process commit limits from dedicated device memory.

`ResidentInferenceMemoryPolicy` now assigns the CUDA 1.8B profile a **bounded 5 GiB
host cap** and requires both available physical RAM and available commit to cover
that cap plus the existing 1 GiB system reserve before starting. Job/working-set
watchdog enforcement remains active. CPU/Vulkan limits, the 7B profile, integrity
checks and native VRAM thresholds remain unchanged. The correction applies by
backend and verified model identity, not by laptop/device name.

Run 3, with only that production host-budget correction, succeeded on CUDA:

- CUDA worker PID 23928, RTX 5080 Laptop, driver 616.56, compute capability 12.0.
- Matching ready/complete protocol results; no CPU worker, fallback, or cache hit.
- Sampled CUDA-process dedicated GPU memory peak **2,629,894,144 bytes (2.45 GiB)**,
  with corresponding GPU engine activity up to **50.21%**. Windows labels this
  driver's CUDA execution on its 3D engine. Native source selects this adapter and
  offloads all model layers (`n_gpu_layers = 999`), with fitting disabled.
- Sampled host Job commit peak **3,625,578,496 bytes (3.38 GiB)**, above the old cap;
  no memory-limit notification with the new cap.
- About 4.82 seconds end to end, including existing integrity validation; this is
  one correctness observation, not a performance benchmark.
- Synthetic input: “The morning sunlight shines through the window, and we will
  meet beside the river after breakfast.”
- Actual CUDA output: “晨光透过窗户照进来，我们会在早餐后于河边见面。”

The installed Beta11 application and native assets were not replaced. This proves
the Beta12 production managed call chain works on this machine with those installed
native assets; it does not claim a packaged Beta12 UI acceptance run, 7B verification,
or universal GPU/driver coverage. The settings-restart historical cause remains
unproven. See [compact evidence](evidence/beta12-local-cuda/verification.json) and
[reusable narrow probe](../../tools/AiLyricsCudaProbe/README.md).

## Validation

Manual source/diff review and necessary WinUI Debug x64 compilation. Initial
compilation and the final incremental compilation both succeeded with zero warnings
and zero errors; the final result is recorded in `.codex/beta12-runtime-fixes-build.log`.
`git diff --check` found no whitespace errors and both resource files parsed without
duplicate keys. Following the owner's updated authorization, only the three one-line
CUDA runs above were performed. The final App build including the host-memory fix is
in `.codex/beta12-final-build.log`; the probe has its own necessary compilation log.
No test suites, native worker builds, restart verification, UI interaction tests,
model downloads or download pressure tests were run.
