# Beta17 optimization release record

Status: committed source-authorization and release-preparation record. At the time this record was written, the candidate had not yet completed a full Windows App/XAML build or been published. Actual execution and publication results belong to the same-source-tree Windows producer's immutable Actions receipt, final `runtime-publication.json`, checksums, update manifest and release attachments. This preparation document does not claim those later steps passed and does not require a source edit after the producer runs.

## Scope and retained evidence

The owner explicitly requested stopping further optimization and releasing the completed work plus the already integrated CUDA PR #102. Latest public App release was checked as `v0.3.1-beta.16`; the next release is `v0.3.1-beta.17`. Existing assets remain immutable. No DLC/Bilibili work is included.

The complete implementation and existing focused results remain in [the initial optimization report](performance-optimization-2026-10-07.md) and [the continuation report](performance-continuation-2026-10-07.md). Reuse those results without relabeling Linux measurements or managed shims as Windows presentation evidence. Reduced caller occupation does not imply equal improvement in total duration, SQL throughput or App frame rate.

## Approval and source boundary

The fresh [owner decision](../../scripts/ai-model-qa/evidence/v0.3.1-beta.17-owner-decision.json) records the actual publication authorization and required acceptance. Release preparation binds the current code-owned source fingerprints to Beta17 in a new review; previous approval/review records remain unchanged. All model/runtime/prompt/sampler/fixture/limit identities are unchanged, `semanticApproved` remains false, and historical models remain unverified.

The later [final source-review supplement](../../scripts/ai-model-qa/evidence/v0.3.1-beta.17-final-owner-decision.json) binds the actual Beta17 producer, previously bundled language-asset reuse, same-tree PR validation receipts and publication promotion as four additional code-owned fingerprint inputs. It reviews the source gate itself and these four inputs in a fresh final review. The original Beta17 preparation decision, review and evidence remain byte-for-byte intact; the supplement does not turn static release-protection checks into Windows execution evidence.

The subsequent [asset-reuse supplement](../../scripts/ai-model-qa/evidence/v0.3.1-beta.17-asset-reuse-owner-supplement.json) reviews one final guard: main packaging with neither a verified cache nor the PR-retained language asset fails before another Beta16 download. Both verified reuse fast paths and the PR's initial static extraction remain available. The active review is fresh, and both earlier review stages remain byte-for-byte intact.

PR #102 is included from verified head `b9bfde8f9d2473b16aa155266af64de3ea4198b6`; its original integration was complete across all 13 files. The publication version pin is subsequently changed by the existing preparation script for the new exact App version. #100 website corrections and #101 resource-release filtering remain included. The fixed component ZIP is 540873572 bytes, SHA256 `79e8deb4f8c35e7c9c94da0efa0752826bafe510fcb1e54da23062f2d6f40a0b`; its bytes and installed-cache identity are unchanged. Do not redownload, rebuild or upload it for Beta17.

## Required actual release evidence

The producer receipt and release attachments, bound to the exact source tree/commit and actual bytes, must supply the following evidence before publication. They remain separate from this preparation-stage source document; old evidence and a source-only review cannot qualify new binaries.

- Final source commit and precise merge/tag relationship.
- Complete Windows App/XAML build and minimum affected-path validation results, including any failures and their fixes.
- Real portable, installer and MSIX identities; exact runtime/model/native/license payload checks and compiler-input binding.
- Small CUDA descriptor bound to the exact App commit/tag, preserving trusted independent component identity.
- Update manifest and checksum results, runtime-publication binding, protected publication decision and immutable uploaded inventory.
- Public release/download/update-entry verification and confirmation that published artifacts match the final source.

These were pending at preparation time. No current Windows build, final executable run, installer lifecycle, real playback/drag animation, new GPU/model quality test or public artifact validation is claimed by this document. The owner requests reuse of unchanged model/GPU evidence and no unrelated broad retesting; unavailable manual behavior remains explicitly unverified. Later release verification must use actual producer/publication receipts, rather than infer a pass from this source document.
