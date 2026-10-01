# Round 1 supplemental full-song local inference QA

Date: 2026-10-01. Linux development environment only. This supplements the first review round; it is not a completed full audit or a Windows release gate pass.

## Current status: native structure passes; semantic release gate blocked

After the parent integrated production v3 exact-ID constrained output, the requested basic full-song structural/runtime checks passed. Subsequent complete semantic review and a local-context fidelity retry reproduced a serious leave/keep action reversal and scarf/headband object substitution. The semantic release-quality gate remains blocked; an AI disclaimer is not a substitute for satisfying the approved criterion. The runtime results below must not be treated as full release approval:

| Model / target | 48-line song time | Strict batches | Cache reuse | Cancellation |
|---|---:|---:|---|---:|
| Standard Q4 / English | 142.142 s | 4/4, first attempts | Equivalent result, 0 inference calls | 2.011 s |
| Standard Q4 / Chinese | 116.292 s | 4/4, first attempts | Equivalent result, 0 inference calls | Shared standard cancellation check |
| Compact IQ3_S / Chinese | 154.061 s | 4/4, first attempts | Equivalent result, 0 inference calls | 2.010 s |

Each result has 36 translated foreign-language lines plus 12 target-language lines retained unchanged. Original timings are preserved. The earlier failures below are retained as regression evidence and explain the verified correction; they are not the final v3 result. This is Linux native pipeline evidence, not Windows UI or semantic-quality certification. A subsequent full 144-pair semantic reading found concrete model errors (including leave/keep reversal and scarf/headband substitution); see `round1-full-song-semantic-review.md`. The structural pass must not be reported as translation-quality approval.

## Scope and method

An independent QA harness called the unchanged production `LyricsTranslationCoordinator`, `LyricsTranslationPrompt`, `LlamaCompletionRunner` and `LyricsTranslationOutput`. It reused the existing local standard Hy-MT2-1.8B Q4_K_M GGUF and llama.cpp executable; no model was downloaded and no product implementation was edited. The test source is an original synthetic 48-line song: 12 English, 12 Japanese, 12 Korean and 12 Chinese lines, with fixed four-second timings. No private/user lyric data was used.

Production limits were retained: 12 requested lines per batch, surrounding context, 60 seconds per runner invocation, 180 seconds per complete song, 4096 context tokens, 2048 generated tokens, four CPU threads, zero GPU layers, non-mmap load mode, offline execution and original strict parser. The existing 3 GiB working-set watchdog remains active. Linux does not validate the mandatory Windows Job Object limits.

## Actual production-path results: failed to translate the complete song

| Target | First attempt | Retry | Song elapsed | Result |
|---|---:|---:|---:|---|
| English | 32.124 s, rejected | 29.497 s, rejected | 61.704 s | Original document returned; zero translated lines; zero cache files |
| Simplified Chinese | 27.641 s, rejected | 28.076 s, rejected | 55.727 s | Original document returned; zero translated lines; zero cache files |

All four completed outputs contained IDs 0–15, although only IDs 0–11 were requested. The additional IDs correspond exactly to the four following context lines in the prompt. The parser correctly rejected these outputs atomically; no output clipping or acceptance-rule weakening was performed. Original text and timestamps stayed unchanged. Neither run reached batches 2–4, so neither establishes complete-song performance or satisfaction of the 180-second end-to-end budget.

A real native inference cancelled after a requested two seconds threw `OperationCanceledException` after 2.014 seconds. The staging prompt directory contained zero files afterward. This confirms this one cancellation/cleanup case, not the Windows GUI song-switch lifecycle or all native descendants under load.

## Parser alignment boundaries

A separate harness called the actual production parser:

- Correct two-line translations: accepted; timings preserved
- Reordered IDs: rejected
- Extra context output row: rejected
- Correct IDs with the two translation texts swapped: accepted
- English source unchanged while Chinese was requested: accepted, zero secondary translations

The parser ensures structural ID alignment and original timing preservation. It cannot establish semantic source-to-translation alignment or that output is in the requested language. No semantic mismatch was deliberately injected into the native model run; the two latter cases are controlled parser-boundary fixtures.

## Isolated prompt comparison (no product-source edit)

A single original Chinese-target batch was repeated with context represented as plain text without IDs and the requested records in a distinct input array. The original strict parser accepted exactly IDs 0–11: 12 translated lines, 32.979 seconds. No output was removed or normalized beyond the unchanged runner's runtime-terminator handling.

This single success supports separating background from requested records; it does not establish robust multi-language or full-song success. A second comparison requested by the reviewer used only non-task context strings in `background`, a separate `lines` array, and a final exact-count/ID reminder. It also passed unchanged strict parsing with exactly 12 translated lines in 29.898 seconds. Both comparisons concern one batch only; production full-song retesting is still required after any prompt change.

