# 【不准测试】Candidate source and CUDA asset binding sequence

Integrated `806f3e3e40c11a6e7d3d50648a9de8708b16b4ac` and subsequent device-handle
correction `16b9b0184949e0be41dc71fa39b2ffb055d23033`. DLC is already included;
do not apply its commits twice. Root adds invalid GPU-setting normalization,
owned notice-partial cleanup and fingerprint coverage. Native source is unchanged;
no CUDA rebuild, tests or repeated App build ran here.

1. Freeze App source/Beta11 metadata as canonical remote commit **A**. The adjacent
   `beta11-source-review-draft.json` binds actual sources, not runtime or model approval.
2. Reuse the completed native build from actual component source **C**. C may differ
   from A; the real worker source hash and file hashes identify its inputs/outputs.
3. Package real payload with `--app-tag v0.3.1-beta.11 --app-commit A --component-commit C`.
   The ZIP contains no App SHA; only its small outer descriptor binds App A.
4. Supply both JSON files through ignored `CudaLyricsRuntimeRoot` build inputs.
   Compile App A with `SourceRevisionId=A` and `IncludeSourceRevisionInInformationalVersion=true`.
   The consumer requires the exact tag/full SHA in AssemblyInformationalVersion.
   Do not commit the generated descriptors into A.
5. The explicit 2026-10-05 11:28:09 UTC owner authorization is recorded in the Beta11 publication review. The integrator publishes actual artifact identities, then
   publishes the separate ZIP plus base App. No test lane is implied.

If final approval/metadata needs a further commit **B**, change only the outer descriptor's
`appRelease.sourceCommit` from A to B and compile App B. All other descriptor fields
and real ZIP bytes/hashes remain unchanged. This reviewed metadata update requires
no CUDA/model rebuild or download, preventing an App/component SHA waiting cycle.

Remaining inputs: final canonical App SHA/outer descriptor and base App Release packages. The actual worker/ZIP/notices are complete and the tracked Beta11 owner review records the new source and authorization. The reviewed-source override is unchanged. Reported DLL total 547,694,816 bytes is not the archive size.
CUDA execution remains unverified in this delivery; parent applies the latest actual
user release requirements without inventing a successful call.

Candidate code includes the actual DLC page, shared manager/providers, CUDA13 route
and download consumer. Real downloadable assets remain pending; no usable release
is asserted before those publication inputs exist.
