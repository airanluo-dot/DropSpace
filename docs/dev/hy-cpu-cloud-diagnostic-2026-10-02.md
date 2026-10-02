# Bounded cloud CPU diagnostic — 2026-10-02

This diagnostic completed 16 old-fixture samples with the default 1.8B model. The optional
7B model was stopped by the predeclared shared-memory safety threshold before producing its
first translation; its remaining 15 samples were not started. There is no complete paired
comparison and no conclusion about 7B translation quality, Windows loading or GPU support.
The user's computer was not accessed. No retries or threshold increases were performed.

## Frozen input and execution

The run was prepared against candidate `49a9ac13dd3d596972d5ac8cabc8d7304171b584`, tree
`37f16c4f44de95daca66dc4c3b23d5c0324341d7`. It does not validate the subsequent admission,
cancellation or UI fixes. Execution began at 13:58:25 UTC and ended at 13:59:18 UTC.

- Inputs: 12 existing source48 screen cases (six into English, six into Chinese), plus
  four existing holdout cases. These are old known fixtures, not unseen evaluation data
  or the user's songs. Each model had the same 16 frozen prompt files and sample order.
- Manifest SHA-256: `4bccdb2fc55d15609a7b0debb6847a2a2f8b4472bee66596769695694ac0def4`.
- Runtime: official llama.cpp `7fe450e19305b828c199d602c23a8337aaa1f03b`, GCC 14.2.0,
  CMake 4.4.3, Linux CPU AVX2/FMA/F16C `llama-completion`. Binary SHA-256:
  `2e27914b5f8e336bd6acc6b664b9903e3d2fb5e01a1dd45c23e76558c4384249`.
- Four inference/batch threads; CPU quota four cores. Every sample starts a fresh process
  with seed 42, context 4096, output limit 2048 and the existing fixed prompt/sampler.
  Exact argv and environment are retained for every started sample. File-page caches were
  not cold-cleared, so equivalent cache starting states are not claimed.
- Limits fixed before execution: 60 seconds per sample, process RSS 3 GiB for 1.8B and
  12 GiB for 7B, shared cgroup memory.current stop at 14 GiB. The cgroup limit was 16 GiB.
  Cleanup was bounded and all 17 started process groups were confirmed gone.

Both official pinned models were downloaded once and fully SHA-256 verified before use:

| Model | GGUF bytes | SHA-256 |
| --- | ---: | --- |
| `hy-mt2-18-q8-plain-beta` | 1,908,528,192 | `5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4` |
| `hy-mt2-7b-q8-plain-beta` | 7,981,928,896 | `58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0` |

Each retained its own official embedded chat template. The 1.8B template is 654 bytes,
SHA-256 `b7491ec0e9c869dfce20f2176758099bf248d979dd05530ede99deb21698acee`, BOS 120000,
EOS 120020. The 7B template is 662 bytes, SHA-256
`788ac16c5d7bfefc28655928ad524c8f378a44cb24d24fb125d6a5859b167677`, BOS 127958,
EOS 3, EOT 127960. Identical user-prompt bytes therefore do not imply identical tokens;
this is a package comparison, not an experiment isolating parameter count alone.

## Observed results

| Model | Planned | Started | Completed | Safety-stopped | Not started |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1.8B | 16 | 16 | 16 | 0 | 0 |
| 7B | 16 | 1 | 0 | 1 | 15 |

1.8B wall time summed to 44.722164 seconds, median 2.409884 seconds, range
1.994408–7.953753 seconds. Its maximum kernel-reported RSS was 2,233,143,296 bytes.
These include a new process/model session per sample and are not production resident-line
latency measurements.

The 7B first sample (`source48-38-to-en`) was stopped after 1.673422 seconds when the
shared cgroup reached 15,152,271,360 bytes, above the 15,032,385,536-byte threshold.
The child's maximum kernel RSS was 3,582,099,456 bytes; stdout contained zero bytes.
The driver issued SIGKILL and confirmed cleanup. OOM, oom_kill and oom_group_kill event
counters stayed zero. The shared reading includes file cache and other processes; it is
not the 7B model's own peak allocation. The 15 unstarted samples are neither passes nor
failures. The stop establishes neither complete loading nor successful generation.

An open-label, per-sample semantic review of the 16 available 1.8B outputs recorded
12 acceptable cases, three warnings and one uncertainty. The warnings retain the actual
differences: leaving a silver key became keeping it, seven individual seeds became seven
types, and the sun became sunlight. One Korean omitted subject remains contextually
uncertain. All dimensions and raw outputs remain in the detailed report; readable wording
does not erase semantic differences. This small old-fixture review is not a quality rate,
blind study or publication approval. No 7B output exists to score or compare.

## Retained evidence and limits

Cloud-only evidence roots (not paths on the user's PC):

- `/workspace/scratch/hy-cpu-comparison-49a9/comparison-manifest.json`: frozen inputs.
- `/workspace/scratch/hy-cpu-comparison/diagnostic-results-49a9`: create-only invocation,
  result, stdout and stderr files, including the safety stop and all unstarted records.
- `/workspace/scratch/hy-cpu-comparison/review-49a9`: 32-row `report.json`, resource CSV,
  per-case semantic notes and complete `REPORT.md`, with original byte counts/hashes.
  Report JSON SHA-256: `b762b4d132bd37d3df61f949e8b0fd3cad14d1ae347ed11e9b67b5e5000a5e68`;
  Markdown SHA-256: `6ec3c091967c3f9c8403fc7f7b458086a971f7e189faeb89f5a2d1329e4f2943`.

The review verified 100 recorded input hashes and all 34 raw output-stream hashes. The
earlier runtime-build preparation note contains an obsolete manifest hash; the actual run
and this record bind the manifest stated above. Original evidence was not overwritten.
Models, runtimes and build caches are outside Git. No Windows App resident worker, complete
song, live playback, new admission policy or user GPU was exercised by this diagnostic.
The actual publication approval remains pending.
