# Selection semantics remain a release blocker

The full three-mode branch and all non-AI provider/cache/identity/title fixes remain intact. This record reports the actual refined checks; it does not replace or relabel prior evidence, activate a failed protocol in production, accept rule-only scope, or publish Beta11.

## Actual observations

The first refined run 37249426991 failed at harness argument admission before inference. Normal two-path invocation was restored in 55a9362; subsequent Windows compilation and execution succeeded, reusing the exact original AVX2 worker/model bytes without rebuilding the native engine.

| Run / selection task | Cross-script (expected c1) | Wrong version/artist distractors (expected c3) | Missing identity (expected abstention) |
|---|---|---|---|
| 37249590704 / concise Chinese task, reversed candidate order | NONE, 2190.4205ms — incorrect | NONE, 2713.0805ms — incorrect | NONE, 2135.1871ms — correct |
| 37249922967 / explicit missing values and optional corroborating metadata | c1, 3607.0159ms — correct | **c5 (Live)**, 4041.5977ms — incorrect | **c7 (Lionel Richie)**, 3514.151ms — incorrect |

Every output in these two rounds had native `complete=true` and exact full-string syntax. The latest run therefore demonstrates real semantic failures, not just JSON truncation: an explicitly different version was selected, and an unidentified same-title track received an unsupported artist. Correct candidate positions were deliberately changed: c1 was second, c3 third; the insufficient-identity candidates were c7 then c6. Expected IDs never shaped prompt construction. These are hand-annotated metadata fixtures, not real provider captures or broad accuracy certification. A single correct answer does not make the other failures acceptable.

The metadata correction was principled: unknown duration is represented as unknown, not a literal zero-second recording; optional album/duration absence does not automatically veto an otherwise complete identity. No production matching rule was fed back as the model's answer. This correction improves input meaning, but still does not establish useful selection behavior.

Pinned GGUF chat template and correct EOS were verified before experimenting. The helper actually applies the model's template. Native prefill/decode phase timing is unobservable in the exact original binary; measured request-to-frame latency is reported above. Existing 500ms production evidence remains 0/3 completed outputs. The producer/native worker sources, translation prompt and model catalog remain unchanged throughout these experiments.

## Implication and next justified experiment

The current frozen Hy-MT2 1.8B translation model with both attempted task representations cannot yet be relied on as the recording selector. This does not prove that every possible Hy-MT prompting or training approach is incapable; it does establish that neither a longer timer nor syntactic output constraints fixes the currently observed semantics. Increasing token limits, extracting an ID prefix, forcing a valid ID, or declaring every safe fallback an AI success would obscure the user's goal. Further test-specific prompt tuning is not a sound release route.

A defensible next experiment is a general instruction model dedicated to **selection**, while leaving Hy-MT and its established prompt dedicated to **translation**. [Official Qwen3-0.6B documentation](https://huggingface.co/Qwen/Qwen3-0.6B) describes multilingual instruction following and a supported non-thinking mode. This is a suitability rationale, **not** evidence of correct selection. [Official Q8 GGUF](https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/tree/23749fefcc72300e3a2ad315e1317431b06b590a) metadata gives:

* File `Qwen3-0.6B-Q8_0.gguf`, immutable revision `23749fefcc72300e3a2ad315e1317431b06b590a`.
* Size **639,446,688 bytes** (about 610MiB additional disk/download), SHA256 `9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031`.
* Public local CPU inference; no paid API or private song upload. No replacement/model weights were downloaded during this research.
* First diagnosis can reuse the existing verified generic completion executable and production Windows process/job owner. The resident Hy helper's fixed model-size/profile policy would reject this model; it must not be bypassed. A resident selection profile is only justified after the generic diagnosis demonstrates semantic suitability and acceptable latency.
* Keep conservative existing 3GiB process cap and 1GiB host reserve for the initial CPU observation; actual peak RAM/startup/request timing must be measured, not promised. The 0.6B parameter count and smaller file do not guarantee accuracy or a particular speed.
* A production integration would need an explicit selector-model identity/download-consent surface and independent protocol/cache keys. One global owned inference lane should switch/drain models safely rather than introducing competing processes or quietly doubling resident memory. Existing translation behavior and privacy wording stay untouched.

**New model download requires the owner's explicit choice** under the task's no-unapproved-model-download constraint. No Qwen model has been installed, no unverified protocol has replaced production selection, and no production budget has been extended. If authorized, use a small frozen identity/optional-metadata contract with reordered positives, wrong versions, missing-identity abstention and at least one admitted ambiguous source case; preserve raw outputs and do not tune against expected answers. A successful small diagnostic would permit engineering integration, not certify general quality.

For usable production timing, provider translation collection must retain its three seconds and original lyrics must publish first. A separately bounded cancellable selector may then improve the result only while generation/track/settings/model/cache fences are current. The present 2.5-second reserve/shared-deadline implementation needs to be revised when a genuinely suitable selector is integrated, rather than spending source lookup time on an AI deadline. Cold preparation, maintenance, translation priority and rapid skips must share a clear owner; no final selection may overwrite a later song. This plan is not silently implemented while semantics remain failed.

## Retained exact evidence

* Run 37249590704 / head 55a93621386d46f5244dfd73ad0924fb12228918: artifact 11320336660, 18,409 bytes, ZIP SHA `3fac832c7a384e55307a55630b0197aa151095dc46c685aa616411e29aae6dab`.
* Run 37249922967 / head 9e60861efa7ff94fcf8682f6ff83089e46dcdd9a: artifact 11319858767, 18,605 bytes, ZIP SHA `3bef798dc67c3adf47d2527147b41e311b9ea8f43bd3a196845773d5dc740433`.
* Original model SHA `5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4`; actual AVX2 worker SHA `efbd796e9518198fc374dd37aa44928e97f775c9e7a76099d3add95ca7679360`.

ZIP SHA/size were checked after authorized downloads and the original evidence JSON is retained under `artifacts/beta11-refined-selection-37249590704/` and `artifacts/beta11-selection-missing-metadata-37249922967/`. No old tests were rerun; no native build, new model, paid service, local laptop, scope acceptance or publication occurred. Further model approval, meaningful inference and production lifecycle integration remain blockers.
