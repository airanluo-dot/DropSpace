# Optional Hy-MT2 7B Q8 profile — 2026-10-02

The default remains Hy-MT2 1.8B Q8_0. The only added selection is Tencent Hy-MT2 7B Q8_0, downloaded through the existing explicit-consent flow. No model was downloaded onto the user's computer, and no GPU workload was run for this change. This document records source, policy, build and limited cloud CPU diagnostic evidence; it does not establish 7B model-quality acceptance.

## Frozen official artifact

| Field | Value |
| --- | --- |
| Catalog ID | `hy-mt2-7b-q8-plain-beta` |
| Repository | `tencent/Hy-MT2-7B-GGUF` |
| Revision | `ab8472660ac61fac25f1af43fac2599d52a8a775` |
| File | `HY-MT2-7B-Q8_0.gguf` |
| Bytes | `7,981,928,896` (7.98 GB; about 7.434 GiB) |
| SHA-256 | `58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0` |
| License | Apache-2.0, including Tencent's model-specific copyright preamble |
| License bytes / SHA-256 | `11,635` / `746750afa6af28fe4f8b326751ad2a40c700d2e5c459c0a1f6a2e76d99ace224` |

The official [LFS pointer](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/raw/ab8472660ac61fac25f1af43fac2599d52a8a775/HY-MT2-7B-Q8_0.gguf) and Hugging Face model API agreed on file size and SHA-256. The complete official 1.8B and 7B files were subsequently downloaded once each in the cloud and independently verified against their pinned SHA-256 values, including the 7B identity above. See the [cloud CPU diagnostic report](hy-cpu-cloud-diagnostic-2026-10-02.md). Installation also verifies the entire downloaded file before activation.

The complete [upstream license](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/blob/ab8472660ac61fac25f1af43fac2599d52a8a775/LICENSE.txt) was read and is preserved unchanged in [the local license copy](../licenses/tencent-hy-mt2-7b-gguf-LICENSE.txt). The pinned repository tree did not contain a separate NOTICE file. No license terms were inferred solely from model-card tags.

## Header and pinned runtime evidence

The initial header inspection used two strict HTTP 206 range responses covering bytes 0 through 8,388,607. The GGUF metadata and tensor directory end at byte 7,518,912; that inspection retained only the prefix in scratch. It preceded the complete cloud downloads and limited CPU diagnostics recorded below.

The inspected file is GGUF v3, `hunyuan-dense`, with 32 layers, hidden dimension 4096, 32 attention heads, 8 KV heads and head dimension 128. Its 354 tensor descriptors comprise 225 Q8_0 tensors and 129 F32 tensors. There are no STQ tensor types in this exact file, so the general model-card warning about STQ kernels is not evidence that this Q8_0 file requires an STQ runtime update. The tokenizer uses the `hunyuan` pre-tokenizer, BOS 127958, EOS 3 and EOT 127960; no EOS override was added.

The existing pinned llama.cpp revision `7fe450e19305b828c199d602c23a8337aaa1f03b` contains:

- [`hunyuan-dense` architecture registration](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/src/llama-arch.cpp), a model factory entry, and [dense model support reusing Hunyuan VL tensor and graph code](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/src/models/models.h).
- The [`hunyuan` tokenizer implementation](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/src/llama-vocab.cpp).
- [Q8_0 quantization/dequantization](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/ggml/src/ggml-quants.c) and [Q8_0 Vulkan kernels](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/ggml/src/ggml-vulkan/ggml-vulkan.cpp).

This supports source-level integration feasibility. It does not certify that the packaged Windows worker successfully loads this exact file on a particular driver/device. The private helper changed, so the normal runtime build must rebuild its binaries and recompute its existing source/binary manifest identities. An older worker cannot certify the new 7B resource profile.

## Model-specific resource and identity policy

The 1.8B profile keeps its existing default, exact prompt, sampler arguments, inference-identity serialization, 3 GiB job/working-set budget and GPU admission threshold. The shared inference gate still permits only one native inference owner.

The 7B profile has a separate 12 GiB process job/working-set ceiling. This is a bounded engineering allowance, not a measurement of successful full-model peak memory. At the unchanged 4096-token context, a basic F16 KV calculation is `32 layers × 4096 tokens × 8 KV heads × 128 dimensions × 2 K/V × 2 bytes = 512 MiB`; graph buffers, staging and driver allocations add overhead and must be measured in later native qualification.

