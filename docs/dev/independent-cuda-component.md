# Independently versioned CUDA component

The CUDA package now has its own immutable release alongside the AI model resource
release in `airanluo-dot/DropSpace`. An App release does not rename or republish the
same 540.9 MB archive. `RELEASE_VERSION` is unchanged by this work.

## Identities and compatibility

- Component release: `cuda-llama-cpp-v0.5.0-cuda13-win-x64-v1`
- Asset: `DropSpace-CUDA-llama-cpp-v0.5.0-cuda13-win-x64-v1.zip`
- ZIP: 540873572 bytes; SHA256
  `79e8deb4f8c35e7c9c94da0efa0752826bafe510fcb1e54da23062f2d6f40a0b`
- Inner manifest: 1815 bytes; SHA256
  `da8742d806541edf452061eec408f645be704445952a93895bc8e9d6a200215a`
- Runtime: `llama-cpp-v0.5.0-cuda13-win-x64-v1`; protocol 1;
  profile `hy-q8-plain-resident-v1`
- Engine source: `7fe450e19305b828c199d602c23a8337aaa1f03b`
- CUDA producer source: `806f3e3e40c11a6e7d3d50648a9de8708b16b4ac`

The initial standalone ZIP is the verified Beta16 asset, renamed without repacking.
The worker, two cuBLAS DLLs, inner manifest and two license notices are unchanged.
The cache path still uses RuntimeId plus the exact inner manifest hash. Existing
verified installations are reused; changing the App version does not trigger a
new CUDA download. Old App assets must remain available to already shipped Apps.
No GGUF, CPU/Vulkan runtime, or NVIDIA driver is included.

## Packaging and App binding

`scripts/cuda_runtime_contract.py` owns the reviewed component identity and pinned
archive/manifest hashes. It participates in the AI release source fingerprint.
Changed package bytes require a newly reviewed identity, not an overwrite.

The component producer takes no App version or App commit:

```sh
python scripts/package-cuda-runtime.py --payload <real-producer-payload> \
  --license-directory <real-producer-notices> \
  --component-commit 806f3e3e40c11a6e7d3d50648a9de8708b16b4ac \
  --output <fresh-component-output>
```

It produces the ZIP, original manifest and `cuda-runtime-component.json` (schema 2).
The component descriptor has `componentRelease.tag` and no App binding. Existing
bytes should be reused directly rather than rebuilt/recompressed.

For each future App checkout, run the existing CI staging command:

```sh
python scripts/stage-reviewed-cuda-metadata.py --app-commit <exact-HEAD-SHA>
```

This writes only the manifest and `cuda-runtime-download.json` build inputs. The
latter adds `appRelease.tag` and `appRelease.sourceCommit` to schema 2. There is no
per-Beta allowlist of CUDA reuploads. The exact App tag/commit, component identity,
ABI/profile, trusted URL, sizes and hashes still fail closed in the build checker
and runtime. The downloaded descriptor never supplies its own trust anchor.
Explicit download consent, allowed redirect hosts, bounded extraction and file
verification remain unchanged.

## Publication order and next App release

1. Merge the separate narrow website resource-tag filter before making the
   component release public. Unknown non-App tags remain errors in website sync.
2. Verify the existing ZIP's real size/SHA256 and each inner file; publish the
   renamed ZIP, component descriptor, original manifest and checksums in the
   independent resource release. Do not mark it as the latest App release.
3. Keep the runtime/packaging PR in draft until reviewed for the next Beta. No App
   version bump, App release, or overwrite/deletion of older release assets is
   part of this change.
4. Rebind the next App's exact checkout metadata. The old AI source approval is
   intentionally stale after delivery-code changes and must not be copied,
   edited to pass, or treated as an approval of the new source. A new applicable
   review/authorization is required by the existing release gate.

Focused tests exercise metadata, rejection cases, exact App binding and cache
reuse. They do not execute a CUDA worker, load a model, benchmark a GPU or claim
model quality. The separate `CUDA component contracts` workflow runs only these
contracts; it does not publish an App or change the existing release gates.
