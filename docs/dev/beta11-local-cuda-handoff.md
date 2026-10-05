# Beta11 local CUDA13 integration handoff

Checkout `E:\Dev\DropSpace-beta11-cuda`, branch `local/beta11-nvidia-cuda`,
baseline `6788585e16040b4903c6cc534c0780c61079c07e`. User checkouts and models untouched.
The main task is the sole main-branch integrator and release publisher.
GPU routing checkpoint65be1cc, unified DLC checkpoint9784aab, CUDA13 upgrade806f3e3
and driver handle fix16b9b01 were pushed.
DLC commits2129381/8cda4d5 are included as b3ffb8e/3915f73; avoid applying patches twice.

## Final implementation

GPU off -> CPU. GPU on offers persisted Automatic/Vulkan/CUDA. Automatic chooses CUDA
only with a compatible NVIDIA driver/device and trusted installed components, otherwise
Vulkan; existing bounded GPU->CPU fallback is retained. Last completed backend is obtained
from the protocol-validated runner response, never inferred from the preference.
1.8BQ8 default/7B option, frozen prompt and KV/timing defaults are preserved.
`tools/plain-lyrics-helper/main.cpp` was not edited; CUDA uses a generated overlay.

First-level settings DLC follows Widgets and precedes System Activities. The single
`DlcManagerService` owns transient download/cancel/delete/inspection states for models
and CUDA. `CudaRuntimeDlcProvider` is stateless and only advertises a valid descriptor.
It reports verified installation/actual owned bytes and reuses the runtime downloader.
Music projects those snapshots and opens the same DLC page. Downloads require consent
and do not enable AI/GPU or replace a model. Delete cancels/drains native inference under
the maintenance fence before removing owned variants/partials; never deletes lyric/model
files. Removed duplicate `CudaComponentService`.

Base App embeds only inner manifest and exact-release outer descriptor. DLLs/EXE are
external DLC, with fixed official release URL, archive hash/bytes, six entry ZIP, binary and
notice hashes, engine/worker source identity, App tag/sourceSHA binding. Verified binary
leases remain held through owned process exit. Release requires real metadata, no placeholder.

## Toolchain selected by the user's newest-CUDA instruction

Final route: CUDA13.4.1 with existing MSVC19.51.36257/VS2026; NVIDIA explicitly lists
MSVC195x/VS2026 in its support table. No unsupported compiler/STL flags are used.
The prior incomplete CUDA12.9/MSVC14.44 build was stopped, not packaged or claimed usable.
No system toolchain/driver/security/network settings were changed; all downloaded components
were extracted on E: and environment changes apply only to the build process.

Official SHA256 and exact size verification passed for all6archives (523,566,280 bytes):
NVCC13.4.59, CRT13.4.59, cudart13.4.49, CCCL13.3.4.2.1, NVVM13.4.59, cuBLAS13.7.0.27.
Official records: `docs/dev/evidence/beta11-local-cuda/cuda13-official-components.json`.
Sources:
https://developer.download.nvidia.com/compute/cuda/redist/redistrib_13.4.1.json
https://docs.nvidia.com/cuda/cuda-installation-guide-microsoft-windows/index.html

CUDA13 runtime ID `llama-cpp-v0.5.0-cuda13-win-x64-v1`; bundled dynamic DLLs are
`cublas64_13.dll` and `cublasLt64_13.dll`; cudart is static. The production probe requires
UMD>=13.4/capability>=7.5, conservatively avoiding older-driver PTX restrictions. Compiled
targets75,86,120(upstream120a). Older unsupported devices retain Vulkan/CPU.
Real device: RTX5080Laptop, driver616.56, UMD13.4,16303MiB. Read-only device evidence
was recorded earlier; production backend has not been run.

Compatibility cause: installed MSVC19.51 is rejected by CUDA12.9 host_config.h:168
(_MSC_VER>=1950); installed MSVC yvals_core.h:911 requires CUDA13.2+. CUDA13.4 removes
that mismatched combination using a supported complete toolchain, with guards enabled.

