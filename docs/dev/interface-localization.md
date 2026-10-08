# Interface localization and publication contract

`localization/languages.json` is the single offline definition of language codes, native names,
immutable App enum values, matching rules and product terms. Support exactly en-US, zh-CN,
zh-TW, ja-JP, ko-KR, de-DE, fr-FR, es-ES, pt-BR and ru-RU. Use native names, Taiwan terminology
and Brazilian Portuguese. Character conversion or copying English is not completed translation.

## Permanent lyrics boundary

Interface language and lyric target are independently saved. The lyric target is permanently
**Simplified Chinese or English**, forwarded in the existing Chinese/English target formats.
Upgrade stores the previously effective target once, without migrating lyric configuration.
Interface switching refreshes UI without retranslation; target switching reuses established
cancellation and stale-result isolation. Never pass new interface locales into lyric/AI paths.

Only settings labels/bindings and necessary target forwarding may change. Preserve sources,
queries, matching, sorting, fallback, timeout, language detection, provider translation priority,
AI admission/skipping, model, prompt bytes, protocol, segmentation, inference, scheduling,
cancellation, async/per-line results, cache keys/versions/data, acceleration, model downloads
and independent components. Do not add target extensibility, model capability lists, cache
cleanup, unrelated refactoring or model evaluations. Lyrics, AI output, prompts and protocols
are not UI resources. Report the exact conflict and wait for the owner if a change requires
crossing this boundary; do not publish a known broken lyric path.

## Resources and runtime

Keep existing `.resw` and static-template/JSON systems: App strings under
`src/DropSpace.App/Strings/<code>/Resources.resw`, installer UI under
`installer/localization/<code>.isl`, website stable semantic IDs under
`website/_source/src/locales/<code>.json`. Ship every language offline in Installer and Portable,
without language DLC. English runtime fallback avoids blank UI but is not a completed translation.
Keep System=0, English=1 and SimplifiedChinese=2 stable; unknown settings recover safely.
Match ordered system/browser preferences by exact code, explicit Chinese script/region rules,
language aliases, then English. Refresh windows, tray/menu, dynamic prompts and accessibility
together. Allow long text to grow/wrap and use system font fallback; do not shrink all type.
Locale-format dates/numbers/units without changing data. File/song names, brands, shortcuts,
protocols and diagnostics keep actual data with narrow exceptions in `localization/policy.json`.

## Website and release content

Each content page has one canonical URL for all languages. Home and changelog may differ.
Saved manual language wins, otherwise match browser preferences then English; never use IP.
Switch immediately and persist across pages/reloads while tolerating unavailable storage.
When storage rejects a manual choice, keep it in the current page's history state for refresh
and pass it through internal links for navigation. Preserve unrelated history state, query
parameters and fragments, and remove the temporary language hint on arrival.
Legacy `/en/`, `/zh-cn/` and their changelog entries select the link locale and redirect to the
corresponding unified page with query/fragment preserved. Preserve static-showcase navigation
and resource paths. Default HTML provides usable English body, navigation and downloads without
scripts. Initialize early; update UI, accessible attributes and document language together.
Keep canonical/sitemap entries unique, no nonexistent language alternates and no promise of ten
separately indexed languages at one URL.

New version summaries, changelog bodies and highlights have one English source bound to the
actual release tag, rendered with `lang="en"` in every interface language. Localize surrounding
UI; preserve history, download URLs, hashes, channels and updater contracts. Static and runtime
release data share that source: no former English/Chinese override or old highlights under a
new tag. Publish App assets before advertising them, then verify real downloads/Beta channel/site.

## Incremental review and integrity gate

1. Extract changed IDs: `node scripts/check-localization.mjs --extract --scope app|website
   --language <code>` reports pending IDs without changing records.
2. Update only affected translations; inspect meaning, terms and related layout.
3. Confirm read/revised IDs: `--review --scope app|website --language <code>
   --keys-file <explicit JSON ID array> --reviewer <identity and review scope>`.
4. Run `node scripts/check-localization.mjs` or `scripts/Test-Localization.ps1` before integration.
   App build uses `--scope app`; website build uses `--scope website`, avoiding duplicate scans.
5. Inspect actual Portable/MSIX PRI once with `scripts/Test-PackagedLocalization.ps1`.
   Reused bundles verify their hash-bound `localization-publication.json` using
   `node scripts/check-localization.mjs --artifacts <directory>`.

`localization/reviews/<scope>/<code>.json` records each English source/translation SHA-256 pair.
Source edits make translations pending; translation edits require confirmation too. Missing,
empty, unconfirmed, stale or malformed required translations block publication. Never bulk-copy
English, delete pending records or refresh hashes without reviewing affected translations.
Confirmation requires explicit IDs and rejects unapproved English copies. Initial records cover
only supplied/reviewed initial resources, not future blanket refreshes.

The existing XML/HTML parser and native JSON parser check duplicate/empty IDs, placeholders,
required markup, references, manifest/menu/resource consistency, script/CSS text bypasses, unified
URLs/old links and language markers. Exceptions specify exact IDs/values or file expressions
with reasons, not broad product-file exemptions. English release bodies and lyric/model internals
are separate domains. Failure identifies locale, ID, file and problem. A pass establishes
structure/completeness, **not automatic semantic correctness**.

The installer framework's standard wizard translations come from the existing SHA-256/signature-
pinned Inno Setup 7.0.2 package. Repository-owned ISL language names, CustomMessages and any
future Messages overrides belong to the App resource gate and reviewed fingerprints; an override
cannot silently fall back to English. Preserve Inno `%1`/`%2` variables and `%n` newline markers.
The main installer script's `[Messages]` and `[CustomMessages]` sections must remain empty;
put repository-owned entries in reviewed locale `.isl` files. The [installer gate](installer-localization-gate.md)
uses the pinned compiler's distinct section-specific percent/newline rules for both checking
and confirmation, including `%1` versus `%%1` and `%n` versus `%%n`.

## Focused verification and publication

Keep one project-wide passive ledger: at most 20 actual functional cases/scenarios including
failures, retries, languages and workflows. A development/release request or manual release
refresh does not authorize broad diagnostics. Do not resume full suites, ten-language browser
matrices, repeated screenshot sweeps or model tests. Resource entry counts are separate:
compilation, source/resource integrity, static package inspection, hashes and publication/API
readback are non-functional production checks under the existing policy. Verify focused
compatibility/matching, representative layouts/old links, English release exceptions and package
inclusion. Review the lyric-boundary diff without full lyric/model suites. Report blockers if
crossing that boundary or exceeding the budget would be necessary.

Read latest main, PRs, tags and actual release state before work and before choosing the next Beta.
Reuse completed work/exact-source bundles; preserve unrelated tasks/tags; increment the actual
latest version. Use existing signing, packages, updater manifest and independent components;
do not rebuild unrelated components. A commit or started workflow is not publication success.
Report permission/credential/external failures with completed work and concrete blockers.