## Evidence and reproducibility

- `.runtime/fullsong-qa/Program.cs`: independent production-path harness
- `.runtime/fullsong-qa/synthetic-source.json`: complete synthetic fixture
- `.runtime/fullsong-qa/results.json` and `run.log`: measured outcomes
- `.runtime/fullsong-qa/en-US-{1,2}.prompt.txt` and `.output.txt`: original prompt/output pairs
- `.runtime/fullsong-qa/zh-CN-{1,2}.prompt.txt` and `.output.txt`: original prompt/output pairs
- `.runtime/fullsong-qa/source-hashes.txt`: evaluated source fingerprints
- `.runtime/fullsong-boundary-qa/Program.cs` and `results.jsonl`: structural/semantic boundary checks
- `.runtime/fullsong-prompt-compare/candidate.prompt.txt`, `candidate.output.txt`, `result.json`: first candidate
- `.runtime/fullsong-prompt-compare-v2/`: second candidate harness and evidence

The harness release builds completed with zero warnings/errors. An initial default `dotnet run` build failed without a compiler diagnostic; an explicit serial Release build with shared compilation disabled succeeded. The native inference data above comes from the successful Release harness, not the failed build attempt.

## Production prompt v2 retest

After the parent updated the real production prompt to `lyrics-v2-task-only-ids`, the unchanged coordinator/runner/parser were rebuilt and tested again. The QA harness did not substitute a candidate prompt function.

- English target: first batch returned `[]` twice (11.894 s and 10.438 s). Strict validation rejected both. Total 22.406 s; original document returned; zero translated lines; no cache. The first 12 source lines are already English; the model failed the requirement to copy them into the requested output records.
- Chinese target: four actual batches passed strict validation with IDs 0–11, 12–23, 24–35, 36–47 respectively. Times: 26.651 s, 25.702 s, 28.463 s, 25.934 s (raw results retain full precision). Complete song: 106.797 s, within the unchanged 180-second budget. Thirty-six foreign-language lines gained secondary translations; the final 12 Chinese source lines were preserved as-is. Original timings remained intact; one complete-song cache file was written.
- Cache reuse: equivalent complete document, zero inference callback calls, 0.012188 s measured before recording/printing overhead.
- Native cancellation repeated: 2.0077 s, zero residual prompt files.

Evidence: `.runtime/fullsong-v2-qa/` contains the rebuilt harness, prompts, raw outputs, complete source/result documents, cache, source hashes, and measured JSON. Both targets did not pass, so the requested conditional compact whole-song test has not yet started.

Manual reading also found a lexical issue in the accepted Japanese-to-Chinese batch: source `七つの種` (seven seeds) became `七种种子` (seven kinds of seeds). Structural acceptance is not a translation-quality guarantee.

## Fixed-ID constrained-decoding candidate

The existing native executable advertises `--grammar`, `--grammar-file`, `-j/--json-schema`, and `-jf/--json-schema-file`. An independent QA-only static GBNF forced an array of exactly 12 objects with IDs 0–11 in order and a nonempty JSON string for each text. It reused the failing English-target production-v2 first-batch prompt, model, offline/CPU/context/token/loading arguments. This candidate used the CLI directly without editing the production runner; a QA watchdog retained 60 seconds, 3 GiB RSS and bounded output, sanitized ambient LLAMA/GGML variables, and no interactive input.

Result: normal exit in 26.405 seconds; sampled peak RSS 1,530,654,720 bytes. All 12 requested records copied their English source text. The unchanged production runtime-terminator removal and strict `TryApply` accepted the output, preserving source text/timings and yielding zero unnecessary secondary translations. No extra rows were cropped and empty output was never counted as success.

Evidence: `.runtime/fullsong-grammar-qa/` contains static grammar, direct CLI harness, raw stdout/stderr, timing/memory result and C# `strict-validation.json`. This proves one constrained batch, not whole-song reliability. An equivalent `prefixItems` JSON-schema candidate using the existing `-j` interface is being evaluated separately.

The equivalent `prefixItems` schema (fixed `id.const`, `text.minLength=1`, exact min/max array count, no generic `items` property) also loaded through the existing `-j` interface. It exited normally in 28.741 seconds at sampled peak RSS 1,530,482,688 bytes. The actual C# parser accepted exactly IDs 0–11 and unchanged source/timings. Evidence is in `.runtime/fullsong-schema-qa/`, including `schema.json` and `strict-validation.json`.

## Production v3 typed-batch retest

The parent integrated exact-ID output schemas into production. The new QA harness calls `TranslateBatchesAsync`, passing the actual supplied batch IDs and model hash through `RunAsync(expectedLineIds: ...)`. It does not use the older untyped compatibility wrapper. The planned sequence is standard Q4 English and Chinese whole songs, then existing compact IQ3_S Chinese, with no simultaneous inference jobs. Each successful song is tested for cache reuse, and cancellation is retested. Evidence directory: `.runtime/fullsong-v3-qa/`.

