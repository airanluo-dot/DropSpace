# Plain Hy production evidence capture

This finite Windows harness captures the current shipping plaintext Beta through
`PlainHyLyricsBackend` → `PlainHyLyricsCoordinator` → the actual
`PersistentPlainLyricsRunner.RunPlainAsync`. `IPlainLyricsRunner` is an observation boundary,
not a replacement native launcher. The argument vector is obtained directly from
`PersistentPlainLyricsRunner.BuildArguments`; only the model path value is
normalized to `$MODEL` in recorded configuration.

## Run once per explicitly selected model and variant

Use PowerShell 7, .NET SDK 10 and Node on Windows x64. Build the current runtime first
with the repository's normal build workflow. Supply the current eight-file shipping
payload, including `LICENSE-llama.cpp`; the historical candidate artifact lacking
that license or the CPU/AVX2/Vulkan resident workers is not accepted. The model must already exist and match the production
catalog's exact Q8 bytes. This harness does not download or install anything.

```powershell
./scripts/plain-hy-production-evidence/Run-WindowsProductionEvidence.ps1 `
  -RuntimeDirectory ./artifacts/ai-runtime/win-x64 `
  -ModelPath $env:DROPSPACE_AI_SMOKE_MODEL `
  -ModelId hy-mt2-18-q8-plain-beta `
  -Variant Baseline `
  -OutputDirectory ./artifacts/plain-hy-production/production-plain-hy-RUN-ATTEMPT-baseline
