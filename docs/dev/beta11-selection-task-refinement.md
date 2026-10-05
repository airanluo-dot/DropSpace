# Selection refinement after owner authorization

The owner explicitly chose continuing all three selection modes and authorized longer diagnostic time on October 5, 2026. Publication remains conditional on actual valid selection evidence; the rule-only branch is preserved as a backup, not accepted scope.

Two suspected runtime defects were checked before altering native code:

* The pinned Q8 GGUF revision `a0c709d9fac510f2c807aa3af52872340dc37a4a` contains `tokenizer.chat_template`. Its official Hunyuan user/assistant boundaries match the current official tokenizer template. Only the bounded 8MiB public header range was read; no replacement weights were downloaded.
* Pinned llama.cpp `7fe450e19305b828c199d602c23a8337aaa1f03b/common/chat.cpp` sets `has_explicit_template=true` when the model contains a chat template. Thus this helper's `common_chat_templates_was_explicit` branch applies it; "no CLI template override" does not imply plaintext continuation. EOS ID 120020 matches official model/generation configuration and tokenizer's end-of-message token; no EOS override is justified.

Primary references: [official model card](https://huggingface.co/tencent/Hy-MT2-1.8B), [official chat template at inspected revision](https://huggingface.co/tencent/Hy-MT2-1.8B/blob/9a341cd1b679d3efd23b46e847b01745a71ed792/chat_template.jinja), [pinned upstream chat code](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/common/chat.cpp). The model is translation-specialized; the card's translation instruction capability is not evidence that it can rank recordings or abstain.

The next explicitly nonproduction diagnostic reuses the exact original worker/model/manifest and changes only the **selection diagnostic task** to a concise Chinese metadata comparison, accepting an entire candidate ID or literal NONE. It does not modify the translation prompt or production selector. Candidate order is reversed in all three fixtures: positive answers move behind wrong-artist/wrong-version alternatives; unidentified Hello must still abstain. Expected IDs do not shape the prompt. No rules pick an AI answer and no ID-prefix salvage is permitted. A native completed response is required for a pass.

`selection_diagnostic_variant=refined` grants at most 30 seconds per diagnostic request; default `original` remains the exact original ten-second experiment. The three-second provider lookup and 500ms production budgets remain unchanged until effective semantics and latency are observed and a justified asynchronous policy is implemented. Original provider lyrics already publish before selector inference, with generation/settings/track/token fences; those fences must also cover any later change.

No native rebuild, new model, paid API, local laptop or larger benchmark is requested by this experiment. Complete-original NetEase response reuse from the independent P2 fix is now also preserved on the full branch (`112b250`), while the generic title fix remains present. Original failed evidence is untouched. Template/EOS observations alone are not successful model validation.
