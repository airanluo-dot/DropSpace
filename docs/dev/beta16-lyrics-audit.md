# Beta16 AI-lyrics implementation and delivery record

Status: focused source validation passed; final production packaging and publication remain pending.

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

Total: 13 controlled cases and one actual pinned-native compatibility session passed. Local TRX records are `artifacts/beta16/focused-tests/beta16-pipeline.trx` and `tests/DropSpace.Infrastructure.Tests/TestResults/beta16-fasttext-compatibility.trx`; the App test's console result reports eight passed, with no TRX requested or claimed. Final package inspection and the explicit `--lyrics-language-smoke` check of the real embedded App/default native path are pending. No full suite, new quality survey or model benchmark is claimed.

## Bundled assets and release

The exact asset contract is `src/DropSpace.Infrastructure/Lyrics/Manifests/fasttext-lid176-bin-v1.json`. The unchanged official `lid.176.bin` has 131,266,198 bytes and SHA-256 `7e69ec5451bc261cc7844e49e4792a85d7f09c06789ec800fc4a44aec362764e`. The selected native-only `Panlingo.LanguageIdentification.FastText.Native` package is pinned to 0.8.1; Windows x64 `fasttext.dll` has 524,800 bytes and SHA-256 `77e0c6f451dac2ec73591ae513811f0a11a30eaaa0d9365d458cad5121d4678b`. Upstream engine commit is `1142dc4c4ecbc19cc16eee5cdd28472e689267e6`; wrapper/native source commit is `818f6264e87a2f80f33fbb3906053d95be3812c5`. Engine/native-wrapper MIT notices and the full model CC BY-SA 3.0 license, attribution and acquisition details ship in `Licenses/`.

The model is acquired from the official fastText URL during development/build staging and ships as App embedded resource `DropSpace.LyricsLanguage.lid.176.bin`. Production recognition never downloads it, uses an optional component, or substitutes `lid.176.ftz`. One shared CPU-only instance extracts the embedded model to an identity-specific local directory, verifies size/hash, loads it explicitly through a read-only mapping, and verifies the actual loaded native DLL. The full managed wrapper is excluded because it contains its own default ftz. Final source commit and installer/portable/MSIX inventories will be recorded after actual build.

Existing Beta15 release: `404561576`, tag `v0.3.1-beta.15`, nine assets. Before/after asset snapshots compare IDs, names, sizes, digests and update timestamps. Beta16 is a fresh prerelease with its own immutable asset paths; no existing release/tag/assets are replaced.

## Unverified limits

Historical Hy-MT2 semantics and broad CPU/GPU/model/hardware qualification remain incomplete. No real WinUI playback/audio session, new twenty-song survey, full regression suite, model benchmark or installer lifecycle was run. No guaranteed English recognition rate, per-line language uniformity, package-size-as-memory claim or installer lifecycle verdict is made. Final-source limitations will be narrowed only by actual evidence.
