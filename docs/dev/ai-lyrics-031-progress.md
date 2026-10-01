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

## Integration checkpoint 2026-10-01 20:32 UTC

- Current remote PR76 head: `70fc25691127ba3f8861f33a77b44891a04e9710`; local `6da821b` has the same tree `13a480f3dc775c6aade180a11af3cac69fd1df3a`.
- Round-one source partitions are covered in three separate review reports. Repairs are integrated, but full round completion remains **0/3** until exact-candidate Windows/native revalidation succeeds. Subsequent rounds and the separate critical product review remain required.
- Previous `2d39f1` failed only the two settings whole-record equality assertions that did not account for the newly serialized peer-mode collection. These test expectations have been updated consistently with the other independently checked collection properties. Corresponding local tests passed. Do not rerun the old candidate hoping for green.
- First complete 48-line native CPU test exposed a real prompt failure: contextual rows were emitted as output. Separating background from task rows fixed Chinese, but same-language English batches emitted an empty array. Version 3 now constrains each batch with a JSON-schema tuple, fixed IDs, exact count and nonempty strings; strict post-validation is unchanged.
- Version-3 standard model full songs: English 142.16 seconds, Chinese approximately 116.30 seconds; four batches per target passed on first attempt, no clipping of model output, source text/time preserved, cache reuse made zero inference calls. These are this machine's observations, not end-user performance promises. Compact full-song validation is still running.
- Current local Core: 358 passed. Focused coordinator/settings: 11 passed. Worker: 22 passed. Windows-only/unsupported local transport checks must not be represented as portable passes.
- The new native smoke path additionally checks the 29 settings UI Automation names and recovery of the same overlay HWND after a simulated failure state; this is queued in the current Windows workflows, not yet verified.
- Website visual design is final and owner-approved. No public website deployment or App release has occurred. GPT Site language-root routing must be verified through actual access at publication. The current PR website check passed, including its browser suite; final deployed access is a separate obligation.
- Publication is followed by the owner's separately requested cloud Codex review, using the signed-in browser and DropSpace environment, with the exact instruction: 开启全面代码审查. Do not create that task before publication is confirmed.

## 2026-10-01 20:58 UTC: release gate remains closed

- Remote `70fc25691127ba3f8861f33a77b44891a04e9710`: Windows CI, Release verification, secret scan and website PR checks succeeded. Core 358, Infrastructure 320 passed/3 skipped, App 262 passed/3 skipped on each Windows test route; separate native AI smoke 2 passed/0 skipped. Signing and publication were skipped. No new release or website deployment occurred.
- Complete review rounds remain **0/3**. This is first-round remediation, not a completed whole-product validation.
- Full-song structural tests succeeded, but semantic QA found known meaning errors that persist in independent single-line official-style prompts. See `round1-full-song-semantic-review.md`. The approved semantic-quality gate is not satisfied; disclaimers and structural validation are not substitutes.
- Additional plan-gap fixes now include embedded hash-verified tokenizer, measured-token adaptive input batching (1800 input tokens, 4096 total context, 2048 output reservation), compact 1.5 GiB memory cap, and three-consecutive-failure pause with explicit manual recovery UI. Prompt/cache version advanced to prevent reusing pre-budget results. These changes require new Windows CI, native tokenizer/compact-memory and cancellation verification.
- Local Core 360 passed. Full Infrastructure run on Linux: 307 passed, 14 skipped, 8 failed; failures concern Windows DPAPI/host-composition platform assumptions and are **not counted as passes**. Current-source focused AI tests and native Windows tests are separately tracked. Linked actual AI service/settings-card compilation succeeded against Windows projection references with zero warnings/errors.
- The user-approved website appearance is unchanged. App and both website releases remain held until the required review, product checks and quality gates are complete. The external Codex request is conditional on actual App release and has not been sent.