### Standard Q4 result

- en-US: four batches strictly accepted on their first attempts (26.392, 31.791, 54.642, 29.220 seconds). Total 142.142 seconds; 36 secondary translations and 12 target-language originals correctly unchanged; original timestamps preserved; one complete cache file. Cache reuse took 0.004610 seconds with zero inference calls and an equivalent document.
- zh-CN: four batches strictly accepted on their first attempts (26.762, 38.682, 25.387, 25.448 seconds). Total 116.292 seconds; 36 secondary translations and 12 target-language originals correctly unchanged; original timestamps preserved; one complete cache file. Cache reuse took 0.003759 seconds with zero inference calls and an equivalent document.
- Cancellation: 2.0110 seconds, zero prompt files left.

Both tested standard-model whole songs fit within the unchanged 180-second overall budget. The Korean-to-English batch took 54.642 seconds, close to the 60-second call budget; these measurements do not establish consistent latency or performance on other CPUs. Compact IQ3_S Chinese testing then ran sequentially, without concurrent inference.

### Compact IQ3_S result

The existing pinned compact model was used with the production hash-specific EOS compatibility argument. All four Chinese-target batches were strictly accepted on their first attempts (38.944, 36.143, 35.889, 42.938 seconds). Complete song: 154.061 seconds, inside the unchanged 180-second budget, with 36 secondary translations and 12 same-language originals preserved. Original timestamps were unchanged; one whole-song cache file was written. Cache reuse took 0.049091 seconds, made zero inference calls, and returned an equivalent document. Cancellation took 2.0103 seconds and left zero prompt files. Compact English was not tested in this supplemental run.

### Final scope limits

- The three v3 successful songs establish basic native Linux operation of the actual typed-batch coordinator/runner/parser/cache path for this original synthetic fixture. They do not replace Windows native/App UI acceptance, broad translation evaluation, race testing during real song switches, or a complete audit round.
- Twelve native model batches were accepted in v3, with no retries. Explicit IDs and exact output shapes were verified; semantic text swaps are not detectable by the structural parser, as demonstrated earlier.
- Budgets remained unchanged. The 60-second per-call and 180-second song boundaries were not disabled or increased. Successful elapsed times are observations, not promises for all hardware or songs.
- All v3 raw prompts, native outputs, result JSON, complete songs, source hashes and QA harness are preserved in `.runtime/fullsong-v3-qa/`. `standard/` and `compact/` isolate evidence and caches. Builds passed with zero warnings/errors.

## Subsequent semantic review

All 144 source/output pairs from the three v3 songs were actually read. Full per-line evidence, including acceptable wording, material errors, and context-dependent uncertainties, is in `round1-full-song-semantic-review.md`. The clearest major local errors are standard English ID 45 (“leave the silver key” rendered “keep the silver key”) and compact Chinese ID 33 (scarf rendered headband). Other definite losses include the two-knock count, seed count versus seed types, sun omission, and bounded “none of us” becoming unrestricted “no one”.

These strings are present in the native model outputs, before parsing/app display. They are therefore model/prompt capability limits in this evidence, not evidence that the application swapped lines. The application structurally validated the wrong translations as designed; the parser is not a semantic verifier. The earlier v1/v2 formatting failures were a separate generation-contract reliability problem addressed by v3 constrained output. Korean subject omission and Japanese declarative/imperative interpretation are marked context-dependent rather than asserted as explicit source-pronoun reversals.

A subsequent QA-only full-song-background experiment improved two standard English errors but caused eight compact Chinese-target outputs to copy Japanese background verbatim. Both were structurally accepted. Full details and all 24 before/after pairs are appended to `round1-full-song-semantic-review.md`; this candidate is not a general quality fix.

The local-context + generic-fidelity retry also failed to fix the blocking action/object errors: standard 28.215 s and compact 35.834 s, structurally valid but still semantically wrong. Evidence and all lines are appended in the semantic review. No product-source modification was made by these QA experiments.

A final bounded Chinese-task-instruction candidate, motivated by the official model card’s task templates, also retained both serious semantic errors: standard action reversal at 39.819 s and compact scarf→headband at 58.251 s. Compact additionally changed the lamp/lantern to morning light. The candidate was not treated as a release-quality pass; complete outputs and provenance are in the semantic review.

Final diagnostic: each model was tested once on its problematic line alone with the official minimal translation template and no background/JSON/schema. Standard still reversed leave→keep (11.644 s); compact changed scarf to necklace (7.595 s). These exact errors are not explained solely by structured-batch pressure. Known semantic release-quality blockers remain, and candidate testing stopped after the two planned calls. Full raw outputs and interpretation are in the semantic review and `.runtime/single-line-diagnostic/`.
