# Exact-worker longer observation — still no valid AI decisions

Run [37238360488 / attempt 1](https://github.com/airanluo-dot/DropSpace/actions/runs/37238360488), head `2d1e392db3a847a7ff237949a4b5fcfdc7bc67d6`, completed its explicitly nonpublishing diagnostic. Native engine was not rebuilt. The exact original eight-file ZIP/inventory/manifest and producer facts were verified; original producer remains run 37235696457, head e81e0d55. The three prompt SHA256s match the original 500ms experiment exactly. Downloaded diagnostic artifact 11316617405 has 17,824 bytes and ZIP SHA256 `1d75cf9df4f8b85fad1635f2fe481eef72388e99e354e57013070ecad7b6ac3e`.

| Fixture | Expected | Request to response | Native response | Valid answer |
|---|---|---:|---|---|
| 小幸运 / 田馥甄 | c1 | 8856.406 ms | Begins `{"id":"c1","p":"NetEase","t":"小幸運"…`, incomplete candidate metadata | No |
| Hello / Adele / 25 | c3 | 9841.1614 ms | Begins `{"id":"c3","p":"NetEase","t":"Hello"…`, incomplete candidate metadata | No |
| Hello / unspecified artist | null | 8465.9549 ms | Begins `{"id":"c6","p":"NetEase","t":"Hello","a":"Adele"…` | No |

All three are actual model outputs in protocol-2 frames with `complete=false`, at the helper's existing 32-token output ceiling. They repeat the first supplied candidate metadata instead of finishing the independent single-ID JSON contract. The first two prefixes happen to contain expected IDs; they do **not** establish correct selection, and the abstention case begins selecting an unsubstantiated artist. Host parsing correctly rejects all three. No truncated JSON or regex-extracted ID is accepted.

Warm preparation was 1251.0577 / 1350.8838 / 1310.9288ms outside the diagnostic request windows. CPU AVX2 worker SHA256 is `efbd796e9518198fc374dd37aa44928e97f775c9e7a76099d3add95ca7679360`; official 1.8B Q8 model SHA/size are verified through the production package service. All three owners completed cleanup. The exact binary exposes no prefill/decode counters or phase boundaries, so those durations remain unobservable/null, not guessed from end-to-end time. This is not GPU or 7B evidence.

The earlier production experiment remains 0/3 completed outputs at 500ms, with successful sent-request cancellation/drain/reprepare. Longer observation establishes two issues in this current CPU setup: inadequate production latency **and** noncompliant output/abstention behavior. Extending a timer alone would not produce three valid decisions. The sample is too small to establish broad quality or universal behavior, but is sufficient to block a claim that these modes have passed.

A later selection-specific constrained-output or task/model design would need independent semantic evidence, including reordered candidates and abstention; syntactic constraints alone cannot establish correct identity or usable latency. No such change, translation prompt change, larger production budget, or new native rebuild is part of this diagnostic. Keep public AI modes paused until an explicit owner decision and justified correction. Original-first/asynchronous improvement is a separate product/budget decision, not silently implemented here.

The separate generic title defect is fixed in `fc672e4e4bc3f0dd6bdfec63f7c84c712fb9f44b`: comparable titles/bilingual bases use the same unambiguous orthography as artist credits. One targeted test passed while retaining wrong-artist/duration, live, language-version and ambiguous-mapping rejection. No source-specific artist exception or added network variant was introduced.

Future producer names now use the existing required `ai-candidate-runtime` contract. The old differently named artifact is not relabeled and still does not pass the normal release binding validator. This diagnostic accepts only that exact immutable input set for observation; no general publication exception was added. Source fingerprint at the diagnostic head is `17a8051488a4c474946654b28a1dca7e834cd75034d02ef4b6c14b97b65fc714`; final integration must recompute it.

Local retained evidence: `artifacts/beta11-selection-long-diagnostic-37238360488/` and the original `artifacts/beta11-selection-evidence-37235696457/`. The readout does not alter either original evidence envelope, approval, model catalog, release version or native worker.
