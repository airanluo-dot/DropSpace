# DropSpace Website

The official ten-language static website for [DropSpace](https://github.com/airanluo-dot/DropSpace) is generated from `website/_source`. Generated production files exist only in the Pages artifact and are never committed to `main`.

## Pages and language selection

The official homepage is `https://airanluo-dot.github.io/DropSpace/`; its changelog is `https://airanluo-dot.github.io/DropSpace/changelog/`. Each content page has one canonical address shared by all ten languages. Canonicals and the sitemap list only these pages; there are no language-specific `hreflang` promises.

`localization/languages.json` is the shared App/website source for supported language codes, native names, matching rules and product terms. English source strings and complete translations live in `src/locales/<language>.json`; templates bind stable semantic resource IDs through `data-i18n` and `data-i18n-attrs`. Reword existing resources without replacing their IDs.

The runtime uses a saved manual language first, then walks `navigator.languages` in preference order, matching an exact code before language/script/region fallbacks. Traditional and Simplified Chinese remain distinct. English is the final fallback. The selector displays native names and changes text, accessibility attributes, controls, dates and the page language immediately.

`src/localization-runtime.js` is bundled with all ten resources as a content-hashed, local asset. An early initialization limits wrong-language flashes; a bounded visibility fallback keeps interrupted loads usable. Browser storage failures do not prevent switching. In that case internal navigation carries a temporary `ds-language` query parameter, which the destination consumes and removes. The page's canonical address is unchanged. Without JavaScript, complete English content, navigation and download links remain available.

Legacy `/en/`, `/zh-cn/` and their `changelog/` pages remain compatibility documents. They select the old link's language and forward to the matching unified page, preserving query parameters and fragments. They are excluded from the sitemap and carry unified canonicals. Both directory routes and explicit `index.html` legacy documents work on Pages.

## Content boundaries

Evergreen features, interface labels, demonstration controls and accessibility text use the ten resources. Sample file names, copied user content, song names, original sample lyrics, brand names and fixed protocol identifiers keep their actual content. No lyric body, generated translation, model instruction or AI protocol belongs in the interface resource catalogs.

The lyric story describes two permanent translation targets: Simplified Chinese and English, chosen independently of the interface language. The illustration does not perform website AI inference. Only its interface explanation and controls are localized; the original sample lyrics and translated sample pair remain unchanged.

Latest release summaries, all newly authored release notes and version highlights have one English body, marked `lang="en"`, whichever interface language is selected. Existing release history is retained. Both static snapshots and runtime updates read the validated release API and `createLatestChangeApi`; highlights remain attached to their own release tag. The `latest-change.json` compatibility `en` and `zh-CN` fields both carry the same English content. Column headings, buttons and channel labels are interface resources. Main downloads continue to follow Stable; Beta stays available through its existing channel and release history.

## Build and focused verification

```bash
npm ci
npm run sync-releases
npm run build
```

Both `build` and `build:static` execute the lightweight localization completeness gate before generating output and verify route/resource contracts in the built artifact afterward. The gate uses the shared language catalog and reviewed source fingerprints. Missing, empty, stale or placeholder-invalid required translations block publication; English runtime fallback is never counted as a completed translation. It also checks references, XML/JSON, native menus, packaging consistency, unified routes and new visible text that bypasses resources. Failures identify language, resource ID, file and issue. Structural completeness does not establish translation semantic quality.

For subsequent copy changes: extract new/changed text, update only affected translations, record a genuine revision or explicit per-resource confirmation, run the completeness gate, and review changed wording and affected layouts as needed. Never copy English into translations or refresh fingerprints in bulk to clear stale entries. Release bodies and explicit fixed/sample-content exceptions are excluded from ten-language completeness.

Minimize testing, retaining necessary verification for the actual change and concrete risks. A development or publication request does not authorize unrelated full Node/Playwright suites, broad language-by-page matrices, repeated screenshot sweeps or model evaluations. Resource entry counts are not test-case counts. Follow the [focused testing policy](../../docs/dev/passive-test-budget.md); reuse passed checks when their relevant inputs are unchanged.

Production sync is fail-closed: `sync-releases` must fetch and validate authoritative GitHub Releases before deployment. Network, HTTP, JSON, contract, Stable-release or required-asset failures retain the previous successful Pages deployment. The committed `data/releases.json` is only a local/PR fixture and is never a production fallback. Asset names are content-hashed, Pages deployment is atomic, and the existing per-document CSP remains enforced.

## Existing illustrations and static showcase

The native Island, widgets and music stories retain the existing shell, palette, backgrounds, borders, shadows and motion. The four-page showcase responds to pointer and keyboard selection without autoplay or wraparound. The lyric demo keeps its fixed logical 560 × 340 geometry, original sample audio and lyrics, playback consent, reduced motion and forced-colors behavior. It does not expose lyric font-size controls.

`npm run build:static` creates `dist-static/` using the same template, complete ten-language resources and a unified `/index.html` homepage. It excludes live release polling, version stamps, latest-change content, changelog snapshots and release JSON endpoints. Its navigation uses explicit files, so hosts need no directory-index rewrite. Legacy `/en/index.html` and `/zh-cn/index.html` redirect to the unified file; release-history links lead to GitHub. The default origin remains `https://dropspace-static.arenvox.chatgpt.site`; `SITE_ORIGIN` can override it. Generating this artifact does not publish a Site or change sharing.
