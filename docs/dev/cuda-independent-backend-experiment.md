# Independent NVIDIA CUDA experiment

Baseline fetched 2026-10-05: `fix/beta10-chinese-lyrics-regression` at
`64457419ae6fc8af15ab5542a402b7b871cbf9be`. Work branch:
`experiment/nvidia-cuda-backend`. No release, default switch, paid GPU allocation,
or execution on the user's PC is part of this change.

## Implemented boundary

Shipping DI still constructs the existing Vulkan/CPU runner. AMD continues using
Vulkan. The default model remains Hy-MT2 1.8B Q8; 7B Q8 remains optional.
`CudaLyricsRuntimePackage`, `CudaPlainHyLyricsBackend`, and its resolver are
unregistered components for explicitly injected experiments only. They reuse
installed, catalog-hash-verified GGUFs and the actual plaintext coordinator.
The resolver and backend bypass all AI resources when provider translations
already match. Progress fences, complete-song-only caching, current-track checks,
and cancellation/cleanup continue through the existing coordinator and runner.

The CUDA component ID is
`llama-cpp-v0.5.0-cuda12-win-x64-experiment-v1`; it has its own manifest resource
namespace and cache directory. Manifest-bound files are exactly:

- `plain-lyrics-worker-cuda.exe`
- `cublas64_12.dll`
- `cublasLt64_12.dll`

Windows [pinned ggml CUDA CMake](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/ggml/src/ggml-cuda/CMakeLists.txt)
links cuBLAS dynamically even with `GGML_STATIC=ON`. Both DLLs are verified before
returning a launch path; the runner additionally rehashes all three component files
under retained read leases before launch and transfers those leases to the native
session until confirmed process exit and reader settlement; metadata, individual sizes/hashes, contained paths and
partial cleanup are checked. Production accepts embedded resources only.
A sidecar manifest cannot make a downloaded variant trusted. CPU fallback keeps
its separate existing trusted package. Cache identity binds the CUDA manifest,
CPU fallback manifest, model hash and frozen plaintext protocol.

The runner has a fixed backend per instance. GPU failure state is scoped to that
instance/backend and verified model; a CUDA failure never disables or attempts
Vulkan. Operational model/role switches preserve failures. Explicit GPU off/on
resets them. One recoverable CUDA attempt can fall back once to CPU, after confirmed
child exit/readers/Job cleanup and a fresh CPU RAM/commit admission check.
Integrity, protocol, component identity and cancellation failures cannot become
an automatic CPU retry. A cleanup failure retains the gate and ownership.

The independent CMake build generates a backend-only overlay from maintained
`tools/plain-lyrics-helper/main.cpp`; it does not edit that file or carry a fork
of its decode/KV loop. The adapter fails on changed backend anchors. Tests assert
that the complete request loop and sampler argument block remain byte identical.
This lets the separate KV task's loop updates flow into the CUDA experiment,
while changed backend scaffolding requires review. Generated worker identity and
all build inputs are recorded in the producer manifest.

CUDA selection asks pinned ggml's `CUDA` registry, maps its `CUDA<N>` name to the
CUDA ordinal, requires a discrete GPU, measures current `cudaMemGetInfo`, and
uses the existing model-specific allowance plus `max(1 GiB, total/5)` reserve.
Unknown measurements, integrated devices, less than 4 GiB total, inconsistent
free/total or insufficient free VRAM decline admission. The 1.8B/7B exact model
sizes and current conservative GPU/host allowances are unchanged. These are
policy thresholds, not measured peaks or a hardware qualification.

## Reuse and build route

The cloud checkout initially contained provenance records, not executable/model
payloads or an engine source tree. Read-only GitHub artifact inventory found the
unexpired `ai-selection-runtime-37235696457-1` artifact (ID `11316610580`,
30,701,589 bytes), with no CUDA-named artifact in that returned inventory. Its
presence is not a CUDA qualification; its bytes were not downloaded or rebuilt.
No GGUF was downloaded. The pinned official engine was fetched once for header
compatibility checks into `/workspace/cuda-evidence/llama-src`; reuse this clean
source tree in subsequent experiments.

