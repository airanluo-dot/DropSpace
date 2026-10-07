# Beta18 round 2 — integration and release review

The second complete App pass used immutable source commit
`729d5b179f34ab2f852e7975f129431f77e8dc7a`. The four partition owners freshly
read all 425 production text/config/data files (84,869 physical lines) before
round-2 production fixes began. Separate scope lists, the per-file manifest and
fresh partition reports preserve the actual coverage; round 1 evidence was not
reused as round 2 coverage.

Root additionally reread the complete Beta18 producer/validation and promotion
scripts, the entire 931-line source/model approval gate and 192-line runtime byte-binding module, the current corrective-release preparer, the active isolated CI lane and
required outcome gate, and release publication/promotion/skip paths. These are
supplemental release checks rather than part of the 425-file App denominator.
The final-main path runs exactly eight selected Core cases, verifies their real
TRX, produces packages once, and promotes the same bytes without broad fallback.
The sole isolated installer payload install/uninstall is conservatively budgeted
as one more scenario. No case has executed during these reviews.

The supplemental native source read covered all of
`tools/plain-lyrics-helper/main.cpp`, `gpu-policy.h`,
`tools/cuda-lyrics-helper/cuda-device.h`, `tools/cuda-lyrics-helper/adapt_worker.py` and
`tools/ct2-helper/helper.py`, including the initially truncated C++ middle range reread separately.
Both native helper CMake files and the CT2 build script/dependency/package schemas were also read completely. No new source-confirmed helper defect was found. This is textual inspection,
not a worker build, model/GPU execution, or linguistic validation. The existing
reviewed native runtime bytes and independent CUDA archive remain unchanged.

Root implemented R2-INF-01 after all partitions completed their full reads:
independently bounded, awaited five-second best-effort notices preserve the local
pairing rejection/cancellation while leaving transfer deadlines unchanged.
The Infrastructure report records the source evidence and fix disposition.
The completed Core report records target-language completeness correction; UI and
Services reports record their separately owned findings and actual dispositions.

Checks here are source/caller/diff inspection and metadata reads only. The latest
public App Beta remains `v0.3.1-beta.17`; release numbering will be rechecked before
preparation/publication. No tests, builds, native probes or performance experiments
were executed for this round. Hosted compilation and the final focused execution
remain future verification, not claimed results.
