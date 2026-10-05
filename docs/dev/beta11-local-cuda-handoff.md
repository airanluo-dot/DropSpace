# Beta11 local CUDA13 integration handoff

Checkout `E:\Dev\DropSpace-beta11-cuda`, branch `local/beta11-nvidia-cuda`,
baseline `6788585e16040b4903c6cc534c0780c61079c07e`. User checkouts and models untouched.
The main task is the sole main-branch integrator and release publisher.
GPU routing checkpoint65be1cc and unified DLC checkpoint9784aab were pushed.
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
CUDA13.4/MSVC19.51 compiler identification/ABI/configuration passed. Native build275steps
is currently compiling; final worker/link/ZIP are still pending. Do not claim runtime success.

Measured cuBLAS DLLs54,942,320 +492,752,496 =547,694,816 bytes (522.32MiB), about213MiB
below CUDA12.9's two DLLs. PE inspection: cuBLAS depends only on cuBLASLt/KERNEL32;
cuBLASLt only KERNEL32. Final EXE and ZIP sizes await actual link/package.
Development headers/compiler archives do not ship. Base App receives only tiny metadata.

The user explicitly canceled real model calls as an extra release gate. No model inference,
App launch, startup smoke, unit/full regression, benchmark or test CI was run.
Existing1908528192-byte1.8B and7981928896-byte7B model files were not downloaded/copied.

## Remaining publication integration

Finish worker link/import inspection and package actual components/notices using
`scripts/package-cuda-runtime.py`. The sole parent integrator must provide the final App
tag/sourceSHA so its base App embeds the exact matching release descriptor. No guessed
final sourceSHA or placeholder download asset should be published. Parent owns version
bump and base App packaging. Reviewed CPU/Vulkan artifact11316610580 not retrieved here.
Daily installed App is untouched. Actual CUDA invocation remains unverified by user choice.