On an already authorized **Windows x64 cloud build host** with existing MSVC,
CMake >=3.24, Python and CUDA Toolkit 12.3–12.9, use an x64 developer PowerShell:

```powershell
./scripts/Build-CudaLyricsExperiment.ps1 `
  -VerifiedSourceDirectory C:/verified/llama.cpp `
  -CudaToolkitDirectory 'C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v12.9' `
  -CudaArchitectures '86;89;90'
```

Select architectures for the actual target matrix; this example is not universal.
The producer verifies the immutable clean engine, uses fresh isolated artifact
paths, builds only the CUDA worker, binds toolkit DLLs by hash, gathers notices,
checks PE imports for every component. Native `--version` identity smoke requires
the explicit `-ValidateStartup` switch, which stays off for compile-only work.
It requires no GPU for compilation, but startup dependencies must be available.
It does not install toolkits/drivers, build CPU/Vulkan again, overwrite shipping
payloads, dispatch cloud jobs, or publish. Full Windows linking/startup of this
route remains unverified in this Linux environment. Any unexpected import fails
review rather than silently extending the payload. The experiment executable and
manifest are not automatically embedded in the app.

## Current no-test instruction and integration review

After the user prohibited testing, no unit/regression/benchmark/native startup
or GPU checks were rerun and no test CI was dispatched. Existing test sources
and historical evidence remain intact. The resumed change adds retained component
leases and makes producer startup smoke explicitly opt-in. Only
`dotnet build src/DropSpace.Infrastructure/DropSpace.Infrastructure.csproj -c Release --no-restore`
was run for the final managed change: zero warnings/errors. This does not validate
lease behavior, Windows DLL loading, startup or cancellation. Keep `-ValidateStartup`
and all commands in the following experiment section unexecuted under the current
instruction.

The separate KV/timing commits `55f8ec4e5f5b95035d112c5d4bdda5f88dd1b011`
and `14e0550c5b474d4cd93610265b29a3232ad51eb9` were reviewed by reading their
source diff. They preserve every CUDA overlay backend anchor and the sampler
argument block. Their optional `--timings`/`--experimental-prefix-kv` parsing and
compatible response fields remain in the maintained loop and would flow through
the backend overlay when the main integration includes those commits. The host
runner passes neither flag by default. Required protocol/id/complete/text parsing
allows extra response fields. This branch does not edit or cherry-pick `main.cpp`;
no KV-source overlay generation, compilation, or execution was performed here.
The primary task owns final integration.

## Real comparison route (deferred; currently prohibited)

`CudaNativeComparisonTests` is an explicit Windows native experiment with six
rows: CPU/Vulkan/CUDA on each installed pinned 1.8B/7B Q8 model. Configure only
existing reviewed artifacts and GGUFs on an authorized cloud host:

```powershell
$env:DROPSPACE_CUDA_COMPARISON_RUNTIME = 'C:/reviewed/shipping-runtime'
$env:DROPSPACE_CUDA_COMPONENT = 'C:/reviewed/cuda-payload'
$env:DROPSPACE_CUDA_MODEL = 'C:/models/Hy-MT2-1.8B-Q8_0.gguf'
$env:DROPSPACE_CUDA_MODEL_7B = 'C:/models/Hy-MT2-7B-Q8_0.gguf'
dotnet test tests/DropSpace.Infrastructure.Tests -c Release `
  --filter FullyQualifiedName~CudaNativeComparisonTests `
  --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=cuda-native.trx'
