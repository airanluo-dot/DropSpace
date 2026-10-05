# Beta11 local CUDA integration handoff

Local checkout `E:\Dev\DropSpace-beta11-cuda`, branch `local/beta11-nvidia-cuda`,
baseline `6788585e16040b4903c6cc534c0780c61079c07e`. Original user checkouts untouched.
Production routing checkpoint `65be1cc3c8bc494e86b87af364b59779771db62c` was pushed.
DLC branch commits 2129381 and 8cda4d5 are included as b3ffb8e and 3915f73;
the sole integration task should avoid applying these patches twice.

## Production behavior

GPU off -> CPU. GPU on retains Automatic/Vulkan/CUDA preference, with CUDA disabled
on incompatible NVIDIA drivers. Automatic chooses the trusted installed CUDA runtime
on compatible NVIDIA; missing components use Vulkan, and GPU failure retains the
existing bounded CPU fallback. Existing models, frozen prompt, KV/timing defaults and
`tools/plain-lyrics-helper/main.cpp` are unchanged.

Base App contains only the real inner CUDA manifest and outer exact-release descriptor,
never CUDA EXE/DLLs. Descriptor pins App version/source SHA, engine SHA, manifest and
archive hashes, the three binary hashes/bytes and two notices. Downloads require explicit
consent, fixed official release URL, bounded HTTPS redirects/time/size and six ZIP entries.
Native executable/DLL read leases remain held until the owned process exits.

## Unified DLC

Settings has first-level DLC after Widgets, before System Activities. Model and CUDA
adapters implement `IDlcPackageProvider`; `DlcManagerService` is the sole transient state
manager. The previous `CudaComponentService` was removed. `CudaRuntimeDlcProvider`
advertises only a valid exact-release download descriptor, reports verified installation
and actual owned on-disk bytes, and delegates consent/download/delete to existing services.
Delete cancels/drains inference under `AiLyricsWorkLifetime.MaintainAsync` before removal,
including old hashed runtime variants and owned partials; it never touches models or lyrics.
Music GPU controls project the same DLC snapshots and open the same DLC page.
No automatic download or AI/GPU activation is added.

## Local compilation and toolchain

Windows App Debug x64 compilation after DLC/CUDA integration passed: zero warnings/errors.
Evidence: `docs/dev/evidence/beta11-local-cuda/dlc-windows-compile.txt`.
No App launch, test suite, benchmark, inference request or test CI was run.
The user canceled the earlier real-request requirement on 2026-10-05; it is not a release gate.

Real device: NVIDIA RTX 5080 Laptop GPU, 16,303 MiB, driver616.56 / UMD13.4.
Official CUDA12.9.1 redist archives SHA256-verified on E: (683,321,966 compressed bytes).
Official MSVC14.44 component VSIXs are SHA256-verified against the existing installed
Microsoft catalog, extracted locally without system installation. Compiler19.44.35228,
matching headers/libs and existing WindowsSDK10.0.26100; no unsupported compiler flags.
NVCC's initial portable-directory OS error was resolved by retaining Microsoft's existing
VC environment scripts under the local toolchain, with documented --use-local-env.

Original installed MSVC19.51 is rejected by CUDA12.9 host_config.h:168 (_MSC_VER>=1950).
Its yvals_core.h:911 additionally requires CUDA13.2. Neither guard was bypassed in the build.
CUDA compiler identification and ABI configuration now pass; the 275-step real native
worker build is in progress. Do not claim a compiled or runtime-qualified worker yet.

Measured cuBLAS DLLs:102,518,272 +668,669,952 =771,188,224 bytes (735.46MiB).
The exact Lt bound is768MiB; other binaries512MiB, total1GiB. Final EXE and ZIP sizes
will be recorded after link/package. Development compiler/SDK files do not ship.

## Remaining handoff

Finish actual native worker link, dependency inspection and external ZIP packaging with
`scripts/package-cuda-runtime.py`. Bind the final downloadable descriptor to the parent's
exact final App tag and source SHA; never substitute placeholders. The parent owns the
only main-branch integration, version bump, base App packaging and publication.
CPU/Vulkan reviewed artifact11316610580 has not been retrieved by this local task.
The installed daily App and driver have not been changed. CUDA real invocation is unverified.
