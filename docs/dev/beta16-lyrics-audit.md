# Beta16 AI-lyrics implementation and delivery record

Status: published [v0.3.1-beta.16](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.1-beta.16) at `2026-10-06T12:40:40Z`. Required local focused checks, necessary packaging, exact asset-verification CI and public Release/website verification passed. Final build source is `44f89877ae2a01b10a76b740c4cf3d3caf692376`; the execution limits below remain explicit.

## Scope and baseline

The October 6, 2026 owner request and final handoff plan supersede conflicting earlier AI-lyric plans. Beta15 production source is `734e0a96366fcffae4d3e8361745e4ba549ebf06`; the current checkout began at `d1314ac` on `codex/v0.3.1-beta.15`, then fast-forwarded to `a26c43a` with an identical source tree while retaining local work. Existing uncommitted lyric-source protection and local evidence are reviewed and preserved when consistent with the final plan. The untracked `.codex` directory contains prior builds, private diagnostics and evidence; it is not a public source or asset inventory. Two conflicting uncommitted gap-filling/completeness test files and the initial patch were preserved under local `artifacts/beta16` evidence rather than treated as current requirements.

Only AI lyrics, directly required dependency/asset configuration and release metadata are in scope. Media layout, downloads, settings, Hy-MT2 model selection, prompt, sampler and worker protocol retain Beta15 behavior. No paused cloud implementation is resumed.

## Required order

1. Map the current app language to `en` or `zh`.
2. Clean only original lyric body lines, join with spaces and run one fastText prediction for the whole original. A reliable target-language top label yields Target; every other result yields Unknown.
3. For Unknown, retain bounded normal provider queries and inspect provider translation tracks independently. Any valid target translation, including partial coverage, disables AI for the entire track and preserves gaps.
4. Only Unknown without a valid target translation may enter the existing opt-in Hy-MT2 flow. Every resource request, queue and output/cache boundary uses the current whole-track decision and identity.

Language prediction cache identities include content role/fingerprint, model SHA-256, native/wrapper identity and preprocessing version. Predictions retain real labels/scores; app-language switches remap the prediction. Persistent AI cache identities include target, source/candidate/content revisions and rule version; live publication and cache-write fences also bind the playback generation. Late native translations remove current AI content and fence pending results.

## Existing defect evidence

The local installed-Beta15 trace records six songs with 25 replaced provider rows. The same-recording Die For You review confirms 71 native Chinese translations survived source parsing/alignment; the ten affected occurrences were admitted later by language/protection policy. The final Beta16 rule requires zero AI for Die For You and for her with 48 native translations and five real gaps. The prior evidence does not constitute a Beta16 execution result.

## Actual checks and outcomes

- Controlled infrastructure tests: `FullyQualifiedName~Beta16WholeTrackPipelineTests`, five passed. They cover target-original and partial-native zero lookup/cache/inference, bounded Unknown lookup followed by progressive translation and repeated occurrences, late-native cancellation and cache/final-result retirement, legacy cache veto and source/target revision binding, and preserving/restoring non-target provider source roles.
- Controlled App tests: `FullyQualifiedName~Beta16WholeTrackAiAdmissionTests`, eight passed, with necessary Debug x64 WinUI compilation. The initial environment/build failures were resolved by matching current CUDA descriptor source/version metadata and using the existing native-test `WindowsAppSdkDeploymentManagerInitialize=false` configuration. These are controlled API/state tests, not real playback observations.
- Actual Windows native compatibility: `FullyQualifiedName~Beta16FastTextBundlingTests`, one passed using one native/model instance (`ModelLoadCount=1`, `PredictionCount=7`). It submits actual whole-original samples, remaps an app-language switch from cached predictions, exercises English/Chinese/non-target labels, and replays existing Die For You/her provider payloads. Die For You retains 71 native translations and her retains 48; eligible AI rows are zero and exact provider Secondary text is preserved. Her's five ordinary gaps stay gaps; two additional credit-only blank rows do not change the rule. This used the staged official bin and restored native library, before final packaging.
- Required dependency restore succeeded for the solution and the production-evidence project, including the transitive native-only package lock.

Total: 13 controlled cases and one actual pinned-native compatibility session passed. Local TRX records are `artifacts/beta16/focused-tests/beta16-pipeline.trx` and `tests/DropSpace.Infrastructure.Tests/TestResults/beta16-fasttext-compatibility.trx`; the App test's console result reports eight passed, with no TRX requested or claimed. No full suite, new quality survey or model benchmark is claimed.