```

This diagnostic resource loader reads the explicitly supplied reviewed payload;
shipping component materialization uses embedded resources. The native test
retains a model file lease after SHA/size verification. It uses the same actual
coordinator, prompt, sampler, engine commit, source lines and target. Different
owned track IDs avoid complete-song cache hits for the warm pass. It measures
first **useful validated translation progress**, complete-song latency, same-PID
resident reuse, process peak working set, sampled private bytes, adapter admission
snapshot, and cancellation-to-confirmed-cleanup latency after actual pipe dispatch.
GPU rows fail if they succeed through CPU fallback. All Jobs must be drained and
the global gate released; no partial file may remain.

“Cold” means a fresh process, with uncontrolled OS filesystem cache. Two rows are
an initial structural comparison, not a statistical speed or quality claim. Repeat
and alternate backend order on the same GPU/driver after the first native gate;
review output semantics independently and collect fixture-scale evidence before
considering any default change. The test explicitly reports peak VRAM as unknown:
admission free/total is not peak device usage. Capture process/device VRAM and
GPU timing with a separately retained nvidia-smi/Nsight trace. No GPU request
latency, quality, peak VRAM or cleanup timing is reported by this CPU-only run.

## PDL and pinned engine evidence

[PR #22522](https://github.com/ggml-org/llama.cpp/pull/22522) merged May 20, 2026.
The current pin `7fe450e19305b828c199d602c23a8337aaa1f03b` already has PDL launch,
barrier and runtime control in
[`ggml-cuda/common.cuh`](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/ggml/src/ggml-cuda/common.cuh).
It guards MSVC host support with CUDA >=12.3 and device primitives with compute
>=9.0. The launch path also checks the loaded kernel's PTX version >=90, avoiding
pre-Hopper PTX forward-JIT assumptions. Ada 8.9 does not qualify just because it
is NVIDIA. No PDL code was transplanted and no Hy-MT2 benefit is assumed.
The runner scrubs inherited `GGML_*` variables, so a parent-shell
`GGML_CUDA_PDL=0` is **not** an on/off test. A separately controlled diagnostic
option would be needed for that future experiment; it is not implemented here.

NVIDIA skill finder was read and the live
[catalog](https://raw.githubusercontent.com/NVIDIA/skills/main/skills.sh.json)
checked after its CLI failed due an unwritable npm cache. No strong catalog
match for this Windows llama.cpp resident-worker integration was identified;
no skill installation or paid infrastructure setup was attempted.

## HIP preparation only

AMD remains on Vulkan; the runner rejects `hip` as a selected experiment backend.
The following planning matrix reflects the [AMD Windows HIP SDK 7.2 requirements,
updated July 31, 2026](https://rocm.docs.amd.com/projects/install-on-windows/en/latest/reference/system-requirements.html),
queried October 5. It is vendor support, not DropSpace qualification:

| Candidate group | Explicit target | Windows HIP SDK status / route |
| --- | --- | --- |
| RX 7900 XTX/XT, PRO W7900/W7800 | gfx1100 | Listed supported; first discrete HIP comparison candidate |
| RX 7800 XT/7700 XT, PRO W7700 | gfx1101 | Listed supported; separate target build |
| RX 7600/7600 XT/7650 GRE | gfx1102 | Listed supported; separate memory qualification |
| RX 9070/9070 XT/9070 GRE, AI PRO R9700 | gfx1201 | Listed supported; verify pinned hipBLAS/rocBLAS toolchain compatibility |
| RX 9060/9060 XT | gfx1200 | Listed supported; separate target build |
| Ryzen AI Max series / Ryzen AI 300 | gfx1151 / gfx1150 | Listed supported APU targets; shared RAM requires separately measured physical/commit budgets; no current HIP admission implementation |
| RX 6000 / PRO W6800/W6600 | gfx103x | Listed unsupported by this SDK; retain Vulkan, no override-based qualification |
| Intel or unlisted GPU | none | Not an AMD HIP route; retain current supported backend policy |

That SDK page lists Windows 11 x64 22H2; it does not qualify DropSpace's entire
minimum-Windows-version range. Linux ROCm support must be checked independently.
On an existing supported Windows cloud host, the pinned upstream
[HIP build instructions](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/docs/build.md#hip)
provide a runnable **standalone toolchain probe**, independent of app packaging:

```powershell
$env:PATH = "$env:HIP_PATH/bin;$env:PATH"
cmake -S C:/verified/llama.cpp -B C:/experiments/hip-gfx1100 -G Ninja `
  -DGGML_HIP=ON -DGGML_CUDA=OFF -DGGML_VULKAN=OFF -DGPU_TARGETS=gfx1100 `
  -DCMAKE_C_COMPILER=clang -DCMAKE_CXX_COMPILER=clang++ `
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DGGML_STATIC=OFF `
  -DLLAMA_BUILD_TOOLS=ON -DLLAMA_BUILD_SERVER=OFF -DLLAMA_OPENSSL=OFF
