# Explicit cloud diagnostic producer

This branch adds `produce_selection_runtime=true` to the existing trusted `release.yml`. It is **manual and nonpublishing**. Mixing `publish=true` or translation evidence capture fails immediately. Normal CI and release validation still retrieve the reviewed runtime; they never select this build job. No approval, expiry, override, version, model catalog or translation prompt is rewritten.

Parent trigger after reviewing/integrating this source:

```sh
gh workflow run release.yml --ref fix/beta10-chinese-lyrics-regression \
  -f publish=false -f capture_ai_evidence=false -f produce_selection_runtime=true
```

The producer checks out frozen Beta10 `4044baed6b1c393a815b91e1406a83e0995f1f94` separately and runs its unchanged approved-artifact retrieval. The historical approval remains subject to its existing validity/expiry checks (currently expires October 11, 2026); failure has no alternate download or approval bypass. That approval establishes only the old bytes' reuse, never approval of the new branch. The three legacy completion/tokenizer files must match that verified inventory exactly.

Because no static libraries or CMake trees exist in the old artifact, the explicitly requested producer fetches the unchanged immutable engine revision and builds static engine dependencies for **only three worker targets**, once in each fresh baseline/AVX2/Vulkan directory. It does not build completion/tokenizer targets. This is not incremental reuse of existing engine objects. Ordinary automation continues to perform no engine rebuild. An existing trusted native build-tree cache is not introduced by this change.

Limits: Windows Server 2025 runner; read-only contents/actions permissions and existing Actions token; 90-minute overall producer, 15-minute probe step/12-minute probe cancellation budget; Vulkan dependency concurrency 2 and native build concurrency 4. Official model download is 1,908,528,192 bytes, with the catalog SHA verified through `AiModelPackageService`; production CPU memory admission and resident limits apply. No additional paid service, secret, GPU driver install, laptop or private user dataset. Standard runner availability/quota and public downloads remain operational dependencies.

Artifacts:

* `ai-candidate-runtime-RUN-ATTEMPT`: exact eight-file payload, new worker hashes and source hash, unchanged manifest identity/profile/protocol fields, actual producer run/head/checkout identity. It is a candidate runtime, not an approved release runtime.
* `ai-selection-evidence-RUN-ATTEMPT`: producer receipt including exact legacy provenance, reused byte identities, final inventory, manifest/source/support-file hashes; probe identity, three annotated fixture results, actual sent-request cancellation/drain and next warm preparation, or execution failure. Workflow artifact ID/archive digest are included in probe identity. The complete Actions run must succeed before it is eligible as a trusted producer; uploaded bytes alone do not prove success.

The three metadata fixtures are independently annotated before inference: traditional/simplified whole title and artist; same-title wrong artist/live version; insufficient identity requiring abstention. They contain no lyrics, aliases or fabricated durations. Deliberately excluded alternatives exercise model/host rejection and are explicitly **not** claims about production provider candidate admission. Unique IDs/queries avoid successful decision reuse. Production selector budgets remain 500ms: raw output, sent flag, timing, backend, outcome and fallback are separate fields. Incorrect answers/timeouts are retained without claiming semantic approval; failure to exercise cancellation or regain preparation returns a failed probe status.

Cancellation is triggered by a production runner notification **after successful pipe write/flush**, then production cleanup is awaited. This proves an actual sent-request cancellation rather than canceling before work starts; it does not prove native token decoding had begun. The next permitted warm preparation demonstrates gate release. The probe is CPU-only and does not validate Vulkan, baseline versus AVX2 equivalence, 7B, translation quality, rapid real media switching or all artists/languages. All three scenarios timing out establishes no usable selector inference. Raw output exists only when the native worker completed a response within production limits.

Local cloud checks are compilation and workflow/script structural checks, not a native/model execution. The parent triggers the workflow and integrates evidence, final source/manifest/artifact bindings and owner approval before any Beta11 publication.

## Follow-up diagnostic and title comparison

Original run 37235696457 remains immutable: all three production-budget requests sent but returned no complete answer; cancellation/drain/reprepare succeeded. Its `ai-selection-runtime` name does not pass the existing publication artifact contract. Future producer uploads use the already required `ai-candidate-runtime` naming; the general validator is unchanged.

The additional explicit `diagnose_selection_runtime=true` mode downloads exactly the original runtime/evidence IDs, ZIP sizes and SHA256s and checks their original successful release workflow run, head, attempt, eight-file inventory and exact manifest SHA. This **one pinned diagnostic input set** does not grant a general alternative-name exception, create a release contract, rewrite a producer, or upload replacement runtime bytes. There is no native build/dependency setup in the diagnostic job. It reconstructs the same three prompts and rejects any prompt-hash change before inference. CPU-only requests receive at most ten seconds outside production; the 500ms failure records are included verbatim. Model/worker file leases, production Windows job/process owner and memory admission remain in force. Only the .NET diagnostic harness is compiled.

```sh
gh workflow run release.yml --ref fix/beta10-chinese-lyrics-regression \
  -f publish=false -f capture_ai_evidence=false \
  -f produce_selection_runtime=false -f diagnose_selection_runtime=true
```

The exact old helper exposes only a ready handshake and a final response, with native logs muted. The observation records request-to-frame timing, complete flag, raw text, parse/expected comparison and native exit state; **prefill/decode split remains unobservable** and is recorded as null. It cannot invent those timings without instrumenting a different binary. A 10-second result is not evidence that production 500ms works, and no budget or translation prompt changes are made. A failed diagnostic still uploads evidence.

Generic matching correction: comparable titles and bilingual Chinese title bases now use the same pinned unambiguous character folding as artist credits. Search requests/raw metadata/version labels are preserved; wrong-artist, language-version, live/remix and duration conflicts still reject. One new focused test covers two independent title pairs, language/live conflicts, wrong artist/duration and an ambiguous dictionary mapping; it passed, and earlier passing tests were not repeated.
