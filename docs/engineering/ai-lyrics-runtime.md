# Local AI lyrics runtime: provenance and process limits

The shipping Hy-MT2 configuration and its CPU, Vulkan and optional CUDA
workers are described in [the architecture guide](../../ARCHITECTURE.md).

## Original CPU completion runtime

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

## Package verification

The Windows runtime build verifies the declared source, expected payload files,
SHA-256 hashes, file sizes and x64 PE headers. It starts the baseline executable
with `--version`. The release workflow also inspects the runtime embedded in
the portable and MSIX packages. [Runtime verification](../../scripts/Test-AiLyricsRuntime.ps1)

For a functional check, use the [core test plan](../../TEST_PLAN.md). The former
native inference and process-isolation fixtures are archived in Git revision `20c524a3`.

References: [Windows process attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute),
[extended job limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information),
[basic job flags](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information).
