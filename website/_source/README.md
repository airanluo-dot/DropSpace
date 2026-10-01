# DropSpace Website

Official bilingual static website for [DropSpace](https://github.com/airanluo-dot/DropSpace). Source and build tooling live in `website/_source`; generated production files exist only in the GitHub Pages deployment artifact and are never committed to `main`.

## Routes

- English: `/en/`
- 简体中文: `/zh-cn/`
- Localized changelog: `/<language>/changelog/`

These are independent generated HTML documents. The language switch is a normal link and does not replace strings at runtime.

## Build and verification

```bash
npm ci
npm run sync-releases
npm test
npx playwright install chromium
npm run test:browser
```

Production deployment is fail-closed: `npm run sync-releases` must fetch and validate the authoritative GitHub Releases response before the build can continue. Network, HTTP, JSON, contract, Stable-release, or required-asset failures exit non-zero, so GitHub Pages keeps the previous successful deployment. Production never falls back to the committed `data/releases.json`.

The committed `data/releases.json` is only a local-development and pull-request fixture. `npm test` validates it before building. An explicit offline validation can be run with `node scripts/sync-releases.mjs --fixture data/releases.json --validate-only`; fixture mode is rejected when `NODE_ENV=production`.

CSS, JavaScript, brand assets, screenshots, and demo media receive content-hashed filenames so each Pages artifact is internally consistent.

GitHub Pages does not expose arbitrary response-header or cache-rule configuration. The build therefore uses a strict per-document CSP meta policy, immutable versioned asset URLs, static redirects, a custom 404 page, and an atomic Pages artifact deployment. Moving to a host with response-header controls would allow the same CSP to be enforced as an HTTP header.

## Native Island homepage stories

The homepage's native-island, widgets and music sections share the original
shell, dark palette, section typography and gradient accents. Their illustrations
use labeled sample content, not live desktop/audio data. The four-page showcase
supports pointer and keyboard selection without autoplay or wraparound. Maintain
English/Simplified Chinese copy together in scripts/i18n.mjs. Browser coverage in
tests/native-showcase.spec.mjs checks tab behavior, locale, download links,
eight widget tiles and mobile overflow. Website-only publication uses the Pages
workflow; do not create another App release for presentation changes.

Visual consistency is mandatory: the new feature stage reuses the original
mode-screen background, native surfaces reuse its expanded-island background,
border and shadow, and page transitions reuse the original .55s easing and
popover entrance geometry. Browser tests compare computed appearance/motion
against those existing elements and verify reduced-motion behavior.

## AI lyrics story

The evergreen bilingual `#ai-lyrics` section uses the established site layout,
colors, typography and spacing. Its copy explains App-language translation,
provider-translation priority, optional manual model download, local/offline
inference with available lyrics, accuracy limitations and the
Off / AI / Music glow modes. It does not hard-code release numbers, model sizes,
benchmarks or unsupported language guarantees.

`src/lyrics-demo.js` ports the signed-distance geometry, three broad overlapping
ribbons and orange/pink/blue palette from the native `IslandGlowRasterizer`.
Only exterior pixels are lit; the Island's physical silhouette remains fixed.
The Island is a fixed logical 560 × 340 surface, matching the App reference.
On narrow screens, its whole container scales proportionally, including fonts,
corners and halo. Interactive controls stay outside the scaled illustration. Long lyrics remain
inside a keyboard-scrollable, height-bounded viewport; resizing lyrics never
changes the physical Island or pushes into the playback artwork.
The website uses fixed lyric typography. It deliberately has no font-size
controls or font-size input handlers; App font settings are separate.

Music brightness uses the same analytic envelope as the original sample audio,
clocked by `audio.currentTime` during playback. Audio never autoplays; animations
pause offscreen and respect reduced motion and forced colors. The illustrative
AI mode shows the working glow, not actual website AI inference.

Regenerate the small original demo audio with
`node scripts/generate-demo-audio.mjs`. No third-party recordings are used.
`tests/ai-lyrics.spec.mjs` checks both locales, controls, transparency, fixed
geometry, fixed typography, audio consent, mobile layout and reduced motion.

## GPT Site static sync

`npm run build:static` creates `dist-static/` for the existing public static
showcase. It reuses the same source/layout and localized feature illustrations,
while excluding live release polling, version stamps, the latest-change section,
changelog snapshots and release JSON endpoints. Downloads use official GitHub
latest-release asset links; release-history links lead to GitHub. The normal
`npm run build` output and updater-facing API are unchanged.

The default static origin is `https://dropspace-static.arenvox.chatgpt.site`.
`SITE_ORIGIN` can override it for a deliberate migration; generating this artifact
does not publish a Site or alter its access. Keep the existing Site project and
public sharing when synchronizing it. Root release coordination owns publication.

Browser tests support `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH` when the environment
provides its own compatible browser. The static test server uses port 4174;
the official Pages preview remains on 4173.
