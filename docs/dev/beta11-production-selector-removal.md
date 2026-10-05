# Production AI selection removed; AI translation retained

The user authorized cancelling AI selection if a reliable implementation could not be established (2026-10-05 08:58:35 UTC, `Sentinel_5a84dc5df360819199790abab2a789c1`). This supersedes the previously pending three-mode product proposal. The measured existing profiles do not establish a reliable, low-latency verifier. This is a decision about the available qualified implementation, not a claim that no future verifier is possible.

## Shipping change

- The music settings page removes both AI selection choices and their descriptions. There is no pending/fallback AI selection setting in the interface.
- `AiLyricsService` no longer creates, prepares, configures, retires or calls a candidate selector. `MediaExperienceService` always requests the strict rules path and removes the later selection/publication phase. The shipping `PlainHyLyricsBackend` implements only translation, not `ILyricsSelectionRuntime`.
- Legacy `SelectionMode` and `AiSelectionModelId` JSON fields are ignored and no longer written. Validation forces rules. Their ignored in-memory compatibility slots are retained solely for isolated diagnostic code; they do not trigger reloads or enable production inference.
- The two existing Hy translation models, frozen translation prompts, GPU preference, translation opt-in, native translation preference, immediate trusted originals, bounded native search, source caching and stability fixes remain. No privacy text or reviewed-source override was changed.
- Four App test source files for the removed production selector API are preserved as historical fixtures and excluded from compilation because those APIs no longer exist. Source/provider and translation tests remain. Experimental Core/Infrastructure/native selector protocol fixtures are retained as diagnostic code, with no production caller through the shipping backend.

## Final source-evidence feasibility round

These findings came from adapter/source review, without additional music API requests:

| Source | Evidence already available | Limitation |
| --- | --- | --- |
| NetEase | Artist IDs/names and source-attached aliases; canonical and alias titles | Adapter flattens artist aliases and drops IDs/raw canonical title. These fields alone do not resolve arbitrary unsupported names. |
| QQ | Singer identities and songmid/songid | Adapter drops raw singer IDs. A generic community QQ ID is not assumed to be songmid merely from its shape. |
| AMLL | Community document IDs and platform references, ISRCs | Community references are not proof of performer/version identity; no audio duration is supplied. Contributors are not performer aliases. |
| Kugou | Hash-bound catalogue and independently matched duration | Duration can fill a missing source value only after recording agreement; hashes must stay in their own namespace. |
| LRCLIB | Source entry ID, artist strings and duration | No structured performer IDs or native translation endpoint. |

The AMLL fields are documented in the upstream [native API schema](https://github.com/amll-dev/applemusic-like-lyrics/blob/main/packages/docs/src/content/docs/reference/http-api/native.mdx) and [HTTP API overview](https://github.com/amll-dev/applemusic-like-lyrics/blob/main/packages/docs/src/content/docs/reference/http-api/overview.mdx). Other findings are from the five checked-in provider adapters. Capturing source evidence would require no extra requests, but it does not qualify model decisions over a complete production snapshot.

`beta11-recording-evidence-research-prototype.patch` preserves the last unqualified research prototype for review. It contains bounded namespaced recording facts, structured credits/title/duration provenance, optional provider capture and an ordered, citation-checked assessment contract. **The patch is not applied to shipping code.** Its Core/Infrastructure compilation succeeded before archiving; no actual model inference or profile qualification ran against it. Existing 0.6B/4B/8B diagnostics remain historical evidence in their readouts. No new model download, paid API, prompt tuning or provider traffic was used here.

## 【不准测试】 — completed historical checks only

The parent forwarded the later explicit instruction forbidding tests. No further unit/integration/regression checks, model runs, benchmarks or test CI may be started. The following checks had already completed before that instruction; they are retained as evidence, not repeated or broadened. No new test execution is a release prerequisite. Necessary compilation/packaging and manual code review remain permitted.

## Checks completed before the explicit prohibition

1. One Core migration case passed for both Hy model choices: obsolete settings cannot reactivate selection, disappear on serialization, preserve translation/GPU/source preferences and do not trigger a reload; a real translation model change still triggers one. Log: `/tmp/dropspace-selector-removal-migration-check.log`.
2. One portable check links the actual App `AiLyricsService` and invokes the existing real NetEase payload/old native translation cache admission case. It passed without model/cache preflight. Log: `/tmp/dropspace-selector-removal-app-check.log`. This compiled Core, Infrastructure and the actual App service through a portable harness, not the complete Windows UI application.
3. Diff whitespace and both resource XML files were checked. No broad or repeated suite was run.

The parent release task still owns a necessary combined Windows App build, version/tag/release, and any remaining independent translation optimization integration. Fast switching, GPU behavior and current native crashes remain unverified on the user's Windows installation. The separate native lifetime repair does not prove the cause of today's crashes; the available runtime fatal-report symbol is not the originating bad access. No claim is made that every upstream rate limit has been solved.
