# Qwen4 deterministic selection control

The corrected invocation now produces complete, official-EOS answers, but the evaluated 4B configuration still fails identity confirmation. Zero temperature did not resolve unknown-artist acceptance or cross-script candidate-position dependence. This does not establish that no possible 4B training or instruction could work; it does establish that the evaluated deployment route is not ready to ship. No further prompt rewrites against these expected answers were performed.

## Exactly one variable

[Run 37275610079](https://github.com/airanluo-dot/DropSpace/actions/runs/37275610079) used head `5ccda30e36089f7bbeccca06938db15494c77832`. It retains the pinned 4B model/runtime, byte-preserving `-bf`, official bare assistant prefix, special-token telemetry, context/output budget, sampler parameters and frozen corpus. Temperature alone changes 0.7 to 0. The original corpus SHA is `8de7e3c74bcac182d894c8c5f0fadc48ceebbb9851a10086331e20784ba1e2ee`. Every prompt byte and normalized inference argument was compared with its earlier binary counterpart; only the temperature differs. Three binary control calls and twelve independent calls were executed, without rerunning the defective text transport.

All fifteen calls terminate with `<|im_end|>`, native exit 0 and confirmed owned cleanup. None is empty or truncated. Ten decisions match the predeclared expectation, nine of twelve in the independent cohort. This is a focused diagnostic count, not a general accuracy estimate.

| Relevant boundary | Temperature 0 result | Interpretation |
|---|---|---|
| Artist missing, Hello with two different artists | c0 | Unsupported acceptance persists |
| Reversed Jay Chou / 周杰倫 alias | NONE | Positive identity relation still not recovered |
| Former empty Ed Sheeran answer | c1 | Fixed binary template remains useful |
| Incomplete Post Malone / Swae Lee credit | NONE | Now satisfies the conservative complete-credit fixture contract |
| Michael Jackson / 迈克尔·杰克逊, original / reversed order | NONE / c1 | Candidate-position dependence persists even without sampling |
| Artist missing, Stay with one candidate | c0 | Being the only candidate does not prove the target artist |
| Artist absent in novocaine candidate | c0 | Album/duration cannot establish missing artist identity under this contract |

Other exact identities, complete reordered credit, explicit wrong-version rejection, two-candidate Stay abstention in both orders and escaped metadata-control-token case succeed. Incomplete-credit is a conservative metadata-contract question, not proof that a lead-only catalogue entry describes a different real recording. The wholly unknown-artist and independent-alias failures remain material without relying on that disputed boundary.

## Resources and evidence

This runner exposed AMD EPYC 9V74, 2 cores / 4 logical CPUs. Fresh-call wall times were **10.925–16.238 seconds**, prompt evaluation **9.012–14.109 seconds**, sampled peak RSS **4,680,982,528 bytes** (about 4.36GiB). Different hardware and system conditions mean these times cannot be credited as a controlled regression caused by changing temperature. Residency has not been measured and cannot promise to remove prompt computation.

Artifact **11330162796**, `ai-selection-long-diagnostic-37275610079-1`, is **97,820 bytes**, ZIP SHA256 `68227878904dca94309d1972ba92bc676f74f32538c4c624c8b6c3be02847715`. ZIP size/hash/paths were checked before extraction; unchanged original, corpus, prompts, effective input, raw output/performance, arguments and per-call results remain under `artifacts/beta11-qwen4-deterministic-37275610079/`. The diagnostic project compiled once for the changed temperature switch, with zero warnings/errors; Windows execution passed. Public immutable model caching is isolated to diagnostics, rechecks exact size/hash on reuse, uses an exact cache key and never supplies production release inputs. A cache hit was not presumed.

The [independent engineering fixes](beta11-selection-engineering-progress.md) at `4bc38a5` remain separate from model qualification. A subsequent per-model GPU failure record prevents operational selector/translator switching from retrying an already failed backend each song. Only a real GPU preference change invalidates those records, including off/on with no intervening inference; same-value configuration writes do not. One additional real fixture-process check verifies cleanup before every replacement and per-profile fallback state. The completed relevant checks total one Core, eight Infrastructure and one linked actual App-service check; no whole suites or already passing filters were repeated. One exploratory local-LRC fixture omitted mandatory bound identity and failed; its speculative change/test was withdrawn, and no local-LRC fix or passing check is claimed.

## Reviewable alternative

The next supported model comparison is official [Qwen3-8B Q4_K_M](https://huggingface.co/Qwen/Qwen3-8B-GGUF/blob/7c41481f57cb95916b40956ab2f0b139b296d974/Qwen3-8B-Q4_K_M.gguf): **5,027,783,488 bytes**, SHA256 `d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785`, Apache-2.0. This is 17.46% more download than the evaluated 4B Q8, despite twice the parameters. The [official base model](https://huggingface.co/Qwen/Qwen3-8B/blob/b968826d9c46dd6066d109eabc6255188de91218/README.md) supports non-thinking via its empty thinking block; its template differs from 2507 4B and must also preserve trailing newlines.

The fixed engine has Qwen3 architecture and Q4_K implementation, so there is source-level compatibility evidence, not a verified 8B load. At 2048 context, FP16 KV is approximately 288MiB before other buffers. An **8GiB diagnostic process cap plus 1GiB available host reserve** is a prudent provisional allowance; measured usage should decide the product requirement. CPU prompt computation may be slower and is a material tradeoff. GPU, actual selector quality and production residency remain unverified. Neither size nor a working loader qualifies identity selection.

The exact inactive proposal is retained in `selector-candidate-qwen3-8b.json`; it is not embedded in the executable profile catalogue, selectable in workflow dispatch or enrolled into the app. No 8B weights were downloaded. Existing explicit download consent named 4B; the parent can obtain resource authorization for this additional 5.03GB/8GiB route before running it. Translation models, frozen Hy prompts, privacy wording and reviewed-source release override remain unchanged. Beta11 has not been published.
