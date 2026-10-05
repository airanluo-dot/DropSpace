# Qwen dual-use diagnosis: translation is not suitable under the evaluated protocols

The owner authorized a dual-use evaluation at 2026-10-05T01:30:22Z, Sentinel `4279b83aa968819192c15d5f5516c3ab`. The later clarification was **add an optional model, keep the existing two models and user choice**, not replace either. If translation is unsuitable, report the evidence and discuss before exposing it for AI lyrics. No production Qwen model option/default/distribution change was made.

## Actual model output, not a format score

The initial run used unchanged `LyricsTranslationPrompt.Build` / output schema with the official Qwen non-thinking template and recommended sampler. It requested three lines into Simplified Chinese, three into English, with one background line per group. Six model outputs were exact source copies, despite EOS and valid JSON IDs. `LyricsTranslationOutput.TryApply` preserved originals/timestamps and correctly left copied lines without useful secondary translations. **Structural validity is not translation success.** No useful target-language translation was generated.

To separate the task representation from the model's capability, a second diagnostic transferred the unchanged shipping `PlainHyLyricsProtocol.BuildPrompt` onto Qwen for three discriminating lines. It used the same source/target as the actual Hy production runner baseline, no grammar or new prompt wording. Results:

| Source / target | Actual Hy output | Actual Qwen plaintext output | Semantic assessment |
|---|---|---|---|
| I didn't say I never loved you. → Chinese | 我并没有说从未爱过你。 | 我没有说我对你说过没爱过。 | Qwen adds another reported utterance, “我对你说过”, changing the nesting/meaning. Hy preserves the original denial. |
| 君が帰らなくても、私は窓を閉めない。 → Chinese | 即使你不回来，我也不会关上窗户。 | 如果你不回去，我不会关窗。 | Qwen loses the concessive “even if” and changes return perspective; Hy retains the concession. |
| 别把灯递给我，给她；我会在门外等。 → English | Don’t pass me the light, give it to her; I’ll wait outside. | Don't give the light to her, and I will wait at the door. | Qwen reverses the person/negation: the source says give it to her, Qwen says do not. This is a material contradiction, not a fluency preference. |

The other Hy baseline lines preserved the three-returned/fourth-not-taken quantities and the moonlight/empty-cup metaphor. Its isolated “I let it go.” became “我放弃了。”; the group background describes a caged bird, so a contextual translation would be “我把它放走了”. The shipping Hy per-line protocol intentionally has no surrounding context, while the initial Qwen JSON protocol did. This exposes a real context boundary in the current baseline, **not** a controlled proof that Hy weights intrinsically cannot use context. Both production prompt sources remain unchanged.

These are original synthetic QA/previous holdout lines, not claimed provider captures or whole-song certification. Codex reviewed the actual source/output meanings above; there is no BLEU/fluency-only approval, no syntax-derived quality approval and no owner/reviewer identity fabrication. The person/negation reversal is sufficient to decline the current Qwen profile for AI lyrics. Do not expose this model as a lyrics translation option merely because its selector has limited positive evidence. Further discussion would be needed before any alternative translation route or additional qualification; no repeated prompt tuning was used to conceal the result.

## Timing, mapping and shared ownership

The actual Hy production CPU resident runner handled six plaintext lines at 1.689–2.237 seconds per request (the first includes preparation). It was explicitly drained via `DrainCleanupAsync` before starting Qwen. The Qwen contextual JSON calls took 6.491 / 6.024 seconds including fresh model load; sampled RSS was 1,197,187,072 / 1,196,453,888 bytes (about 1.11GiB with a 4096 context). The Qwen plaintext calls took 7.768 / 5.869 / 2.507 seconds; RSS about 1.10GiB. These ran on separate cloud hosts (EPYC 7763 for the first comparison, Xeon 8573C for transfer), so per-call latency is observed evidence rather than a controlled architecture speed claim. The earlier 2048-context selector used about 0.89GiB; storage remains 639,446,688 bytes (~610MiB).

JSON IDs were exactly 1,2,3 and host-applied original text/start/end remained unchanged. Plaintext results are mapped by the host's requested index; the model never generates timestamps. Correct line mapping does not repair the meaning errors.

A small **diagnostic topology**, not a production Qwen integration, held one existing inference semaphore through owned cleanup. An actual Qwen process was canceled after 500ms, before first stdout; it reported caller cancellation, confirmed cleanup in 6.04ms, and a queued waiter canceled without creating a native process. Only then did a follow-up selector process start and complete with `NONE` in 1.785 seconds. This establishes owned startup cancellation and admission recovery, not generation-time cancellation or a correct song decision. No simultaneous Hy/Qwen process residency was used. Real shared resident Qwen selection/translation priority, rapid-skip UI behavior and cancellation during generation remain unverified; production integration still needs the one-owner lifecycle/fences described in the [selector readout](beta11-qwen-selection-diagnostic.md).

## Immutable evidence and actual checks

* Dual-use run [37252158831](https://github.com/airanluo-dot/DropSpace/actions/runs/37252158831), head `0559a529f615890e7e0f2d925fdf567c0df040a3`. Artifact 11320479529, 36,430 bytes, ZIP SHA `3f3682264d4511a38a8fcde1afbb0d399b5549c377acc4a27e296111577214ad`.
* Plaintext transfer run [37252518034](https://github.com/airanluo-dot/DropSpace/actions/runs/37252518034), head `df870e66879d3a429af667af0488412b1cae60f8`. Artifact 11321252547, 27,703 bytes, ZIP SHA `a3dbcde6ae13f728affd929d34c09f00ea6987c25c7c945252609987d121d156`.
* Exact official Qwen hash `9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031`; actual Hy hash `5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4`; original producer manifest hash `26162299cd270bf63f25181e960b3139cfe20421eecdc9da44cea99dfb1711ba`. The model/helper validation policies were not bypassed.

Downloaded archives were verified for size/SHA before safe extraction and retained unchanged under `artifacts/beta11-qwen-dual-37252158831/` and `artifacts/beta11-qwen-plain-transfer-37252518034/`. The new diagnostic code compiled with no warnings/errors; actual Windows execution completed. This stage made thirteen focused calls (six Hy lines, two Qwen JSON batches, one canceled startup, one recovery selection, three Qwen plaintext lines). No full or unrelated suite, native rebuild, laptop, paid API, model replacement, production Qwen enrollment, translation-prompt/privacy edit, reviewed-source override edit or publication occurred.