The first candidate at `5e20cb67bd07ea8027c86f458714c1ff44d1a773` compiled all three formats. Its actual explicit portable `--lyrics-language-smoke` entry passed using the production default adapter and the App's embedded resource: one model load, two native predictions, English/English and Chinese/Chinese Target, and English-to-Chinese app-language remapping from the cached raw prediction. Actual loaded native path is the single-file `.net/DropSpace/.../fasttext.DLL`; model path is the App-owned `BundledLyricsLanguage/<model-sha256>/lid.176.bin` directory. Both identities are verified by the adapter. The check performs no Hy-MT2 inference and records no original lyric text. Local result: `artifacts/beta16/bundled-language-smoke.json`.

Static inspection of that first portable verifies one original bin resource, one exact Windows x64 native DLL, three exact notice files and no ftz/second model. Its eight existing Hy-MT2 runtime files match the reviewed native inventory. Those records are preserved in `artifacts/beta16/first-candidate-package-inspection`.

The active source-review pointer was not included in the first candidate's source commit. Rather than tag a checkout that fails its own source gate, the final source `44f89877ae2a01b10a76b740c4cf3d3caf692376` adds only the correct pointer and reruns necessary packaging with matching CUDA descriptor commit metadata. App language/admission/diagnostic implementation and asset source bytes are unchanged. The first-candidate native execution is identified as such; final portable/MSIX payloads and same-portable installer compiler input were statically checked again without relabeling the earlier execution as a final-binary test. Installation/MSIX activation are not inferred from static inspection.

The first portable has 488,249,448 bytes and SHA-256 `971996c218d13728232115f60454d3c3dbedc1dbd83290ca3a704c01ea3c92cd`; it is not the final public asset. Final-source portable/MSIX Release packaging and pinned model/native/license/runtime static inspections passed. Final installer compilation passed and its record binds the exact inspected portable input; installer execution is not claimed. MSIX produced zero errors and one warning for the absent optional `mspdbcmf` symbol tool.

`node scripts/test-ai-release-approval.mjs --release-bundle artifacts/beta16/upload` passed against final source `44f89877ae2a01b10a76b740c4cf3d3caf692376`, 248 code-owned inputs and exact runtime/package records. `Test-UpdateManifest.ps1` passed strict schema/version/size/SHA-256 checks. The actual MSIX manifest is x64 version `0.3.1.16`; executable versions and Beta update code `3010016` match the release. Every checksum entry and copied upload asset is bound to the final inventory. Local review record: `artifacts/beta16/release-review-manifest.json`.

## Final nine-asset inventory

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| DropSpace.exe | 488249448 | `0a9af9f7dda1fc77251ec224f84e72023fe64cb3f0e3d5caa20627db3ae61ac1` |
| DropSpaceSetup.exe | 203573736 | `0b4fb1ac9dad351677f63a706746115c4fc83da2916bb4caeaf5a62da5975366` |
| DropSpace-x64.msix | 186724604 | `8ffda49a62d7729b3d332a821b7c9157025ba79119f530665e3bde25d86f9abf` |
| DropSpace-CUDA-win-x64-v0.3.1-beta.16.zip | 540873572 | `79e8deb4f8c35e7c9c94da0efa0752826bafe510fcb1e54da23062f2d6f40a0b` |
| cuda-runtime-download.json | 1867 | `4e083b69a03af83dca543433e2e1256555fa2622fe2736e16c182138b74e8937` |
| cuda-runtime-manifest.json | 1815 | `da8742d806541edf452061eec408f645be704445952a93895bc8e9d6a200215a` |
| runtime-publication.json | 5235 | `3bc111c50e237ed71ca3d4d197ca2ef8090b25f520b96a5381db8ab000a8167d` |
| update-manifest.json | 772 | `31084bb0c90fbd8ea10166ef327debd90d87382eb419dcbfa52a641fe8b27053` |
| SHA256SUMS.txt | 730 | `1b096a324646830e657cd5ab290ca59c405eee42350cd0cdd550d9815b8a9b20` |

## Bundled assets and release

