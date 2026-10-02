# M2M100 1.2B direct candidate QA

Candidate-only Windows evaluation; no App catalog/default or publication change. This preserves the failed Marian experiment and tests a different official model through the same CT2 engine. The older 418M ONNX experiment is not evidence about this 1.2B CT2 model.

The official model is `facebook/m2m100_1.2B`, revision `7b36184180524c1a1bbfa37f120a608046250b98`, MIT, ungated. Original PyTorch weights are 4,958,230,644 bytes with SHA256 `a58ef8f42362ef12adeddc600b3425f1e2bbd019cfa6aae6b0051e2e3e055cd4`. Exact source file hashes are in models.json. No official safetensors artifact exists in that revision. The fixed dependencies are reused from ../marian-model-qa, without new package versions.

Run `Run-WindowsM2mQa.ps1` under Windows x64/Python3.11.9/.NET10.0.401. The independent workflow only triggers on branch qa/m2m-031 and its own experiment files, with read-only permission and cancel-in-progress false.

Before conversion, record available Windows RAM/disk and require at least10GiB/18GiB. Conversion uses two sequential processes: ZIP checkpoint verification followed by explicit weights-only Transformers loading and sharded safetensors saving; then a new process verifies those shards and performs CT2 int8 conversion. Pinned Transformers4.57.1 uses meta initialization and memory-mapped ZIP tensor loading. Non-ZIP checkpoints fail instead of taking an unbounded load fallback. Conversion is build work and is excluded from inference resource claims. Each build phase records actual peak RSS/private bytes and has a 10GiB sampled stop threshold and 900-second deadline. These build limits do not raise the inference cap.

Inference retains the production Windows Job's3GiB cap and one-process limit, four CT2 threads,60seconds per source-target group, and an active180second combined cold/warm target deadline. The direct base interpreter is hashed and runs with -I -S -X utf8; only the locked QA venv site-packages are added, without .pth execution. No HTTP service or outbound inference socket is used. Cleanup has an independent10second observation bound and unconfirmed cleanup blocks subsequent work.

Source language tags are supplied by the unchanged fixture. M2M100 source-language tokens and per-row target prefixes follow the official CT2 protocol. Every direction goes directly to the target, eliminating the English pivot. The official model uses zh, with no separate simplified-Chinese token; actual Chinese script and meaning must be reviewed before any adoption. Outputs are not silently normalized. Raw token arrays, exact text, native exits, CPU/RSS/private memory and cold/warm results are retained.

The original12screen IDs run first. Technical failure prevents the larger fixtures. Technical success permits the unchanged48-line song and frozen12-line benchmark, each in both targets with cold/warm decode, followed by source-only and real decode cancellation checks. This sequence is evidence collection, not automatic semantic approval. The frozen benchmark has now been reviewed on older candidates, so a fresh independent holdout is still required later. No prompt/source sentence is tailored to observed mistakes.

Int8 parameter storage is expected to be roughly1.2–1.5GB before runtime overhead, embedding storage and activations; this is not a peak-RAM promise. The3GiB cap remains unchanged and the actual Windows result decides feasibility. The first model conversion and inference remain unverified until CI runs. Content status stays PENDING SEMANTIC REVIEW.

Primary references: https://huggingface.co/facebook/m2m100_1.2B and https://opennmt.net/CTranslate2/guides/transformers.html#m2m-100