Before starting a new CPU process, the managed host measures available physical RAM and available commit, under the shared inference gate and after the old owner has exited. Both must be at least the selected job ceiling plus 1 GiB: 4 GiB for 1.8B, 13 GiB for 7B. Unavailable readings or either resource below its threshold reject CPU startup with a local-resource outcome; source lyrics remain available. GPU-to-CPU fallback takes a fresh measurement, and an existing resident does not reapply a full-model allocation requirement for each line. The host reads Windows `GlobalMemoryStatusEx`; the commit reading is the host process's currently available commit, potentially below the system-wide value. This guard is not copied into the already job-limited worker. Vocabulary-only tokenization remains separate. These conservative admission values are not measured peaks and do not guarantee that a machine with 16 GB installed RAM can run 7B. Job limits and the runtime watchdog remain necessary because available memory may change after admission.

Native GPU admission for 7B requires current reported free GPU memory of at least model bytes + 2 GiB + `max(1 GiB, total / 5)`. For a 16 GiB adapter that threshold is 13,565,386,380 bytes (about 12.634 GiB) currently free. Having “16 GB VRAM” alone does not qualify the device. Integrated GPUs additionally need separately measured available host RAM of model bytes + 4 GiB. The existing supported-vendor/capability checks and no-silent-fit policy remain in place. Admission estimates do not guarantee successful inference or latency.

The model SHA-256 selects the larger budget; arbitrary, legacy or unknown identities cannot acquire that profile through the plaintext runner. The 7B worker command uses a fixed `--model-profile hy-mt2-7b-q8` value, verifies exact file length before model load, and must confirm that profile in its ready handshake. The managed package resolver still verifies full SHA-256. Changing model identity drains the previous process before starting a replacement; GPU-fallback state does not bleed across models.

Cache identities include the selected verified model hash plus the same runtime/protocol identity. Cross-model cache reuse and mismatched package ID/hash/identity are rejected. Language/source-translation admission remains ahead of model/cache/runtime activity.

Downloads keep per-hash partial/final files, 64-bit lengths and offsets, range validation, SHA-256 verification and atomic final rename. They do not copy the full model into a second install directory. Disk admission includes remaining bytes, runtime extraction allowance and existing 64 MiB margin, and is rechecked if a server ignores Range. Only the 7B download total deadline increases to two hours; 1.8B remains 30 minutes and per-read/header deadlines remain 45 seconds. No model is bundled with the app or automatically downloaded by this change.

## Cloud checks and remaining native work

Focused cloud results at this implementation checkpoint:

- Core model/settings/identity tests: 12 passed, 0 failed, 0 skipped. The old 1.8B inference identity for runtime hash `aaaa…` remains `f52dcf4ea70c653eaea13c67b51ad82ec5d80cd33b7554a684393111a638fec3`.
- Infrastructure plaintext/backend/resident/one-shot tests: 61 passed, 0 failed, 0 skipped. Synthetic processes cover 7B profile handshake, old/mismatched worker rejection, switching at the same path, cancellation and cache isolation; they load no model.
- Model-package tests: 19 passed, 0 failed, 0 skipped. Large-length/range cases use tiny synthetic streams rather than multi-gigabyte files.
- Native resource-policy test compiled with C++17 and `-Wall -Wextra -Werror -pedantic`, then ran successfully. It covers both models, exact size checks, GPU budget boundaries and integrated host-memory boundaries.

Windows native-worker rebuild and Windows build/package integration subsequently passed in both en-US and zh-CN matrices of [CI run 37013042584](https://github.com/airanluo-dot/DropSpace/actions/runs/37013042584), for exactly commit `49a9ac13dd3d596972d5ac8cabc8d7304171b584`. This evidence applies only to that SHA; any later candidate must rerun its own gates. Building and packaging the worker does not establish that it successfully loads or translates with 7B.

The [cloud CPU diagnostic report](hy-cpu-cloud-diagnostic-2026-10-02.md) records 16 completed 1.8B samples. The first 7B case was stopped by the cgroup 14 GiB safety threshold after 1.673 seconds, with a recorded cgroup memory peak of 15,152,271,360 bytes, process RSS of 3,582,099,456 bytes and zero stdout bytes. The remaining 15 cases were not started. No OOM event occurred. Cgroup memory and process RSS are distinct measurements; neither this stopped attempt nor the completed 1.8B samples certifies successful 7B loading, translation quality or full-load resource use.

Still unverified for 7B: successful model loading and generation, quality/style comparisons, 16 GB GPU behavior, successful full-load CPU/GPU memory requirements, Windows inference performance, time to first lyric, long-song completion and real cancellation/unload on that model. No GPU workload or operation on the user's computer was performed for these cloud diagnostics. Later validation must reuse the user's existing directories and explicitly downloaded artifacts.
