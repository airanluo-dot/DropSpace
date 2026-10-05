# Beta11 local CUDA integration handoff

Local checkout: `E:\Dev\DropSpace-beta11-cuda`; branch `local/beta11-nvidia-cuda`.
Base: `6788585e16040b4903c6cc534c0780c61079c07e`. Existing user checkouts were preserved.

## Completed code

- Production DI uses `PersistentPlainLyricsRunner.CreateAutomatic` and the singleton CUDA package/component service.
- GPU off uses CPU. GPU on persists `LyricsGpuBackend.Automatic/Vulkan/Cuda` via `LyricsSettings.AiLyricsGpuBackend`; Auto selects CUDA only with a compatible NVIDIA driver and installed components, otherwise Vulkan. Existing bounded GPU failure -> CPU behavior is retained. No inference/model/prompt changes.
- GPU controls are in `Views/Music/AiLyricsSettingsCard.cs`; non-compatible NVIDIA hardware disables CUDA. First missing-component confirmation invokes `AiLyricsService.RequestCudaComponents()` rather than starting a silent download.
- Base App embeds only `cuda-runtime-manifest.json` and `cuda-runtime-download.json`; Release fails if these real inputs are absent. No CUDA EXE/DLL is embedded.
- Downloader aligns with `8589560`'s exact outer descriptor: App tag and assembly informational source SHA, inner manifest hash, worker/engine identity, exact release URL, archive SHA/bytes, fixed six ZIP entries, executable/DLL and notice hashes. Reparse-safe files and retained runtime leases remain in effect.
- Fixed `package-cuda-runtime.py`'s single-component bound precisely for `cublasLt64_12.dll`: 768 MiB; other files stay 512 MiB, total stays 1 GiB. This admits the real 668,669,952-byte CUDA 12.9 Lt component without removing limits.
- Mainline KV/timing and native worker `main.cpp` were preserved; CUDA remains a generated overlay.

## Shared DLC API

Use singleton `DropSpace.Infrastructure.Lyrics.CudaComponentService`, already registered in App DI. This is the sole component state source.

Properties: `Id`, `IsNvidia`, `IsInstalled`, `IsDownloading`, `Progress` (0..1), `Error` (exception category only), `DownloadBytes`, `InstalledBytes`, `HasDownloadManifest`.

Methods: `RefreshAsync(token)`, `DownloadAsync(bool consent, token)`, `CancelDownload()`, `RemoveAsync(token)`. Removal drains the owned resident process before deleting components. Download does not download or delete any model. Missing metadata is unavailable, not a placeholder working asset.

Event: `StateChanged` may run on a worker thread; DLC UI must dispatch.

`AiLyricsService.CudaComponents` exposes the same singleton. `CudaComponentsRequested` is the navigation event the DLC task must connect to its CUDA card. `CudaDownloadRequired` is the initial automatic-selection notification; the music card handles it while loaded. The DLC/MainWindow task should hook the same signal for a global prompt when the music card is not loaded. `ActualBackend` reports the completed runner response, not the selected preference.

## Actual local evidence and blockers

- `nvidia-smi`: NVIDIA GeForce RTX 5080 Laptop GPU, 16,303 MiB; driver 616.56, reported CUDA UMD 13.4.
- Existing tools: .NET SDK 10.0.401, MSVC 14.51.36231 / VS 18 Community, bundled CMake and Ninja, Codex bundled Python 3.12. Git works. No CUDA toolkit/nvcc found in PATH, environment, or standard NVIDIA toolkit installation directory.
- Existing 1.8B Q8 GGUF: 1,908,528,192 bytes. Existing 7B GGUF: 7,981,928,896 bytes. Neither was downloaded, changed or copied.
- Infrastructure and App compile succeeded; App Debug x64 portable compile: zero warnings/errors. Final compilation evidence: `artifacts/beta11-cuda-compile.log`. No unit tests, regressions, CI, benchmarks, native startup checks, App launch or model inference were run.
- CUDA worker build has NOT started: nvcc/toolkit absent. The user's instruction required reporting new tools before installing. A request to permit official CUDA 12.9 portable components on E: (no driver installation/change) is pending and has not received an answer. Do not claim CUDA qualification or publication.
- MSVC 14.51 is newer than CUDA 12.9's usual host compiler support; compatibility must be resolved with the existing tools after toolkit authorization. No new compiler, driver or system settings were installed/modified.
- Official CUDA 12.9.1/cuBLAS 12.9.1.4 metadata reports DLLs 102,518,272 and 668,669,952 bytes, total 771,188,224 bytes (735.46 MiB); official cuBLAS archive 549,755,186 bytes (524.29 MiB). Final worker and final combined runtime ZIP sizes remain unknown until real compilation/packaging.
- Source: https://developer.download.nvidia.com/compute/cuda/redist/redistrib_12.9.1.json . These are official component metadata figures, not measured local outputs.
- CUDA producer payload/notices, actual component fingerprints, final App tag/SHA-bound descriptor, separate release ZIP and the single real CUDA call are still required. Reuse `scripts/package-cuda-runtime.py`; do not publish placeholder hashes or infer CUDA success from CPU fallback.
- Runtime source for CPU/Vulkan remains the parent's reviewed artifact 11316610580; it was not retrieved in this local task. Daily installed App was untouched.
- Cross-thread message tool returned an error, so no successful direct message to the DLC task is claimed. This document and final response carry the API contract.