The exact asset contract is `src/DropSpace.Infrastructure/Lyrics/Manifests/fasttext-lid176-bin-v1.json`. The unchanged official `lid.176.bin` has 131,266,198 bytes and SHA-256 `7e69ec5451bc261cc7844e49e4792a85d7f09c06789ec800fc4a44aec362764e`. The selected native-only `Panlingo.LanguageIdentification.FastText.Native` package is pinned to 0.8.1; Windows x64 `fasttext.dll` has 524,800 bytes and SHA-256 `77e0c6f451dac2ec73591ae513811f0a11a30eaaa0d9365d458cad5121d4678b`. Upstream engine commit is `1142dc4c4ecbc19cc16eee5cdd28472e689267e6`; wrapper/native source commit is `818f6264e87a2f80f33fbb3906053d95be3812c5`. Engine/native-wrapper MIT notices and the full model CC BY-SA 3.0 license, attribution and acquisition details ship in `Licenses/`.

The model is acquired from the official fastText URL during development/build staging and ships as App embedded resource `DropSpace.LyricsLanguage.lid.176.bin`. Production recognition never downloads it, uses an optional component, or substitutes `lid.176.ftz`. One shared CPU-only instance extracts the embedded model to an identity-specific local directory, verifies size/hash, loads it explicitly through a read-only mapping, and verifies the actual loaded native DLL. The full managed wrapper is excluded because it contains its own default ftz. Final source commit and installer/portable/MSIX identities are recorded above from the actual builds.

Beta16 Release ID is `404681331`, a prerelease with tag and target commit both bound to final build source `44f89877ae2a01b10a76b740c4cf3d3caf692376`. All nine new asset IDs, byte counts and server SHA-256 digests match the reviewed inventory above and remain unchanged from draft upload to publication. The release was published with `--latest=false`; the latest Stable remains `v0.2.1`. Actual publication receipt is `artifacts/beta16/published-release-404681331.json`.

Existing Beta15 release: `404561576`, tag `v0.3.1-beta.15`, nine assets. Before-upload and after-publication snapshots match every asset's ID, name, size, digest, creation/update timestamp, state and public download URL. No existing release/tag/assets were replaced. The local receipts are `artifacts/beta16/beta15-assets-before.json` and `artifacts/beta16/beta15-release-after-publication.json`.

## Publication and website verification

[Windows artifact-verification CI 37464573481](https://github.com/airanluo-dot/DropSpace/actions/runs/37464573481) passed. It downloaded and hashed the exact nine uploaded assets, verified source/runtime approval binding and used the fail-closed Beta16 artifact route. Functional tests, native smoke, compilation and lifecycle steps were explicitly skipped; this CI result does not repeat or broaden the local execution evidence. [PR98](https://github.com/airanluo-dot/DropSpace/pull/98) merged the source and delivery metadata into main as `e8f13e6cd8eab3f76eedd5e84a5ee2c56adcd84d` at `2026-10-06T12:39:24Z`; this metadata merge does not change the packaged source identity.

The [automatic Release-event Pages run 37464988925](https://github.com/airanluo-dot/DropSpace/actions/runs/37464988925) failed at deployment: the GitHub check annotation states that tag `v0.3.1-beta.16` is not allowed to deploy to `github-pages` by the environment protection rules. Its build passed and its live verify job was skipped. The protection settings were retained. A [main-branch dispatch 37465088818](https://github.com/airanluo-dot/DropSpace/actions/runs/37465088818) then synchronized the published release, built and deployed the website, and passed its live verification job. Broad static/browser/visual website tests were skipped under the existing exact release waiver.

The actual local command `node --use-env-proxy website/_source/scripts/verify-published-release.mjs v0.3.1-beta.16 180` also exited zero, verifying GitHub Release, nine public assets, manifest, checksums, public release API, latest-change API and live website. An earlier attempt without the existing environment proxy failed with `ECONNRESET`; it was a network attempt, not a passing verification. Local receipts include `artifacts/beta16/pages-release-event-annotations.json`, `artifacts/beta16/pages-main-dispatch-jobs.json` and `artifacts/beta16/public-live-verification.txt`.

## Unverified limits

Historical Hy-MT2 semantics and broad CPU/GPU/model/hardware qualification remain incomplete. No real WinUI playback/audio session, new twenty-song survey, full regression suite, model benchmark or installer lifecycle was run. No guaranteed English recognition rate, per-line language uniformity, package-size-as-memory claim or installer lifecycle verdict is made. Final-source limitations will be narrowed only by actual evidence.
