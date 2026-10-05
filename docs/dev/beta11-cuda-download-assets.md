# 【不准测试】Separate CUDA download assets for Beta11

The latest user instruction supersedes embedding CUDA binaries in the base App.
GPU off uses CPU. GPU on offers Auto/Vulkan/CUDA; Auto prefers CUDA on NVIDIA and
Vulkan otherwise. Non-NVIDIA devices cannot select CUDA. Missing CUDA components
require an explicit download prompt, including Auto; no silent download and no
second GGUF download. The local task owns this UI, detection, routing and download
implementation. This cloud change only prepares separate release packaging.
The subsequently requested DLC page is also part of Beta11 scope: model and CUDA
cards must use the agreed shared management abstraction and the local CUDA package's
single state source. This packager does not add another downloader or management API.
The parent must integrate that page before releasing the target version.

## Deliverable and trust boundary

`scripts/package-cuda-runtime.py` consumes the real output of the independent CUDA
producer, without building, invoking or probing a worker/model. It emits:

- `DropSpace-CUDA-win-x64-<exact-App-tag>.zip`: exactly the worker, two cuBLAS DLLs,
  component manifest and two notices. No GGUF, CPU/Vulkan worker or driver installer.
- `cuda-runtime-download.json`: small outer descriptor with the ZIP's actual SHA256,
  byte count and official release URL, exact App tag/source commit, component source
  commit, engine/worker identity and inner file/manifest identities.
- `cuda-runtime-manifest.json`: exact producer bytes copied as small App build input;
  its hash remains the component/cache identity. This does not copy any CUDA binary
  into the base App.

The downloaded ZIP/manifest is not its own trust anchor. The matching App build must
pin the small descriptor/manifest from the reviewed producer output, then verify the
whole downloaded archive before extraction and every executable/DLL before launch.
Only the fixed six ZIP entries are allowed. Existing reparse-safe extraction,
retained component leases and process ownership belong to the local integration.
The ordinary shipping CPU/Vulkan runtime and its reviewed-source override are
unchanged. This descriptor does not replace the existing App publication binding.
Generate the metadata after checkout of the final reviewed commit as build inputs;
do not try to check an App's own final commit SHA into that same commit.

The schema currently preserves the independent producer's RuntimeId
`llama-cpp-v0.5.0-cuda12-win-x64-experiment-v1`, protocol 1, profile
`hy-q8-plain-resident-v1`, and its ordered three-file component manifest. Local
integration must align the consumer with this outer contract, or coordinate any
changed ID/schema back to this packager before publication. These are implementation
identifiers, not an assertion that CUDA is already a usable App option.

## Versioned outer descriptor contract

| Field | Meaning |
| --- | --- |
| `schemaVersion` | 1; descriptor is at most 16 KiB |
| `repository` | `airanluo-dot/DropSpace` |
| `appRelease.tag`, `appRelease.sourceCommit` | Exact release tag and final reviewed App commit; different from the upstream engine commit |
| `componentSourceCommit` | Reviewed DropSpace source commit used to produce this CUDA worker |
| `runtimeId`, `backend`, `platform`, `protocol`, `profile` | Explicit CUDA/win-x64 ABI/role identity |
| `engineSourceCommit`, `workerSourceSha256` | Pinned upstream engine and generated worker source identities |
| `download.name`, `url`, `sha256`, `bytes` | Exact immutable release ZIP asset and actual compressed integrity/size |
| `manifest` | `{name, sha256, bytes}` of the exact inner component manifest |
| `files` | Existing ordered `{name, sha256, bytes}` records for worker/cuBLAS/cuBLASLt |
| `notices` | Two fixed notice file identities |

Archive size and total component size are different. The prompt/download progress
should use `download.bytes`; retained runtime admission uses actual component sizes.
When App versions reuse identical inner manifest bytes, an already verified component
may be reused without downloading again. The small App-version binding still changes
for the new release; the downloader must not infer compatibility from a filename.

## Producer/publication sequence

1. Complete the necessary Windows CUDA compilation in the authorized local task.
   Reuse the pinned engine, installed CUDA toolkit and existing model; do not enable
   startup/benchmark/test flags in the cloud packaging lane.
2. Run the packager on that real payload and its producer notices. Output must be a
   new directory; previous artifacts are preserved. The implementation streams file
   contents, validates them against producer identities while writing the ZIP, and
   writes hashes from real bytes. No placeholder hashes or model calls are emitted.
3. Bind the descriptor's `appRelease` to the parent's final App release/tag. Embed
   only small trust metadata in the App; publish the ZIP plus descriptor as separate
   assets of that exact release. Parent retains publication/version ownership.
4. Connect the local consumer to those exact pinned assets and preserve explicit
   consent/error/cancellation behavior. Missing assets are unavailable; they must
   not be presented as a working CUDA backend or replaced with a guessed URL.

Packaging invocation (only when real producer files exist; this is not a test):

```text
python scripts/package-cuda-runtime.py --payload <producer-payload>
  --license-directory <producer-build> --app-tag <exact-release-tag>
  --app-commit <final-App-SHA> --component-commit <CUDA-producer-SHA>
  --output <fresh-release-staging-directory>
```

All required arguments use actual release/producer values; `RELEASE_VERSION` is not
changed by this script. Licensing notices come from the existing producer, not a new
download. No driver installation, network access or release upload occurs here.

## Current evidence and remaining inputs

No test, benchmark, native startup, model trial or CI was run for this change. The
packager was manually reviewed but has not been executed: this cloud environment
does not contain the real Windows CUDA worker/DLL payload. Consequently no actual
ZIP hash, download descriptor or usable asset has been generated or published.

Required next inputs are the real Windows producer payload/notices, the final App
tag/source SHA, and the local consumer's aligned metadata contract. The single real
CUDA call exception remains restricted to the local task; it does not authorize
cloud tests or make packaging a native execution check. These missing artifacts are
concrete release inputs, not a demand for additional testing.