## Compilation and size evidence

Windows App Debug x64 after DLC and CUDA13 integration passed: zero warnings/errors.
Evidence `docs/dev/evidence/beta11-local-cuda/cuda13-windows-compile.txt`.
CUDA13.4/MSVC19.51 compiler identification/ABI/configuration and all275 native build
steps passed. Worker link and PE dependency inspection completed with exit code0.
Evidence `docs/dev/evidence/beta11-local-cuda/cuda13-build-completed.txt` and import reports.
This establishes compilation/packaging; no runtime/model invocation is claimed.

Measured cuBLAS DLLs54,942,320 +492,752,496 =547,694,816 bytes (522.32MiB), about213MiB
below CUDA12.9's two DLLs. PE inspection: cuBLAS depends only on cuBLASLt/KERNEL32;
cuBLASLt only KERNEL32. Worker134,269,440 bytes (128.05MiB). Runtime binaries total681,964,256 bytes
(650.37MiB). The separate ZIP is540,873,572 bytes (515.82MiB).
Development headers/compiler archives do not ship. Base App receives only tiny metadata.

The user explicitly canceled real model calls as an extra release gate. No model inference,
App launch, startup smoke, unit/full regression, benchmark or test CI was run.
Existing1908528192-byte1.8B and7981928896-byte7B model files were not downloaded/copied.

## Remaining publication integration

Native payload and notices:
`E:\Dev\DropSpace-beta11-cuda\artifacts\cuda-experiment-build-2c9fcb0fa29545e4a8471adffe6336aa`.
Ready ZIP and tiny producer records:
`E:\Dev\DropSpace-beta11-cuda\artifacts\cuda13-producer-806f3e3`.
Archive `DropSpace-CUDA-win-x64-v0.3.1-beta.11.zip` contains exactly three binaries,
inner manifest and two complete notice files. Packaging rechecked each extracted entry's
SHA/size without executing a program. Full fingerprints are in
`docs/dev/evidence/beta11-local-cuda/cuda13-producer-report.json`.
The base App inner manifest is1815bytes; final outer descriptor is still to be generated.

Full native component source SHA:806f3e3e40c11a6e7d3d50648a9de8708b16b4ac.
Subsequent driver handle fix:16b9b0184949e0be41dc71fa39b2ffb055d23033 (no native input change).
The sole parent integrator owns final App version/sourceSHA, base package and publication.
No outer download descriptor was generated with a guessed App SHA.

There is no circular source binding: compiled worker, DLLs, inner manifest and ZIP contents
do not include AppSHA. Only the external outer descriptor contains final App tag/sourceSHA.
Commit final App code/version first; then generate metadata under ignored `artifacts`,
embed it and compile the App with that exact source SHA. Do not commit that generated
outer JSON, which would change its own source binding. Native inputs need no rebuild.

To generate the final outer descriptor with the existing packager (archive bytes are
deterministic and unchanged by AppSHA):

    python scripts/package-cuda-runtime.py --payload <native-build>/payload --license-directory <native-build> --app-tag v0.3.1-beta.11 --app-commit <final-main-App-SHA> --component-commit 806f3e3e40c11a6e7d3d50648a9de8708b16b4ac --output <fresh-final-package-directory>

Stage only its two JSON files at `artifacts/cuda-runtime/win-x64`; the ZIP stays a separate
release asset. Do not point runtime download metadata to a missing/publication-placeholder
asset. App assembly InformationalVersion must match tag plus the exact sourceSHA.
The base Release build still needs the parent's reviewed CPU/Vulkan artifact11316610580,
not retrieved here. Final source binding, base Release packaging and asset upload remain
with the parent task. No release or installed daily App was changed locally.
Actual CUDA invocation is unverified by the user's explicit choice.
