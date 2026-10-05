# Qwen8 identity selection comparison

The official 8B model loads and follows the verified non-thinking template, but the evaluated selection configuration is **not qualified for production**. It improves some missing-artist and cross-script cases while still accepting a candidate with no target artist evidence. A valid loader, a larger model or a constrained output format cannot establish correct recording identity. No 8B model was enrolled into the application, and Beta11 has not been published.

## Bounded inputs and authorization

[Run 37278278637](https://github.com/airanluo-dot/DropSpace/actions/runs/37278278637) used diagnostic head `caf02486d2e022ad316d4f619ae0a8df1098146c`. The parent confirmed owner approval at 03:18:58Z (`335d29085ba48191953f25ee395d415a`) and subsequent full autonomy at 03:19:22Z (`d98b65bb93f8819186605036960cf431`). No additional permission was requested. Only the official public model was downloaded; there were no paid calls, provider queries, cookies, mirrors or credential workarounds.

Official `Qwen/Qwen3-8B-GGUF`, revision `7c41481f57cb95916b40956ab2f0b139b296d974`, file `Qwen3-8B-Q4_K_M.gguf`, **5,027,783,488 bytes**, SHA256 `d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785`. Actual download and verification took 69.992 seconds, `reusedPresentFile=false`. The exact public-weight cache is isolated to diagnostics and rechecks size and hash. Both prelaunch verification and the diagnostic executable verification succeeded.

The pinned base tokenizer template at `b968826d9c46dd6066d109eabc6255188de91218` and official GGUF metadata template were actually rendered with Jinja, `enable_thinking=False`. For this single-user path both produce the same assistant prefix plus `<think>\n\n</think>\n\n` with actual LF bytes, despite different full historical template texts. GGUF declares architecture `qwen3`, EOS 151645, BOS 151643 and `add_bos=false`. There is no double-escaping defect. The metadata-only local evidence ends after all 28 KV entries at offset 5,932,881; no additional model tensor download was required for this audit.

All fifteen calls use `-bf`, so effective input bytes equal stored prompt bytes. Frozen corpus SHA256 remains `8de7e3c74bcac182d894c8c5f0fadc48ceebbb9851a10086331e20784ba1e2ee`, byte-identical to the corrected 4B run. Every corresponding effective prompt was compared: the only input difference is the official 8B empty thinking block. Normalized arguments are identical to the 4B temperature-0.7 binary arms; model paths and the separately recorded 6GiB-to-8GiB job allowance differ. Official non-thinking sampling is temperature 0.7 / top-p 0.8 / top-k 20 / min-p 0, context 2048, output 32 tokens, CPU 4 threads. No prompt rewrites or annotation hints were added.

## Actual decisions

Fifteen calls return native exit 0, official `<|im_end|>`, full output and confirmed owned cleanup. No inference/time/output/RSS failure fired and none is empty. Twelve answers satisfy the strict ID/NONE grammar. Ten satisfy the complete predeclared contract, eight of twelve independent calls. These are focused fixture counts, not a population accuracy estimate.

| Boundary | Observed output | Assessment |
|---|---|---|
| Hello, target artist absent, two candidates | NONE | Previously unsupported 4B acceptance is corrected here |
| 晴天 / Jay Chou against 周杰倫 | NONE | Known positive alias still missed |
| Don't / Ed Sheeran, optional metadata absent | c1 | Correct |
| 東京フラッシュ / Vaundy | 答案：c1 | Intended ID is present, complete protocol is invalid |
| Michael Jackson / 迈克尔·杰克逊, original / reversed order | 答案：c1 / c1 | Semantic relation appears in both, only reversed output satisfies the protocol |
| Stay, target artist absent, two candidates, both orders | NONE / NONE | Correct abstention |
| Stay, target artist absent, one candidate | c0 | Unsupported identity acceptance; only-candidate status is not artist evidence |
| novocaine candidate artist absent | NONE | Previously unsupported 4B acceptance is corrected here |
| Metadata containing literal template markers | 答案：c1 | Complete output is invalid; metadata remained escaped data |

Exact Love Me / JMSN, reordered complete Sunflower artist credit, conservative incomplete-credit abstention and explicit acoustic/studio/live conflict rejection also succeed. Incomplete collaboration credit remains a conservative fixture contract, not proof of an incorrect actual catalogue recording. That disputed boundary is not needed to establish the wholly unknown target-artist failure.

The three prefixed outputs were not salvaged by searching for a partial ID. A future grammar constraint could address representation, but would not demonstrate correct abstention for the single unknown-artist candidate or fix the missed Jay Chou relation. The independent Michael Jackson pair differs in formatting rather than selected semantic identity; this run does not establish an 8B semantic candidate-order bias. It also does not establish that all possible 8B prompts or training fail. The evaluated deployment route remains unqualified under the contract fixed before inference.

## Resource observations and retained evidence

Windows Server 2025 runner, AMD EPYC 9V74, 2 exposed cores / 4 logical CPUs. Fresh-call wall time **13.546–17.631 seconds**, native prompt evaluation **8.400–11.911 seconds**, peak RSS **8,441,286,656 bytes** (about 7.86GiB), peak private bytes **4,133,789,696** (about 3.85GiB). RSS and Windows private/job memory are different metrics; mapped model pages contribute to RSS. The diagnostic enforces both its 8GiB allowance and 1GiB available host reserve. These measurements use the fixed engine defaults, including `n_batch=2048`; they are not a universal model RAM requirement or a measured production residency profile.

Every observed cold call exceeds the separate twelve-second product snapshot budget. Warm residency was not measured and cannot remove prompt evaluation, which alone approaches that budget on these small metadata fixtures. The existing experimental helper and host also retain their two 500ms request caps; extending only one cap would not make this engine/model/profile usable. Full production priority prompts, ordered confirmed IDs, warm cancellation and actual GPU/owner behavior still need a separately qualified integrated profile. No latency or real-device guarantee is inferred from this cloud CPU run.

Artifact **11331740889**, `ai-selection-long-diagnostic-37278278637-1`, **98,539 bytes**, ZIP SHA256 `74bae88cc030747e041529d1c13403595b4913b3ddca7a5bc0651eab983583c4`. ZIP size/hash and safe paths were verified before extraction. Unchanged archive, corpus, complete effective inputs, raw stdout/stderr, arguments, model identity, performance and cleanup remain under `artifacts/beta11-qwen8-evidence-37278278637/`. Diagnostic compilation passed with zero warnings/errors; the Windows diagnostic workflow succeeded. Workflow success means completed observations, not semantic approval.

## Engineering retained independently

[Retirement fix c168fc2](https://github.com/airanluo-dot/DropSpace/commit/c168fc2) closes the independent service/cache-generation windows. One new deterministic barrier test passed; actual App service compilation passed with zero warnings/errors. Previous passing checks were not repeated. Source preview, shared source priority, cache migration, bounded weak-candidate collection, independent selector/translator roles, one owned resident lane and per-model GPU failure state remain intact. The native full-three-second window and immediate strict-original preview remain intact.

Translation still uses the two existing Hy models with unchanged prompts. Privacy wording, version number and reviewed-source release override are unchanged. No further model growth or answer-targeted prompt rewriting was performed. This report preserves a technical qualification blocker rather than substituting a rules-only selector or claiming upstream rate limits are solved.
