# Windows candidate loading + fixed-screen QA handoff

Prepared 2026-10-02. This package does not edit production, run or cancel GitHub workflows, download a runtime, or promote a model. The migrated C# harness compiled on Linux with zero warnings/errors and locked restore passed. Official PowerShell 7.5.3 parsed the QA script and all 40 Release inline PowerShell blocks without errors; both inline Bash blocks passed bash -n. Windows native execution has NOT been performed here.

## Purpose and sequence

The Linux Qwen3-1.7B Q8/Q4 and IBM Granite3.3-2B Q4 processes ended with SIGKILL while loading. No QA memory/deadline watchdog fired and no translated text existed. Those outcomes are environment/loading blockers, not semantic failure evidence. Use Windows to separate those issues.

1. Obtain an already-built, trusted Windows runtime artifact from the existing pinned-source build. Do not launch or cancel CI for this script.
2. Verify its runtime manifest SHA256 through the trusted artifact provenance. Pass that expected hash explicitly. The script refuses a different runtime ID/source commit and verifies the selected EXE/tokenizer against the trusted manifest.
3. Reuse the existing model files after transfer; the script rechecks exact bytes and SHA256. `-DownloadMissing` optionally downloads only the three predetermined pinned public URLs. No model terms/login interaction is automated.
4. Run one model at a time. A zero-token native load probe runs first. If that fails, stop that model and retain native exit/stdout/stderr/peak metrics; do not silently change flags or rerun until green.
5. When load succeeds, count actual tokens with the pinned tokenizer, then run the same two six-line screens, using the original full48 fixture, production prompt/schema/parser and fixed uniform sampling. No background trimming, alternate prompt or random retry is automatic.
6. Structural success still requires manual semantic reading. Do not run the unseen holdout/full song until the fixed screen is genuinely free of blocking errors. No holdout is included in this Windows handoff.

## Prerequisites

- Windows x64; existing .NET10 SDK matching the repository; PowerShell7
- Repository source tree containing the current Core project and production native process-policy sources
- Trusted artifact directory containing runtime-manifest.json, llama-completion.exe, llama-tokenize.exe, and optional llama-completion-avx2.exe
- Commit expected by the manifest: 7fe450e19305b828c199d602c23a8337aaa1f03b; runtime ID llama-cpp-v0.5.0-cpu-win-x64
- Prefer a machine with at least4GiB free RAM for the3GiB job plus host overhead. This is diagnostic headroom guidance, NOT a change to the job cap or a minimum-product-hardware claim
- Two default models together use approximately2.83GB disk; add1.83GB for optional Q8. Hashing/output/build storage is additional

## Invocation (PowerShell7, from the repo)

Replace the placeholder manifest hash only with a value obtained from the trusted artifact, not an arbitrary EXE directory.

```powershell
# Optional parser-only syntax check; it does not run any model or script body.
$tokens = $null; $errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
  (Resolve-Path 'scripts/ai-model-qa/Run-WindowsModelQa.ps1'),
  [ref]$tokens, [ref]$errors) | Out-Null
$errors

& scripts/ai-model-qa/Run-WindowsModelQa.ps1 `
  -RuntimeDirectory 'C:\QA\trusted-ai-runtime\win-x64' `
  -ExpectedRuntimeManifestSha256 '<trusted 64-hex SHA256>' `
  -ModelDirectory 'C:\QA\models' `
  -ModelIds @('qwen3-17-q4','granite33-2-q4') `
  -Variant Baseline `
  -MemoryMiB 3072
```

Useful explicit alternatives:
- `-LoadOnly`: diagnose loading first without tokenization/generation screens
- `-DownloadMissing`: download fixed candidate URLs if verified local files are unavailable; preserves partial files and verifies before renaming
- `-ModelIds @('qwen3-17-q8')`: test existing author-official higher-precision Qwen3 artifact separately
- `-Variant Avx2`: deliberately separate CPU-variant run, only on a verified AVX2/FMA/F16C-capable host. Do not relabel an AVX2 success as baseline success
- `-MemoryMiB 1536`: separate compact-budget experiment. It is expected to be tight or insufficient for these models; never silently replace a3GiB result with this or vice versa
- `-TestCancellation`: after screens, run a predeclared2s cancellation diagnostic and inspect native reason/process observations
- `-OutputDirectory`: must be a new directory; previous evidence is never overwritten

## Native limits and trust boundary

WindowsModelQa.csproj links the exact existing LocalInferenceProcess.cs, WindowsInferenceProcess.cs and ReparseSafePathPolicy.cs, plus the production Core project. The native process is launched directly inside the same mandatory Windows Job Object: active-process limit1, process/job memory limit, kill-on-job-close, suspended start and restricted handle inheritance. Failure to enforce the job has no permissive fallback.

The QA probe duplicates the production controlled CLI arguments only to add fixed sampling/telemetry. It does NOT use the Linux Python wrapper; that wrapper would conflict with the single-process Windows job and is not a valid Windows substitute.