cmake --build C:/experiments/hip-gfx1100 --target llama-completion --parallel 4
C:/experiments/hip-gfx1100/bin/llama-completion.exe --list-devices
```

Pinned ggml HIP explicitly disallows static linking and needs HIP/hipBLAS/rocBLAS
libraries. Before a resident HIP model experiment, create a separate component
identity/handshake/cache and a reviewed manifest for **all** DLLs and rocBLAS
kernel/database resources; implement HIP-specific live-memory/device mapping,
reuse the maintained worker loop, and run the same ownership/quality/latency
matrix. The standalone probe is not that integration and was not executed here.
Do not reuse CUDA manifests, classify HIP failure as Vulkan failure, or use
unsupported Windows `HSA_OVERRIDE_GFX_VERSION` to claim qualification.

## Current checks and blockers

See [machine-readable evidence](evidence/cuda-experiment/checks.json),
[compile-only build output](evidence/cuda-experiment/compile-only-build.txt),
[historical managed TRX](evidence/cuda-experiment/cuda-managed.trx) and
[historical header compile evidence](evidence/cuda-experiment/header-compile.json).
`checks.json` was absent in checkpoint `a1f88c0` and was subsequently assembled
from saved evidence and the authorized compilation, without rerunning tests.
Historical checks below precede the final lease/build-script changes and do not
qualify their final behavior.

- Locked .NET restore succeeded. Relevant managed/real-child protocol tests
  passed; six actual-native rows are skipped because this is Linux without a GPU
  or installed GGUFs. Synthetic child timings are not inference measurements.
- Three Python/native contract checks pass: unchanged sampler/request loop,
  fail-closed overlay anchors and compiled model-specific GPU admission boundaries.
- Entire generated CUDA worker passed `g++ -std=c++17 -fsyntax-only` against the
  verified pinned engine and real NVIDIA CUDA 12.9 headers. This validates host
  API compatibility only, not CUDA kernel compilation/linking or Windows ABI.
- CMake passed clean pinned-source checks, then failed at real toolkit discovery:
  `Could not find nvcc, please set CUDAToolkit_ROOT.` CUDA compiler-header wheels
  include headers/ptxas, not nvcc; no full toolkit/driver installation was attempted.
- Broader translation tests exposed
  `SlowerHigherScoreTranslationWinsOverFirstReportedTranslation` returning
  QqMusic instead of expected Kugou. It reproduced on a clean archive of baseline
  `64457419`, including an isolated test invocation. Provider/ranking files were
  not modified by this CUDA task; this is a baseline issue for its responsible task.

Remaining blockers: Windows CUDA compile/link/PE startup and DLL import review;
real authorized NVIDIA GPU/driver with installed 1.8B/7B GGUFs; GPU cold/warm
translation/semantic review, peak VRAM and cancellation resource return. HIP
resident integration and real HIP execution are intentionally still preparation.
No speedup or new supported hardware claim is justified by these checks.
