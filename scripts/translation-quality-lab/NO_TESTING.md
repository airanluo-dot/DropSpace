# 【不准测试】

The latest explicit user instruction supersedes the earlier experiment plan:
“请取消99%的测试不准测试，直接标注不准测试”。

Effective immediately: do not start or repeat unit/integration/regression tests,
model comparisons, benchmarks, trial runs, evidence validation, or test CI. Do not
relabel tests as compilation. Existing source and already-finished evidence remain.
Only implementation, manual review, integration, necessary compilation/packaging,
and handoff are authorized. Model/testing entry points in this isolated directory
are disabled. No production source or CI workflow was changed.

Completed before this instruction: official model downloads/full-file verification,
one llama.cpp build, Hy 1.8B Q8/Q4 and two separate context arms, stopped Hy7B
none-mode attempts, CT2 conversion/inference, original protocol checks.

Not executed: Hy7B mmap diagnostic, the new preface identity test, the new evidence
validator, any final test rerun or test CI. TranslateGemma additionally requires
user-controlled license/account access. Post-instruction report work reads only
existing evidence and records manual judgments/arithmetic; it is not a new run.