Sampling: seed42, temperature0.1, top_k20, top_p0.8, min_p0.05, repeat_penalty1, frequency/presence penalties0. Four CPU threads, zero GPU layers, context4096, generation2048, non-mmap loading, offline, Jinja embedded template, reasoning off. Ambient LLAMA_/GGML_ variables are removed. Native output is bounded. Generation/load probes cap at60s; tokenizer at20s. The180s whole-song budget is NOT claimed tested by this diagnostic sequence, which deliberately includes additional loading/tokenization work.

The load probe uses `-n 0`; it establishes only that this configuration initializes/exits, not that full generation or semantics work. Actual native output and per-step exit code are authoritative. Native signed exit codes and hex forms are preserved; do not automatically label every nonzero exit an OOM.

## What can be reused vs. what cannot

- Reuse: original48-line fixture, selected source IDs, strict parser, production prompt/schema, tokenizer preflight, native Windows process/job implementation, pinned runtime artifact
- Do not directly reuse: Linux fixed-seed Python process wrapper (Windows single-process job would prohibit its native child); Linux RSS outcomes as Windows committed-memory proofs
- Existing AiLyricsNativeRuntimeSmokeTests cover only the two hard-coded Hy catalog descriptors. They cannot test arbitrary candidate hashes without editing production/test code, so this isolated manifest-driven QA harness is used instead
- Source/DLL differences are recorded; the harness exit0 is only technical completion/structural success, never semantic or release approval

## Pinned model inputs

All exact URLs, bytes, revisions and SHA256 values are in candidates.json.

| ID | Provenance | File bytes | SHA256 |
|---|---|---:|---|
| qwen3-17-q4 | ggml-org maintainer conversion, Apache2.0 | 1,282,439,264 | d2387ca2dbfee2ffabce7120d3770dadca0b293052bc2f0e138fdc940d9bc7b5 |
| qwen3-17-q8 | author-official Qwen, Apache2.0 | 1,834,426,016 | 061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a |
| granite33-2-q4 | author-official IBM, Apache2.0 | 1,545,303,328 | ac71e9e32c0bea919b409c5918f69ca74339854b0319c5065e4e9fb6d95c4852 |

File bytes alone do not predict committed memory; weights, KV, graph/work buffers and transient load allocations must be measured. These are3GiB-standard candidates. No claim that any fits1.5GiB is made.

## Expected evidence and classification

Each run creates a new results directory with environment/source hashes/runtime manifest/fixture snapshot; each model gets exact args, raw stdout/stderr, exceptions, measured working-set/private-byte peaks, signed+hex native exit codes, actual token count, strict-validity result and observation of only the task’s native PIDs afterward. No unrelated service/process is stopped.

Classify separately:
- trust/hash/schema setup failure
- native loading/OS error or timeout (no semantic conclusion)
- tokenizer/budget failure (no unrecorded trimming)
- structural failure
- structural success but semantic failure
- fixed-screen semantic success, eligible for the next independent holdout stage only

Review concrete objects, who does what to whom, denied promises, the count of two knocks/seven seeds, group-limited ownership, and requested target language. Read every line; do not infer quality from JSON or exit codes.

## PR-only CI integration

The Release workflow uploads only its own pinned-source runtime executables and manifest, then starts an independent Windows 2025 candidate diagnostic job for pull requests. The expected manifest SHA is passed as a same-run job output. Model weights are downloaded from immutable public URLs and size/hash checked; no secrets, accounts, model uploads, workflow dispatch, or catalog modifications are needed.

Qwen3 Q4 and Granite Q4 run sequentially once. Each native load/generation invocation keeps the 3 GiB / 60 s limits. Q8 is available only as an explicit manual selection. Each model step continues on error so the other candidate can still produce evidence. A final step fails the job if either technical step failed or was incomplete. A technically successful job means only loading/tokenization/strict JSON validation succeeded; semantic status remains PENDING HUMAN REVIEW and releaseApproval is false. The job is not a dependency of signing/publication.

Runtime upload failure does not block unrelated packaging; the diagnostic job fails on a missing/untrusted runtime. If the runtime build never produced a trusted manifest, the diagnostic job is skipped and no candidate result exists. Artifact evidence includes raw synthetic input/output, bounded stderr, arguments, hashes, memory and native exit status; it contains no private lyrics or credentials. Model weights and bin/obj are excluded from diagnostic artifacts.

The source fixture is original synthetic text, not lyrics obtained from a user or provider. The frozen holdout is intentionally excluded. This harness project has its own committed packages.lock.json and uses locked restore.

### Deadline and teardown accounting

Native inference polling, OS exit wait, and output-drain wait share the invocation cancellation deadline (60 s for load/generation, 20 s tokenizer). Deadline cancellation breaks observation even if Kill fails. Teardown uses the production CompleteAsync ownership task and WaitForCleanupAsync's separate 10 s shutdown observation budget, with no second Dispose wait. Evidence distinguishes InferenceBudgetSeconds, total Seconds, CleanupSeconds, CleanupCompleted and CleanupError. Thus a deadline result may include up to 10 s additional shutdown observation; this is never reported as a successful sub-60 s inference.

Unconfirmed cleanup stops remaining screens and writes a guard in the shared model directory so the next candidate step fails closed before native launch. Retain that guard/evidence for diagnosis; do not automatically retry or delete it to obtain a green run. Deadline/cleanup failure is persisted in each native JSON/result rather than being treated as a semantic verdict.
