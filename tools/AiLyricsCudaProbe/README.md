# Minimal shipping-chain CUDA probe

Only run with explicit permission for real inference. One artificial English line,
one installed 1.8B model, one request. No download, preference write, UI startup or
user-cache deletion. CPU fallback is recorded but fails CUDA acceptance. Use a fresh
evidence directory each time; do not repeat successful inference as routine testing.

The project links the actual App `AiLyricsService.cs` and references the production
Infrastructure/Core projects. It constructs the same resolver, backend, coordinator,
automatic resident runner and runtime options used in App DI. Its only isolation is
a fresh lyrics cache and evidence folder. Pass the running installed App's extracted
resource assembly; its pinned model/component checks remain active. This tests
Beta12 managed source with the installed approved native assets, not a repackaged
Beta12 executable or a separate CUDA sample.

Build with `dotnet build tools/AiLyricsCudaProbe/AiLyricsCudaProbe.csproj -c Debug -p:Platform=x64`.
Run the resulting DLL with three arguments: the installed App's extracted
`DropSpace.dll`, its data root, and a new evidence directory. For a running App,
the resource assembly path can be read from its `DropSpace.dll` process module.

Read-only WDDM counters are matched to the runner-owned PID. Acceptance requires a
valid CUDA translation without fallback/cache, GPU engine activity, and dedicated
GPU memory at least the model size. Windows may label CUDA work on its 3D engine;
the physical adapter, CUDA executable and loaded libraries identify this workload.
Failure to collect counters is incomplete evidence, not a successful validation.

The Job observer attaches a diagnostic completion port without changing limits.
Messages 9/10 expose rejected process/job commit allocations; the sampled peak
commit and configured cap are distinct from dedicated VRAM. Diagnostics inspect
private ownership fields read-only and do not bypass production launch policy.

The Beta12 investigation used three one-line runs: reproduction, the same failure
with Job memory evidence, and verification of the bounded host-memory fix. Raw local
reports remain under `.codex/cuda-probe-01` through `03`; a compact, path-free evidence
record is in `docs/dev/evidence/beta12-local-cuda/verification.json`.
