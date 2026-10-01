# AI lyrics 0.3.1 Beta 1 implementation status

2026-10-01 UTC. Implementation is incomplete and must not be released yet.

Approved plan revision 1.3 adds effective App language as translation target and outward multilayer flowing glow. Three modes: off, visible AI lyrics only, manually selected music mode. All changes fade; original contour remains stable. Model consent, standard/smaller options, independent text sizes, isolated inference and three full review/fix/retest rounds remain required.

Implemented foundations:
- Translation target uses existing AppLanguagePolicy rather than a separate language preference.
- Matching provider translations have priority; mismatched target language does not suppress local translation.
- Translation provenance and target language fields added without changing LyricsLine constructor.
- Glow eligibility is separate from frame-rate independent brightness envelope.
- Strict JSON output validation preserves original text and time axis, rejects duplicate/missing/reordered IDs and extra fields atomically.
- Context prompt and cache identity include metadata, ordered text/timing, target, model hash and prompt revision.

Validation: Core tests passed 323/323 before the final pre-serialization input budget guard; rerun required after that guard. No App/native UI, inference integration or release audit completed.

Standard model metadata verified from official Hugging Face API:
- repo tencent/Hy-MT2-1.8B-GGUF
- revision a0c709d9fac510f2c807aa3af52872340dc37a4a
- file Hy-MT2-1.8B-Q4_K_M.gguf
- bytes 1133080448
- SHA256 dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699
- local development download hash passed; this is not yet the in-app downloader.

Official llama.cpp v0.5.0 source checked out at 7fe450e19305b828c199d602c23a8337aaa1f03b for CPU tests. Compilation ongoing. Official 1.25bit candidate needs STQ support; compatibility unverified. Do not assume the generic GGUF label guarantees it.

## Checkpoint 2026-10-01 18:01 UTC

The earlier validation and compilation status above is superseded by this checkpoint.
- Core tests: 332 passed, including protocol-corruption rejection inside otherwise valid JSON.
- CPU llama.cpp compilation succeeded. Standard model inference works through the C# runner.
- Model download service implements explicit consent, bounded resume, redirect allowlist, SHA-256 verification and atomic install; focused infrastructure tests previously passed 9/9. Windows native integration remains pending.
- Cache/coordinator and font-size UI are implemented locally; UI has not passed Windows build or visual checks.
- Six ten-line language batches per model were run. Standard 1.8B took 20.7–28.8 seconds per batch including process/model startup. One Japanese-to-Chinese output leaked prompt fragments into its last JSON string. The new validator rejects this observed corruption; these measurements are not an end-user latency guarantee.
- Qwen3 0.6B Q8 took 12.4–23.5 seconds per batch but materially mistranslated several Japanese/Korean lines, including reversing meaning, and copied Japanese unchanged for the Chinese target. It is not approved as the lightweight release model.
- Official Hy-MT2 1.25-bit candidate failed to load in this runtime and remains excluded.
- Release metadata corrections and 12 regression checks are committed as 9b21674 on the development branch for inclusion in 0.3.1 Beta 1. No remote push or release is implied.
- Still required: eligible lightweight model, Windows runtime packaging/hard limits, App inference/model-management integration, actual outward glow renderer and three-position control, native Windows tests, complete review/fix/retest rounds, final version metadata and release gates.

## Integration checkpoint 2026-10-01 18:49 UTC

- App translation service and guarded media-generation publication are wired to verified model/runtime services.
- Native AI model settings, consent/download/resume/cancel, model selection, three-detent glow control, and font controls are implemented.
- Outward layered-window glow and renderer are implemented; software raster/compositing checks pass, actual native behavior awaits the next Windows run.
- Runtime has baseline/AVX2 embedded payload selection, atomic Windows job assignment, committed-memory/child-count/close limits, fixed-source build and integrity checks. Real Linux runner smoke and cancellation pass; Windows release lane now explicitly tests both model variants using pinned development-only downloads.
- Compact IQ3_S is now an opt-in experimental catalog item with a hash-specific EOS correction, explicit third-party attribution, smaller-size/lower-memory caveat and no faster/fewer-parameters claim. See model evaluation document.
- Current local checks: Core343 passed; focused lyrics/runtime infrastructure44 passed with6 Windows-only skips; metadata12 passed; XML/YAML syntax and654 bilingual keys agree.
- Initial PR76 batch at remote5664ea2 passed both Windows CI and release-bundle validation. This does not validate the current new integration. One prior clipboard smoke timeout passed on rerun and remains tracked.
- User additionally requires completion of all features, full first-time-user/code/data/flow review, separate critical product review, a structured issue file, and item-by-item fixes before publication. These full review gates have not been completed. Website work proceeds separately and its preview is informational, not a release approval gate.
