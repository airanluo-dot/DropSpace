# Local AI lyrics runtime: provenance and process limits

## Shipping payload

`Build-AiLyricsRuntime.ps1` fetches only the official `ggml-org/llama.cpp` source at
`7fe450e19305b828c199d602c23a8337aaa1f03b` (v0.5.0), verifies the commit and Git objects,
and builds the `llama-completion` target on Windows x64. It does not use a mutable latest
release or download an arbitrary executable. The source and CMake caches are recreated for
each build. The application embeds these files from `artifacts/ai-runtime/win-x64`:

- `llama-completion.exe`: baseline x64 CPU build
- `llama-completion-avx2.exe`: AVX2/FMA/F16C CPU build
- `runtime-manifest.json`: source identity and SHA256/size of each exact built executable
- `LICENSE-llama.cpp`: all licenses aggregated by the upstream build, including vendored code

The runtime uses static llama/ggml libraries and the static MSVC runtime. Dynamic backend
loading, GPU/BLAS backends, OpenMP, OpenSSL, the server/unified application, and subprocess
support are disabled. Only the completion target is built. GPU drivers and OpenMP runtime
DLLs are unnecessary. No OS permissions, firewall rules, network settings, or global policies
are changed.

`AiLyricsRuntimePackage` reads its trust manifest from the installed app assembly, never
from the extracted directory or user settings. It selects AVX2 only when runtime CPU/OS
support checks pass for AVX2, FMA, and F16C; other machines use the baseline. It extracts to
a source-version/hash directory under the app's runtime cache, verifies SHA256 and length
on every acquisition, repairs corrupted bytes from the embedded resource, and rejects
reparse-point paths. There is no user-supplied executable setting. Runtime binary hashes
are build-specific because the Windows toolchain can change; they are bound to the app's
embedded bytes rather than guessed in source control.

## Execution and privacy

`LlamaCompletionRunner` serializes inference across runner instances in the app process.
It always sets CPU generation/batch threads to four, GPU layers to zero, a 4096-token
context, 2048-token output, 60-second per-inference deadline, and 65,536-character stdout
ceiling. Ambient `LLAMA_*` and `GGML_*` options are removed. The local model and prompt
are passed as separate arguments, with `--offline`, no HTTP listener, and no tools or
subprocess feature. Original lyrics, model output, and stderr are not logged.

The prompt is a unique file in app-controlled staging, closed before native code opens it.
This is important on Windows: a C# `DeleteOnClose` handle conflicts with the native
`std::ifstream` sharing mode. Normal completion and failure/cancellation paths remove the
file in `finally` (best effort if the filesystem refuses deletion). A machine/app crash can
still leave a staging file; this is not a guarantee of secure deletion.

On Windows, `WindowsInferenceProcess` creates a non-inherited Job Object with:

- a 3 GiB per-process and aggregate committed-memory limit
- an active-process limit of one, with no breakaway allowance
- `KILL_ON_JOB_CLOSE`, so a parent crash/exit closes its sole job handle and kills the child

The job is attached atomically through `PROC_THREAD_ATTRIBUTE_JOB_LIST` during
`CreateProcessW`, before any child code runs. Explicit handle inheritance allows only the
three redirected standard streams. A suspended initial thread permits stream/process
ownership to be established before resume. Any job/attribute/process setup error fails
closed; there is no unrestricted Windows fallback. `--load-mode none` places model allocation
under the committed-memory budget. The working-set watchdog additionally checks the
3 GiB RSS threshold. Cancellation and timeout kill the process, and owner disposal closes
the job as a final lifetime guard.

These are resource and lifetime controls, **not a security sandbox**. They do not deny
filesystem or socket APIs, reduce the user token, or establish an OS network boundary.
Offline behavior comes from the fixed trusted program, stripped build features, and
controlled CLI arguments. Non-Windows execution is development-only and has the
watchdog/time/output limits but does not claim Windows job enforcement.

## Verification gates and limits

- `Test-AiLyricsRuntime.ps1` checks provenance fields, each payload SHA256/length, x64 PE
  headers, the exact file allowlist, and native baseline startup/version
- `AiLyricsRuntimePackageTests` covers extraction, corruption repair, hash mismatch,
  cancellation, wrong source/missing payload, and both CPU variant selections
- `WindowsInferenceProcessTests` covers native closed-prompt reading/stdout/stderr,
  denial of a 4 GiB committed allocation under the 3 GiB limit, child cleanup, and argument
  quoting. Native checks run only on Windows; the owner-disposal test is not a parent-crash test
- `AiLyricsNativeRuntimeSmokeTests` is an opt-in native release gate. Set
  `DROPSPACE_AI_SMOKE_MODEL` to the hash-pinned Standard GGUF and
  `DROPSPACE_AI_SMOKE_RUNTIME` to the built payload folder. It validates the model hash,
  extracts the selected runtime, invokes the production runner for two original EN→ZH
  lines, checks strict output IDs/Chinese text/original timestamps, and verifies
  cancellation and prompt cleanup. It never downloads a model itself

The release gate command is:

```powershell
dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj `
  -c Release --no-build --no-restore `
  --filter "FullyQualifiedName~AiLyricsNativeRuntimeSmokeTests" `
  --logger "trx;LogFileName=ai-native-runtime.trx"
```

Native Windows build, Job Object tests, model execution, and portable/MSIX embedding must
pass in Windows CI before release. Linux build/test results do not establish those facts.
Latency must be measured on target CPUs, including the baseline fallback; a four-thread
cap does not guarantee every song meets the deadline. A failed/timed-out inference must
leave original lyrics intact.

References: [Windows process attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute),
[extended job limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information),
[basic job flags](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information).