```

The default application model remains 1.8B. To capture the optional 7B, explicitly pass
`-ModelId hy-mt2-7b-q8-plain-beta` and the already downloaded official pinned 7B
file as `-ModelPath`, with a different output directory. Selection never reuses or
relabels 1.8B evidence. Model ID, SHA256, byte count, actual inference-call hash,
startup profile and memory budget must all match the current catalog.

Run a separate, explicit invocation with `-Variant Avx2` and another output basename
for AVX2. Both capture modes explicitly set `GpuEnabled=false`, regardless of the
application's user-facing default. The required two-song envelopes do not prove
Vulkan, NVIDIA, AMD, or integrated GPU behavior. There is no automatic variant fallback. AVX2 requires the same AVX2/FMA/F16C CPU
eligibility as production. Existing output directories are refused, even if empty.

Each invocation schedules exactly:

1. A fresh resident model session for the entire unchanged `scripts/ai-model-qa/inputs/source48.json` into English
   Generation uses an actual production progress context with fixed playback position
   zero; every ephemeral callback and its latency are recorded in source-line order
2. A production cache preflight and repeated request, requiring no new inference
3. Confirmed resident cleanup, another fresh model session for the entire unchanged
   fixture into Simplified Chinese, and the same cache checks
4. A separate one-line request through the actual backend, canceled only after a
   new process at this capture's exact runtime path is observed alive
5. Production cleanup draining, native PID/start-time exit observation, prompt
   removal checks
6. A separate fresh default-setting (`GpuEnabled=true`) one-line request for fixture
   line 12 into English, recording actual protocol-confirmed `cpu` fallback or
   `vulkan` execution, observed worker, output and confirmed cleanup
7. Before/after source, model and runtime identity checks

A target has the production 300-second whole-song budget and each actual native
call has its production 60-second limit. The model-specific process cap is 3 GiB
for 1.8B or 12 GiB for 7B. Production CPU admission (including GPU-to-CPU fallback)
requires available physical memory and available commit of at least that cap plus
a 1 GiB reserve. These are conservative engineering allowances, not measured model
peaks or promises for any installed RAM size. The runner's bounded
cleanup remains in effect. The cancellation probe observes for up to 10 seconds,
then requests cancellation even if observation failed, awaits at most 85 seconds,
and drains production cleanup with a 20-second admission cancellation and 30-second
outer observation bound. An unobserved process is a failed probe, never a
successful cancellation claim. No unrelated process is killed or controlled. The supplemental GPU-default probe
has a 60-second request budget and the same bounded cleanup. Its fresh backend can
use the production runner's single CPU fallback; the harness itself never retries.
The packet fails if this probe fails, but CPU fallback is a valid technical result.
`deviceVendor` is always `unverified`; Vulkan mode alone is not NVIDIA/AMD or other
physical-device coverage.

## Independent admission audit

The unchanged original `source48.json` remains the inference input. The separately
reviewed `source48-admission.json` records all original display IDs, source text,
expected production language/confidence/evidence, per-target eligibility and
exclusion reason, and each eligible physical segment's index/text/SHA256. Its
human language annotations are for auditing only and never enter the prompt or
production source metadata. Capture compares actual policy decisions against
this frozen file and fails on drift; it never regenerates expectations.

Under policy v3, all 48 rows remain eligible for English: the bounded English
vocabulary conservatively leaves these longer English sentences unknown. For
Simplified Chinese, rows 37, 38, 39, 40, 41, 44, 46 and 47 have positive matching
evidence and remain original, leaving 40 calls. Japanese/Korean verses and unknown
rows stay eligible. Short known English phrases, credits at the start/middle of
an untimed block, and multiple semantic segments sharing one display ID are
verified in separate fake-inference contract tests, not claimed as native fixture
coverage. If the policy changes, review the fixture plan again before capture.

Every actual call records the original row ID, segment index/hash and verified
model hash. Progress reports completed display IDs, not fabricated segment IDs.
The gate requires the complete eligible call set and progress set, unchanged
source text/timing, final text matching the observed runner outputs, no AI text
on excluded rows, and actual cache preflight/replay documents with zero new
calls. Schema-1 runner output and schema-2 capture/configuration cannot be
relabelled as the new schema-2 runner/schema-3 capture.

Host physical/commit availability is sampled before each cold request and the
GPU-default probe. Production repeats its own admission immediately before CPU
load. Process observations record working set, OS peak working set and private
bytes; these are process observations, not VRAM measurements or hardware quality
claims. Resource refusal preserves failure records and cannot emit a successful
evidence envelope. CPU fallback does not certify GPU execution.

## Evidence and failures

The capture snapshots the exported current gate scope, all its source files,
fixture, complete runtime payload and configuration before execution. The runtime
manifest is embedded into the capture assembly at build time and checked against
the invocation snapshot through production `AiLyricsRuntimePackage`. The harness
assembly/dependencies and build logs are retained. Snapshot files are made read-only and held open with read-only sharing throughout native execution;
source/model/runtime/configuration identities are checked again after execution.

The resident worker is reused between line requests inside each song. Production
`DrainCleanupAsync` closes that resident session after each cold target, and again
before the separate cancellation probe. The next target therefore starts a fresh
worker/model load; the wrapper does not rerun a failed target. Each returned cold
call also records the observed live worker PID/start time and requires a single
unchanged resident worker throughout that song. The exact extracted
worker path returned by production runtime resolution is hashed and recorded.

`en.runner-output.json` and `zh-Hans.runner-output.json` preserve every actual
runner return value, including copied lines, with host line IDs, original source
text, exact prompt, timing and status. A create-only `call-NNN.json` journal is
written immediately after each invocation settles, including failures. These are
**runner-returned outputs, not raw native stdout or resident protocol frames**: production `RunPlainAsync`
removes the runtime terminator and trims terminal whitespace before returning.
The observer never trims, repairs, replaces, translates or synthesizes its output.

`NoUsefulTranslation` is a valid completed technical outcome. Copies are neutral.
No semantic score is calculated. A timeout, rejected line, incomplete eligible-segment set,
new inference during a cache replay, unobserved cancellation, or unconfirmed cleanup
fails the capture. Available calls, target outputs and failure records remain.
There are no retries and no automatic reruns. A failed capture emits no successful
`native-output.json` envelope and cannot become approval by its process exit code.

Only a fully completed capture writes the schema-3 `native-output.json` and
`evidence-reference.json`. References use the logical repository root
`scripts/ai-model-qa/evidence/<output-directory-basename>/...`. When importing reviewed evidence, copy only the referenced configuration,
`native-output.json`, two target output JSON files, both `*.cache-output.json` files, `gpu-default-probe.json`, and the byte-exact runtime
manifest into the repository evidence directory. Preserve that basename and
referenced file names exactly; the review runtime-manifest reference can point to
`<logical-root>/runtime-snapshot/runtime-manifest.json` without its sibling binaries.
Retain source snapshots, compiled harness, native binaries and logs in CI artifacts;
do not commit the whole dump or native executables. The artifact contract binds the
original archive files. The generated packet must still be reviewed and connected to
the reviewed runtime artifact by the parent release gate. Nothing here creates or
modifies release approval, commits, uploads, publishes, or claims semantic quality.

The parent gate's `--print-scope` export is required. Invocation must fail if source,
protocol, limits, native arguments, compiled-helper sampler arguments, model or
fixture drift. The worker manifest must match the current resident profile and the
normalized combined source hash exported by the gate for CMakeLists.txt,
gpu-policy.h, and main.cpp. The wrapper does not
accept a user-authored substitute scope or loosen budgets for a slow run.

## Local contract checks (no native evidence)

```sh
dotnet restore scripts/plain-hy-production-evidence/PlainHyProductionEvidence.csproj --locked-mode
dotnet build scripts/plain-hy-production-evidence/PlainHyProductionEvidence.csproj --no-restore -c Release
dotnet scripts/plain-hy-production-evidence/bin/Release/net10.0/PlainHyProductionEvidence.dll --contract-self-test
dotnet scripts/plain-hy-production-evidence/bin/Release/net10.0/PlainHyProductionEvidence.dll --verify-fixture-admission scripts/ai-model-qa/inputs/source48.json scripts/plain-hy-production-evidence/source48-admission.json
pwsh -NoProfile -File scripts/plain-hy-production-evidence/Test-ProductionEvidenceContract.ps1
```

These platform-neutral tests exercise the production coordinator with a fake
inference seam, neutral copies/no-useful memoization, cache behavior, deterministic
prompt/line mapping, verbatim output preservation, failure preservation and
create-only journal semantics. They explicitly do not establish Windows/native
execution. `--print-native-arguments <model-id>` prints the production builder's normalized
argument vector without starting inference.

No 7B Windows capture or quality approval is established by these tool changes.
The approval manifest remains pending; each model needs independent complete
Windows baseline/AVX2 evidence and a genuine semantic review before publication.
